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
    ILogger<ExtensionBrowserToolProvider> logger,
    UndoJournal? undo = null) : IToolProvider
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
        JarvisTool.Create(ReadTextAsync, "browser_read_text", "Read page main text (or one element by ref); pass query to get only matching paragraphs.", ToolRisk.ReadOnly),
        JarvisTool.Create(TabsAsync, "browser_tabs", "List/switch/new/close tabs."),
    ];

    private async Task<string> OpenAsync(
        [Description("URL")] string url,
        [Description("Open in a new tab (default) instead of the current one")] bool newTab = true,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        url = BrowserToolProvider.NormalizeUrl(url);
        var before = await TryGetTabStateAsync(cancellationToken);
        await bridge.SendAsync("open", new { url, newTab }, cancellationToken, TimeSpan.FromSeconds(35));
        await RecordTabUndoAsync(before, mayOpenTab: true, cancellationToken);
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
        var before = await TryGetTabStateAsync(cancellationToken);
        await SendToElementAsync("click", new { @ref = Ref(@ref), doubleClick }, cancellationToken);
        await RecordTabUndoAsync(before, mayOpenTab: true, cancellationToken);
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
        var isPassword = string.Equals(target.Type, "password", StringComparison.OrdinalIgnoreCase);
        if (isPassword)
        {
            await guard.EnsureApprovedAsync($"Type into password field '{element}'", "Password fields are sensitive.", cancellationToken);
        }

        // Passwords are never kept in memory for undo.
        var previous = isPassword ? null : await TryGetValueAsync(@ref, cancellationToken);
        var before = await TryGetTabStateAsync(cancellationToken);
        await PointAsync(target, PointerAction.Type, element, cancellationToken);
        await SendToElementAsync("type", new { @ref = Ref(@ref), text, clear, submit }, cancellationToken);
        await RecordTabUndoAsync(before, mayOpenTab: false, cancellationToken, previous is null ? null : async ct =>
        {
            await SendToElementAsync("type", new { @ref = Ref(@ref), text = previous, clear = true, submit = false }, ct);
            return $"Restored the previous text of {element}.";
        });
        return submit
            ? await DescribeAsync($"Typed into {element} and pressed Enter", full: false, cancellationToken, excerptOnNavigation: true)
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
        var previous = await TryGetValueAsync(@ref, cancellationToken);
        await PointAsync(target, PointerAction.Click, element, cancellationToken);
        await SendToElementAsync("select", new { @ref = Ref(@ref), value }, cancellationToken);
        if (previous is not null)
        {
            undo?.SetUndo(async ct =>
            {
                await SendToElementAsync("select", new { @ref = Ref(@ref), value = previous }, ct);
                return $"Restored the previous choice in {element}.";
            });
        }

        return await DescribeAsync($"Selected '{value}' in {element}", full: false, cancellationToken);
    }

    private async Task<string> PressAsync([Description("Key")] string keys, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        var before = await TryGetTabStateAsync(cancellationToken);
        await bridge.SendAsync("press", new { keys }, cancellationToken);
        await RecordTabUndoAsync(before, mayOpenTab: false, cancellationToken);
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
        undo?.SkipRecording();
        return await DescribeAsync($"Scrolled {direction}", full: false, cancellationToken);
    }

    private async Task<string> BackAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        var before = await TryGetTabStateAsync(cancellationToken);
        await bridge.SendAsync("back", null, cancellationToken);
        await RecordTabUndoAsync(before, mayOpenTab: false, cancellationToken);
        return await DescribeAsync("Went back", full: true, cancellationToken);
    }

    private async Task<string> ReadTextAsync(
        [Description("Optional ref")] string? @ref = null,
        [Description("Keywords: return only matching paragraphs")] string? query = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        var max = string.IsNullOrWhiteSpace(query) ? Options.ReadTextLimit : TextQuery.SourceLimit;
        var result = await bridge.SendAsync("readText",
            new { @ref = string.IsNullOrWhiteSpace(@ref) ? null : Ref(@ref), max }, cancellationToken);
        return result.ValueKind == JsonValueKind.String
            ? TextQuery.Filter(result.GetString()!, query, Options.ReadTextLimit)
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
                var before = await TryGetTabStateAsync(cancellationToken);
                await bridge.SendAsync("tabs", new { action, index }, cancellationToken);
                await RecordTabUndoAsync(before, mayOpenTab: false, cancellationToken);
                return await DescribeAsync($"Switched to tab {index}", full: true, cancellationToken);
            case "close":
                await bridge.SendAsync("tabs", new { action, index }, cancellationToken);
                return $"Closed tab {index}.";
            default:
                undo?.SkipRecording();
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

    private async Task<string> DescribeAsync(string? header, bool full, CancellationToken cancellationToken, bool excerptOnNavigation = false)
    {
        var json = await bridge.SendAsync("snapshot", new { limit = Options.SnapshotElementLimit, textBudget = 1500 }, cancellationToken);
        var builder = new StringBuilder();
        var (url, lines) = BrowserToolProvider.AppendPageHeader(builder, header, json);

        var tabId = json.GetProperty("tabId").GetInt32();
        var diff = !full && _snapshots.TryGetValue(tabId, out var previous) && previous.Url == url
            ? SnapshotDiff.Describe(previous.Lines, lines)
            : null;
        _snapshots[tabId] = (url, lines);

        if (diff is not null)
        {
            return builder.Append(diff).ToString();
        }

        string? excerpt = null;
        if (excerptOnNavigation)
        {
            // Searches usually land on the answer page: include its text so the model can answer without browser_read_text.
            var text = await bridge.SendAsync("readText", new { @ref = (string?)null, max = BrowserToolProvider.NavigationExcerptLength }, cancellationToken);
            excerpt = text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        }

        return BrowserToolProvider.AppendSnapshot(builder, lines, json.GetProperty("truncated").GetBoolean(), excerpt).ToString();
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

    private sealed record TabState(int TabId, string Url);

    /// <summary>Null when undo is off or the installed extension predates the undo commands.</summary>
    private async Task<TabState?> TryGetTabStateAsync(CancellationToken cancellationToken)
    {
        if (undo is null)
        {
            return null;
        }

        try
        {
            var json = await bridge.SendAsync("state", null, cancellationToken);
            return new TabState(json.GetProperty("tabId").GetInt32(), json.GetProperty("url").GetString() ?? string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read the tab state for undo");
            return null;
        }
    }

    private async Task<string?> TryGetValueAsync(string reference, CancellationToken cancellationToken)
    {
        if (undo is null)
        {
            return null;
        }

        try
        {
            var json = await bridge.SendAsync("value", new { @ref = Ref(reference) }, cancellationToken);
            return json.ValueKind == JsonValueKind.Object && json.TryGetProperty("value", out var value) ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read the field value for undo");
            return null;
        }
    }

    /// <summary>Undo for actions that may change tabs or navigate; otherwise <paramref name="sameUrlUndo"/> (if any).</summary>
    private async Task RecordTabUndoAsync(TabState? before, bool mayOpenTab, CancellationToken cancellationToken,
        Func<CancellationToken, Task<string>>? sameUrlUndo = null)
    {
        if (undo is null || before is null || await TryGetTabStateAsync(cancellationToken) is not { } after)
        {
            return;
        }

        if (after.TabId != before.TabId)
        {
            undo.SetUndo(async ct =>
            {
                if (mayOpenTab)
                {
                    await bridge.SendAsync("closeTab", new { tabId = after.TabId }, ct);
                }

                await bridge.SendAsync("restoreTab", new { tabId = before.TabId, url = (string?)null }, ct);
                return mayOpenTab ? "Closed the tab it opened." : "Switched back to the previous tab.";
            });
        }
        else if (after.Url != before.Url)
        {
            undo.SetUndo(async ct =>
            {
                await bridge.SendAsync("restoreTab", new { tabId = before.TabId, url = before.Url }, ct, TimeSpan.FromSeconds(35));
                return $"Went back to {before.Url}.";
            });
        }
        else if (sameUrlUndo is not null)
        {
            undo.SetUndo(sameUrlUndo);
        }
    }

    private static string Ref(string reference) => reference.Trim().Trim('[', ']');
}
