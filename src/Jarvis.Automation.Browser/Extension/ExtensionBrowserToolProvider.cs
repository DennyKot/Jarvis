using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Jarvis.Core.Configuration;
using Jarvis.Core.Safety;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Automation.Browser.Extension;

/// <summary>browser_* tools that operate the user's own Chrome through the Jarvis Bridge extension.</summary>
public sealed class ExtensionBrowserToolProvider(
    ExtensionBridge bridge,
    ChromeExtensionInstaller installer,
    IPointerService pointer,
    IWindowActivator windows,
    IActionGuard guard,
    RiskPolicy riskPolicy,
    IOptions<JarvisOptions> options,
    Jarvis.Core.Agent.AssistantStatus status,
    ILogger<ExtensionBrowserToolProvider> logger) : IToolProvider
{
    private readonly Dictionary<int, (string Url, List<string> Lines)> _snapshots = [];
    private bool _installAttempted;

    private BrowserOptions Options => options.Value.Browser;

    public IEnumerable<JarvisTool> GetTools() => Options.Mode != BrowserMode.Extension ? [] :
    [
        JarvisTool.Create(OpenAsync, "browser_open", "Open URL in the user's Chrome. Returns page snapshot."),
        JarvisTool.Create(SnapshotAsync, "browser_snapshot", "Full snapshot of current page: elements with refs [eN] + text.", ToolRisk.ReadOnly),
        JarvisTool.Create(ClickAsync, "browser_click", "Click page element by ref.", ToolRisk.Action, "element"),
        JarvisTool.Create(TypeAsync, "browser_type", "Type into field by ref.", ToolRisk.Action, "element"),
        JarvisTool.Create(SelectOptionAsync, "browser_select_option", "Choose <select> option by ref.", ToolRisk.Action, "element"),
        JarvisTool.Create(PressAsync, "browser_press", "Press key in page, e.g. 'Enter', 'Escape'."),
        JarvisTool.Create(ScrollAsync, "browser_scroll", "Scroll page."),
        JarvisTool.Create(BackAsync, "browser_back", "Go back in history."),
        JarvisTool.Create(ReadTextAsync, "browser_read_text", "Read page text (or one element by ref).", ToolRisk.ReadOnly),
        JarvisTool.Create(TabsAsync, "browser_tabs", "List/switch/new/close tabs."),
    ];

    private async Task<string> OpenAsync(
        [Description("URL")] string url,
        [Description("Open in a new tab (default) instead of the current one")] bool newTab = true,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        url = BrowserToolProvider.NormalizeUrl(url);
        await bridge.SendAsync("open", new { url, newTab }, cancellationToken, TimeSpan.FromSeconds(35));
        return await DescribeAsync("Opened " + url, full: true, cancellationToken);
    }

    private async Task<string> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await DescribeAsync(null, full: true, cancellationToken);
    }

    private async Task<string> ClickAsync(
        [Description("ref, e.g. e12")] string @ref,
        [Description("What it is, e.g. Search button")] string element,
        [Description("Double-click.")] bool doubleClick = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        var target = await LocateAsync(@ref, cancellationToken);
        if (riskPolicy.Mode == SafetyMode.ConfirmRisky && !riskPolicy.TryFindRiskyKeyword(element, out _) &&
            riskPolicy.TryFindRiskyKeyword(target.Name, out var keyword))
        {
            await guard.EnsureApprovedAsync($"Click \"{target.Name}\"", $"The element looks risky (\"{keyword}\").", cancellationToken);
        }

        await PointAsync(target, doubleClick ? PointerAction.DoubleClick : PointerAction.Click, element, cancellationToken);
        await SendToElementAsync("click", new { @ref = Ref(@ref), doubleClick }, cancellationToken);
        return await DescribeAsync($"Clicked {element}", full: false, cancellationToken);
    }

    private async Task<string> TypeAsync(
        [Description("ref")] string @ref,
        [Description("Text to type.")] string text,
        [Description("What it is")] string element,
        [Description("Press Enter after typing.")] bool submit = false,
        [Description("Replace content")] bool clear = true,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        var target = await LocateAsync(@ref, cancellationToken);
        if (string.Equals(target.Type, "password", StringComparison.OrdinalIgnoreCase))
        {
            await guard.EnsureApprovedAsync($"Type into password field '{element}'", "Password fields are sensitive.", cancellationToken);
        }

        await PointAsync(target, PointerAction.Type, element, cancellationToken);
        await SendToElementAsync("type", new { @ref = Ref(@ref), text, clear, submit }, cancellationToken);
        return submit
            ? await DescribeAsync($"Typed into {element} and pressed Enter", full: false, cancellationToken)
            : $"Typed {text.Length} characters into {element}.";
    }

    private async Task<string> SelectOptionAsync(
        [Description("ref")] string @ref,
        [Description("Option label or value.")] string value,
        [Description("What it is")] string element,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        var target = await LocateAsync(@ref, cancellationToken);
        await PointAsync(target, PointerAction.Click, element, cancellationToken);
        await SendToElementAsync("select", new { @ref = Ref(@ref), value }, cancellationToken);
        return await DescribeAsync($"Selected '{value}' in {element}", full: false, cancellationToken);
    }

    private async Task<string> PressAsync([Description("Key")] string keys, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        await bridge.SendAsync("press", new { keys }, cancellationToken);
        return await DescribeAsync($"Pressed {keys}", full: false, cancellationToken);
    }

    private async Task<string> ScrollAsync(
        [Description("'down' or 'up'.")] string direction = "down",
        [Description("Number of screens to scroll.")] double screens = 1,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        var sign = direction.Equals("up", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
        var viewport = await bridge.SendAsync("rect", new { @ref = "__none__" }, cancellationToken);
        var height = viewport.TryGetProperty("innerHeight", out var h) ? h.GetDouble() : 800;
        await bridge.SendAsync("scroll", new { dy = sign * screens * height * 0.85 }, cancellationToken);
        return await DescribeAsync($"Scrolled {direction}", full: false, cancellationToken);
    }

    private async Task<string> BackAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        await bridge.SendAsync("back", null, cancellationToken);
        return await DescribeAsync("Went back", full: true, cancellationToken);
    }

    private async Task<string> ReadTextAsync([Description("Optional ref")] string? @ref = null, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        var result = await bridge.SendAsync("readText",
            new { @ref = string.IsNullOrWhiteSpace(@ref) ? null : Ref(@ref), max = Options.ReadTextLimit }, cancellationToken);
        return result.ValueKind == JsonValueKind.String
            ? result.GetString()!
            : $"Ref '{@ref}' no longer exists; take a new snapshot.";
    }

    private async Task<string> TabsAsync(
        [Description("'list', 'switch', 'new' or 'close'.")] string action = "list",
        [Description("Tab index")] int? index = null,
        [Description("URL for 'new'.")] string? url = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        switch (action.ToLowerInvariant())
        {
            case "new":
                return await OpenAsync(string.IsNullOrWhiteSpace(url) ? "about:blank" : url, newTab: true, cancellationToken);
            case "switch":
                await bridge.SendAsync("tabs", new { action, index }, cancellationToken);
                return await DescribeAsync($"Switched to tab {index}", full: true, cancellationToken);
            case "close":
                await bridge.SendAsync("tabs", new { action, index }, cancellationToken);
                return $"Closed tab {index}.";
            default:
                var tabs = await bridge.SendAsync("tabs", new { action = "list" }, cancellationToken);
                var builder = new StringBuilder();
                foreach (var tab in tabs.EnumerateArray())
                {
                    builder.Append(tab.GetProperty("current").GetBoolean() ? "* " : "- ")
                        .Append(tab.GetProperty("index").GetInt32()).Append(": ")
                        .Append(tab.GetProperty("title").GetString()).Append(" — ").AppendLine(tab.GetProperty("url").GetString());
                }

                return builder.ToString();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (bridge.IsConnected)
        {
            return;
        }

        // Chrome suspends idle extensions; an installed one wakes up and reconnects within ~30 s.
        if (bridge.WasEverConnected)
        {
            status.ReportActivity("Waiting for the Chrome extension to reconnect…");
        }

        if (await bridge.WaitForConnectionAsync(TimeSpan.FromSeconds(bridge.WasEverConnected ? 35 : 4), cancellationToken))
        {
            return;
        }

        if (_installAttempted)
        {
            throw new InvalidOperationException($"The Jarvis Chrome extension is not connected. {installer.ManualSteps}");
        }

        _installAttempted = true;
        await guard.EnsureApprovedAsync("Install the Jarvis extension in your Chrome",
            "Jarvis will open chrome://extensions, turn on Developer mode and load its extension (one time).", cancellationToken);
        var message = await installer.InstallAsync(cancellationToken);
        if (!bridge.IsConnected)
        {
            throw new InvalidOperationException(message);
        }
    }

    private async Task<string> DescribeAsync(string? header, bool full, CancellationToken cancellationToken)
    {
        var json = await bridge.SendAsync("snapshot", new { limit = Options.SnapshotElementLimit, textBudget = 1500 }, cancellationToken);
        var builder = new StringBuilder();
        if (header is not null)
        {
            builder.AppendLine(header + ".");
        }

        var url = json.GetProperty("url").GetString() ?? string.Empty;
        builder.Append("Page: ").Append(json.GetProperty("title").GetString()).Append(" — ").AppendLine(url);
        builder.Append("Scroll: ").Append(json.GetProperty("scrollY").GetInt32())
            .Append('/').Append(json.GetProperty("scrollHeight").GetInt32())
            .Append(" (viewport ").Append(json.GetProperty("viewportHeight").GetInt32()).AppendLine(")");
        var lines = json.GetProperty("lines").EnumerateArray().Select(l => l.GetString() ?? string.Empty).ToList();

        var tabId = json.GetProperty("tabId").GetInt32();
        var diff = !full && _snapshots.TryGetValue(tabId, out var previous) && previous.Url == url
            ? SnapshotDiff.Describe(previous.Lines, lines)
            : null;
        _snapshots[tabId] = (url, lines);

        if (diff is not null)
        {
            return builder.Append(diff).ToString();
        }

        foreach (var line in lines)
        {
            builder.AppendLine(line);
        }

        if (json.GetProperty("truncated").GetBoolean())
        {
            builder.AppendLine("…(more elements below; scroll to see them)");
        }

        return builder.ToString();
    }

    private sealed record ElementLocation(string Name, string Type, ScreenRect Css, ViewportMetrics Viewport, bool Active, string Title);

    private async Task<ElementLocation> LocateAsync(string reference, CancellationToken cancellationToken)
    {
        var json = await bridge.SendAsync("rect", new { @ref = Ref(reference) }, cancellationToken);
        if (json.TryGetProperty("missing", out _))
        {
            throw new InvalidOperationException($"Ref '{Ref(reference)}' no longer exists. {await DescribeAsync(null, full: false, cancellationToken)}");
        }

        return new ElementLocation(
            json.GetProperty("name").GetString() ?? string.Empty,
            json.GetProperty("type").GetString() ?? string.Empty,
            new ScreenRect(json.GetProperty("x").GetDouble(), json.GetProperty("y").GetDouble(),
                json.GetProperty("width").GetDouble(), json.GetProperty("height").GetDouble()),
            new ViewportMetrics(json.GetProperty("innerWidth").GetDouble(), json.GetProperty("innerHeight").GetDouble(),
                json.GetProperty("devicePixelRatio").GetDouble()),
            json.GetProperty("active").GetBoolean(),
            json.GetProperty("title").GetString() ?? string.Empty);
    }

    private async Task SendToElementAsync(string command, object args, CancellationToken cancellationToken)
    {
        var result = await bridge.SendAsync(command, args, cancellationToken);
        if (result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("missing", out _))
            {
                throw new InvalidOperationException($"The element disappeared. {await DescribeAsync(null, full: false, cancellationToken)}");
            }

            if (result.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException(error.GetString());
            }
        }
    }

    private async Task PointAsync(ElementLocation target, PointerAction action, string label, CancellationToken cancellationToken)
    {
        if (!target.Active || target.Css.IsEmpty)
        {
            return;
        }

        try
        {
            var hwnd = windows.FindWindow("chrome", target.Title);
            if (hwnd == 0 || windows.GetWindowBounds(hwnd) is not { } bounds)
            {
                return;
            }

            windows.Activate(hwnd);
            await pointer.PointAsync(ViewportMapper.ToScreen(bounds, target.Viewport, target.Css), action, label, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not position the AI pointer");
        }
    }

    private static string Ref(string reference) => reference.Trim().Trim('[', ']');
}
