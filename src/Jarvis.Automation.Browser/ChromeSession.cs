using System.Diagnostics;
using System.Net.Http;
using Jarvis.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Microsoft.Win32;

namespace Jarvis.Automation.Browser;

public sealed class ChromeSession(IOptions<JarvisOptions> options, ILogger<ChromeSession> logger) : IAsyncDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _activePage;

    private BrowserOptions Options => options.Value.Browser;

    private string Endpoint => $"http://127.0.0.1:{Options.DebugPort}";

    public async Task<IBrowserContext> GetContextAsync(CancellationToken cancellationToken)
    {
        if (_browser is { IsConnected: true } && _context is not null)
        {
            return _context;
        }

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_browser is { IsConnected: true } && _context is not null)
            {
                return _context;
            }

            if (!await IsDebugEndpointUpAsync(cancellationToken))
            {
                LaunchChrome();
                await WaitForDebugEndpointAsync(cancellationToken);
            }

            _playwright ??= await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.ConnectOverCDPAsync(Endpoint, new() { Timeout = 15000 });
            _context = _browser.Contexts.FirstOrDefault() ?? await _browser.NewContextAsync();
            _context.Page += (_, page) => _activePage = page;
            _activePage = null;
            logger.LogInformation("Connected to Chrome at {Endpoint}", Endpoint);
            return _context;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<IPage> GetActivePageAsync(CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cancellationToken);
        if (_activePage is { IsClosed: false })
        {
            return _activePage;
        }

        var pages = context.Pages.Where(p => !p.IsClosed && !p.Url.StartsWith("devtools://", StringComparison.Ordinal)).ToList();
        _activePage = pages.LastOrDefault() ?? await context.NewPageAsync();
        return _activePage;
    }

    public async Task<IReadOnlyList<IPage>> GetPagesAsync(CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cancellationToken);
        return context.Pages.Where(p => !p.IsClosed && !p.Url.StartsWith("devtools://", StringComparison.Ordinal)).ToList();
    }

    public async Task<IPage> NewPageAsync(CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(cancellationToken);
        _activePage = await context.NewPageAsync();
        return _activePage;
    }

    public void SetActivePage(IPage page) => _activePage = page;

    public async ValueTask DisposeAsync()
    {
        // Only disconnect: the user's Chrome keeps running.
        if (_browser is not null)
        {
            await _browser.CloseAsync().ConfigureAwait(false);
        }

        _playwright?.Dispose();
    }

    private async Task<bool> IsDebugEndpointUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync($"{Endpoint}/json/version", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task WaitForDebugEndpointAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsDebugEndpointUpAsync(cancellationToken))
            {
                return;
            }

            await Task.Delay(300, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Chrome did not open its debugging port {Options.DebugPort}. If Chrome is already running with the Jarvis profile " +
            "but without remote debugging, close that window and try again.");
    }

    private void LaunchChrome()
    {
        var chrome = FindChrome() ?? throw new InvalidOperationException(
            "Google Chrome was not found. Install it or set Jarvis:Browser:ChromePath.");
        var profile = Options.ProfileDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis", "ChromeProfile");
        Directory.CreateDirectory(profile);

        var startInfo = new ProcessStartInfo(chrome) { UseShellExecute = false };
        startInfo.ArgumentList.Add($"--remote-debugging-port={Options.DebugPort}");
        startInfo.ArgumentList.Add($"--user-data-dir={profile}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--start-maximized");
        using var _ = Process.Start(startInfo);
        logger.LogInformation("Launched Chrome {Path} with profile {Profile}", chrome, profile);
    }

    private string? FindChrome()
    {
        if (!string.IsNullOrWhiteSpace(Options.ChromePath) && File.Exists(Options.ChromePath))
        {
            return Options.ChromePath;
        }

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"')))
            {
                return path.Trim('"');
            }
        }

        return new[]
            {
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData,
            }
            .Select(f => Path.Combine(Environment.GetFolderPath(f), "Google", "Chrome", "Application", "chrome.exe"))
            .FirstOrDefault(File.Exists);
    }
}
