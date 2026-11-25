using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Jarvis.Automation.Browser.Extension;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.Core.Tests;

public class ExtensionBridgeTests : IAsyncLifetime
{
    private readonly ExtensionIdentity _identity = new("key", "abcdefghijklmnopabcdefghijklmnop", "secret-token");
    private readonly int _port = Random.Shared.Next(20000, 40000);
    private ExtensionBridge _bridge = null!;

    public ValueTask InitializeAsync()
    {
        _bridge = new ExtensionBridge(_identity, _port, NullLogger<ExtensionBridge>.Instance);
        _bridge.Start();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _bridge.DisposeAsync();

    private async Task<ClientWebSocket> ConnectAsync(string origin, string token)
    {
        var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Origin", origin);
        await client.ConnectAsync(new Uri($"ws://localhost:{_port}/?token={token}"), TestContext.Current.CancellationToken);
        return client;
    }

    [Fact]
    public async Task SendAsync_ReturnsExtensionResult_WhenExtensionAnswers()
    {
        // Arrange
        using var client = await ConnectAsync(_identity.Origin, _identity.Token);
        Assert.True(await _bridge.WaitForConnectionAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var answer = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            var received = await client.ReceiveAsync(buffer, TestContext.Current.CancellationToken);
            using var request = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
            var reply = $"{{\"id\":{request.RootElement.GetProperty("id").GetInt32()},\"ok\":true,\"result\":{{\"echo\":\"{request.RootElement.GetProperty("cmd").GetString()}\"}}}}";
            await client.SendAsync(Encoding.UTF8.GetBytes(reply), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        // Act
        var result = await _bridge.SendAsync("hello", null, TestContext.Current.CancellationToken);
        await answer;

        // Assert
        Assert.Equal("hello", result.GetProperty("echo").GetString());
    }

    [Theory]
    [InlineData("https://evil.example", "secret-token")]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop", "wrong")]
    public async Task Start_RejectsConnection_WhenOriginOrTokenIsWrong(string origin, string token)
    {
        // Act & Assert
        await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(origin, token));
        Assert.False(_bridge.IsConnected);
    }

    [Fact]
    public void ComputeId_ReturnsChromeStyleId_ForPublicKey()
    {
        // Act
        var id = ExtensionIdentity.ComputeId([1, 2, 3, 4]);

        // Assert
        Assert.Equal(32, id.Length);
        Assert.All(id, c => Assert.InRange(c, 'a', 'p'));
        Assert.Equal(id, ExtensionIdentity.ComputeId([1, 2, 3, 4]));
    }

    [Fact]
    public void Write_CreatesLoadableExtension_WithKeyTokenAndPageScripts()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "jarvis-ext-" + Guid.NewGuid().ToString("N"));

        try
        {
            // Act
            var target = ExtensionFiles.Write(_identity, 17345, directory);

            // Assert
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "manifest.json")));
            Assert.Equal(3, manifest.RootElement.GetProperty("manifest_version").GetInt32());
            Assert.Equal("key", manifest.RootElement.GetProperty("key").GetString());
            Assert.Contains("secret-token", File.ReadAllText(Path.Combine(target, "config.js")));
            Assert.Contains("window.__jarvis", File.ReadAllText(Path.Combine(target, "page-scripts.js")));
            Assert.True(File.Exists(Path.Combine(target, "background.js")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
