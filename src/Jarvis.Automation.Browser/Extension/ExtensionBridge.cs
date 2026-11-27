using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jarvis.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Automation.Browser.Extension;

public sealed class ExtensionBridge : IAsyncDisposable
{
    private readonly ILogger<ExtensionBridge> _logger;
    private readonly int _port;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _startLock = new();
    private HttpListener? _listener;
    private WebSocket? _socket;
    private int _nextId;
    private string? _markerPath;

    public ExtensionBridge(IOptions<JarvisOptions> options, ILogger<ExtensionBridge> logger)
        : this(ExtensionIdentity.LoadOrCreate(ExtensionIdentity.DefaultDirectory), options.Value.Browser.ExtensionPort, logger)
    {
        _markerPath = Path.Combine(ExtensionIdentity.DefaultDirectory, "connected.marker");
    }

    internal ExtensionBridge(ExtensionIdentity identity, int port, ILogger<ExtensionBridge> logger)
    {
        Identity = identity;
        _port = port;
        _logger = logger;
    }

    public ExtensionIdentity Identity { get; }

    public int Port => _port;

    public bool IsConnected => _socket is { State: WebSocketState.Open };

    public bool WasEverConnected => _markerPath is not null && File.Exists(_markerPath);

    public void Start()
    {
        lock (_startLock)
        {
            if (_listener is not null)
            {
                return;
            }

            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{_port}/");
                listener.Start();
                _listener = listener;
                _ = AcceptLoopAsync(listener);
                _logger.LogInformation("Extension bridge listening on port {Port}", _port);
            }
            catch (HttpListenerException ex)
            {
                _logger.LogWarning(ex, "Extension bridge could not listen on port {Port}", _port);
            }
        }
    }

    public async Task<bool> WaitForConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Start();
        var deadline = DateTime.UtcNow + timeout;
        while (!IsConnected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, cancellationToken);
        }

        return IsConnected;
    }

    public async Task<JsonElement> SendAsync(string command, object? args, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var socket = _socket;
        if (socket is not { State: WebSocketState.Open })
        {
            throw new InvalidOperationException("The Jarvis Chrome extension is not connected.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, cmd = command, args });
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }

            return await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(20), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"The Chrome extension did not answer '{command}' in time.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        if (_socket is { State: WebSocketState.Open } socket)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Jarvis is closing", CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex) when (ex is WebSocketException or TimeoutException)
            {
            }
        }

        _listener?.Close();
    }

    internal bool IsAuthorized(string? origin, string? token) =>
        string.Equals(origin, Identity.Origin, StringComparison.Ordinal) &&
        token is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(Identity.Token));

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            // Web pages can also open WebSockets to localhost; only the Jarvis extension with the right token gets in.
            if (!context.Request.IsWebSocketRequest ||
                !IsAuthorized(context.Request.Headers["Origin"], context.Request.QueryString["token"]))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                continue;
            }

            try
            {
                var webSocket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                var previous = Interlocked.Exchange(ref _socket, webSocket);
                previous?.Abort();
                _logger.LogInformation("Chrome extension connected");
                if (_markerPath is not null && !File.Exists(_markerPath))
                {
                    File.WriteAllText(_markerPath, DateTime.Now.ToString("O"));
                }
                _ = ReceiveLoopAsync(webSocket);
            }
            catch (WebSocketException ex)
            {
                _logger.LogWarning(ex, "Extension handshake failed");
            }
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open && !_shutdown.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, _shutdown.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                Dispatch(message.ToArray());
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _socket, null, socket), socket))
            {
                _logger.LogInformation("Chrome extension disconnected");
                foreach (var pending in _pending.Values)
                {
                    pending.TrySetException(new InvalidOperationException("The Chrome extension disconnected."));
                }
            }
        }
    }

    private void Dispatch(byte[] data)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var idElement) || !_pending.TryRemove(idElement.GetInt32(), out var completion))
        {
            return;
        }

        if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
        {
            completion.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
        }
        else
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown extension error.";
            completion.TrySetException(new InvalidOperationException(error));
        }
    }
}
