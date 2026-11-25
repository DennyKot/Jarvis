using System.Diagnostics;
using Jarvis.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

namespace Jarvis.Core.Llm;

/// <summary>Checks, installs and starts a local Ollama server and pulls the configured model.</summary>
public sealed class OllamaSetup(IOptions<JarvisOptions> options, ILogger<OllamaSetup> logger)
{
    private const string InstallerUrl = "https://ollama.com/download/OllamaSetup.exe";

    private readonly LlmOptions _llm = options.Value.Llm;

    public bool IsOllamaProvider => _llm.Provider == LlmProvider.Ollama;

    public string Model => _llm.Model;

    private Uri Endpoint => new(string.IsNullOrWhiteSpace(_llm.Endpoint) ? "http://localhost:11434" : _llm.Endpoint);

    private bool IsLocalEndpoint => Endpoint.IsLoopback;

    /// <summary>True when the server answers and the configured model is already pulled.</summary>
    public async Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient(TimeSpan.FromSeconds(3));
            var models = await client.ListLocalModelsAsync(ct);
            return models.Any(m => IsSameModel(m.Name, _llm.Model));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task RunAsync(IProgress<(string Status, double? Percent)> progress, CancellationToken ct)
    {
        if (!await IsServerRunningAsync(ct))
        {
            if (!IsLocalEndpoint)
            {
                throw new InvalidOperationException($"Ollama server at {Endpoint} is not reachable.");
            }

            if (FindOllamaExe() is null)
            {
                progress.Report(("Installing Ollama…", null));
                await InstallOllamaAsync(progress, ct);
            }

            progress.Report(("Starting Ollama…", null));
            await StartServerAsync(ct);
        }

        progress.Report(($"Downloading model {_llm.Model}…", 0));
        using var client = CreateClient(Timeout.InfiniteTimeSpan);
        await foreach (var update in client.PullModelAsync(new PullModelRequest { Model = _llm.Model }, ct))
        {
            if (update is null)
            {
                continue;
            }

            double? percent = update.Total > 0 ? 100.0 * update.Completed / update.Total : null;
            progress.Report(($"{_llm.Model}: {update.Status}", percent));
        }

        if (!await IsReadyAsync(ct))
        {
            throw new InvalidOperationException($"Model {_llm.Model} was not found after download.");
        }

        logger.LogInformation("Ollama setup finished, model {Model} ready", _llm.Model);
    }

    /// <summary>"gemma4" matches "gemma4:latest"; an explicit tag must match exactly.</summary>
    public static bool IsSameModel(string installed, string configured)
    {
        static string Normalize(string name) => name.Contains(':') ? name : name + ":latest";
        return string.Equals(Normalize(installed), Normalize(configured), StringComparison.OrdinalIgnoreCase);
    }

    private OllamaApiClient CreateClient(TimeSpan timeout) =>
        new(new HttpClient { BaseAddress = Endpoint, Timeout = timeout }, _llm.Model);

    private async Task<bool> IsServerRunningAsync(CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(TimeSpan.FromSeconds(3));
            return await client.IsRunningAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task InstallOllamaAsync(IProgress<(string Status, double? Percent)> progress, CancellationToken ct)
    {
        try
        {
            var exit = await RunProcessAsync("winget",
                "install --id Ollama.Ollama -e --silent --accept-package-agreements --accept-source-agreements", ct);
            if (exit == 0 && FindOllamaExe() is not null)
            {
                return;
            }

            logger.LogWarning("winget install of Ollama exited with {ExitCode}, falling back to the official installer", exit);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "winget is not available, falling back to the official installer");
        }

        var installer = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");
        using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
        using (var response = await http.GetAsync(InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(installer);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress.Report(("Downloading Ollama installer…", total > 0 ? 100.0 * done / total : null));
            }
        }

        progress.Report(("Installing Ollama…", null));
        var code = await RunProcessAsync(installer, "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES", ct);
        File.Delete(installer);
        if (code != 0 || FindOllamaExe() is null)
        {
            throw new InvalidOperationException($"Ollama installer failed (exit code {code}).");
        }
    }

    private async Task StartServerAsync(CancellationToken ct)
    {
        var exe = FindOllamaExe() ?? throw new InvalidOperationException("ollama.exe was not found after installation.");

        // The installer usually starts the tray app (and server) itself; give it a moment before starting our own.
        for (var i = 0; i < 60; i++)
        {
            if (await IsServerRunningAsync(ct))
            {
                return;
            }

            if (i == 5)
            {
                Process.Start(new ProcessStartInfo(exe, "serve") { UseShellExecute = false, CreateNoWindow = true });
            }

            await Task.Delay(1000, ct);
        }

        throw new InvalidOperationException("Ollama server did not start.");
    }

    private static string? FindOllamaExe()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe"),
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim(), "ollama.exe")));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<int> RunProcessAsync(string file, string args, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException($"Could not start {file}.");
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}
