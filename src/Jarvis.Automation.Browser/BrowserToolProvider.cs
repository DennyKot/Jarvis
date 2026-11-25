using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Jarvis.Core.Configuration;
using Jarvis.Core.Safety;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace Jarvis.Automation.Browser;

public sealed class BrowserToolProvider(
    ChromeSession session,
    IPointerService pointer,
    IWindowActivator windows,
    IActionGuard guard,
    RiskPolicy riskPolicy,
    IOptions<JarvisOptions> options,
    ILogger<BrowserToolProvider> logger,
    UndoJournal? undo = null) : IToolProvider
{
    private const float ActionTimeoutMs = 8000;
    private const float ClickTimeoutMs = 2500;
    internal const int NavigationExcerptLength = 1200;
    private const string TextLinePrefix = "text: ";

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPage, PageSnapshot> _snapshots = new();

    private BrowserOptions Options => options.Value.Browser;

    private sealed record PageSnapshot(string Url, IReadOnlyList<string> Lines);

    // Other modes get their browser_* tools from the desktop (UserWindow) or extension provider.
    public IEnumerable<JarvisTool> GetTools() => Options.Mode == BrowserMode.JarvisProfile ? AllTools() : [];

    private IEnumerable<JarvisTool> AllTools() =>
    [
        JarvisTool.Create(OpenAsync, "browser_open", "Open URL in Chrome. Returns page snapshot."),
        JarvisTool.Create(SnapshotAsync, "browser_snapshot",
            "Full snapshot of current page: elements with refs [eN] + text.", ToolRisk.ReadOnly),
        JarvisTool.Create(ClickAsync, "browser_click", "Click page element by ref.", ToolRisk.Action, "element"),
        JarvisTool.Create(TypeAsync, "browser_type", "Type into field by ref.", ToolRisk.Action, "element"),
        JarvisTool.Create(SelectOptionAsync, "browser_select_option", "Choose <select> option by ref.", ToolRisk.Action, "element"),
        JarvisTool.Create(PressAsync, "browser_press",
            "Press key in page, e.g. 'Enter', 'Control+A'."),
        JarvisTool.Create(ScrollAsync, "browser_scroll", "Scroll page."),
        JarvisTool.Create(BackAsync, "browser_back", "Go back in history."),
        JarvisTool.Create(ReadTextAsync, "browser_read_text",
            "Read page main text (or one element by ref); pass query to get only matching paragraphs.", ToolRisk.ReadOnly),
        JarvisTool.Create(TabsAsync, "browser_tabs", "List/switch/new/close tabs."),
    ];

    private async Task<string> OpenAsync(
        [Description("URL")] string url,
        [Description("Open in a new tab.")] bool newTab = false,
        CancellationToken cancellationToken = default)
    {
        url = NormalizeUrl(url);
        var before = await CaptureAsync(cancellationToken);

        var page = newTab ? await session.NewPageAsync(cancellationToken) : await session.GetActivePageAsync(cancellationToken);
        await page.BringToFrontAsync();
        await ActivateChromeWindowAsync(page);
        await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
        await SettleAsync(page);
        await RecordPageUndoAsync(before, cancellationToken);
        return await DescribePageAsync(page, "Opened " + page.Url, full: true);
    }

    private async Task<string> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var page = await session.GetActivePageAsync(cancellationToken);
        return await DescribePageAsync(page, null, full: true);
    }

    private async Task<string> ClickAsync(
        [Description("ref, e.g. e12")] string @ref,
        [Description("What it is, e.g. Search button")] string element,
        [Description("Double-click.")] bool doubleClick = false,
        CancellationToken cancellationToken = default)
    {
        var page = await session.GetActivePageAsync(cancellationToken);
        var locator = await ResolveAsync(page, @ref);
        await GuardRealNameAsync(locator, element, cancellationToken);
        await PointAtAsync(page, locator, doubleClick ? PointerAction.DoubleClick : PointerAction.Click, element, cancellationToken);

        var pagesBefore = page.Context.Pages.Count;
        var before = await CaptureAsync(cancellationToken);
        await ClickWithFallbackAsync(locator, doubleClick);

        await SettleAsync(page);
        if (page.Context.Pages.Count > pagesBefore && page.Context.Pages[^1] is { IsClosed: false } popup)
        {
            session.SetActivePage(popup);
            await SettleAsync(popup);
            await RecordPageUndoAsync(before, cancellationToken);
            return await DescribePageAsync(popup, $"Clicked {element}; it opened a new tab", full: true);
        }

        await RecordPageUndoAsync(before, cancellationToken);
        return await DescribePageAsync(page, $"Clicked {element}");
    }

    private async Task<string> TypeAsync(
        [Description("ref")] string @ref,
        [Description("Text to type.")] string text,
        [Description("What it is")] string element,
        [Description("Press Enter after typing.")] bool submit = false,
        [Description("Replace content")] bool clear = true,
        CancellationToken cancellationToken = default)
    {
        var page = await session.GetActivePageAsync(cancellationToken);
        var locator = await ResolveAsync(page, @ref);

        var type = await locator.GetAttributeAsync("type", new() { Timeout = 2000 });
        var isPassword = string.Equals(type, "password", StringComparison.OrdinalIgnoreCase);
        if (isPassword)
        {
            await guard.EnsureApprovedAsync($"Type into password field '{element}'", "Password fields are sensitive.", cancellationToken);
        }

        // Passwords are never kept in memory for undo.
        var previous = isPassword ? null : await TryGetValueAsync(locator);
        Func<CancellationToken, Task<string>>? restore = previous is null ? null : async _ =>
        {
            await locator.FillAsync(previous, new() { Timeout = ActionTimeoutMs });
            return $"Restored the previous text of {element}.";
        };
        var before = await CaptureAsync(cancellationToken);

        await PointAtAsync(page, locator, PointerAction.Type, element, cancellationToken);
        if (clear)
        {
            await locator.FillAsync(text, new() { Timeout = ActionTimeoutMs });
        }
        else
        {
            await locator.PressSequentiallyAsync(text, new() { Delay = 15, Timeout = ActionTimeoutMs });
        }

        if (submit)
        {
            var urlBefore = page.Url;
            await locator.PressAsync("Enter", new() { Timeout = ActionTimeoutMs });
            await SettleAsync(page);
            await RecordPageUndoAsync(before, cancellationToken, restore);
            if (page.Url == urlBefore)
            {
                return await DescribePageAsync(page, $"Typed into {element} and pressed Enter");
            }

            // Searches usually land on the answer page: include its text so the model can answer without browser_read_text.
            return await DescribePageAsync(page, $"Typed into {element}, pressed Enter and it opened a new page", full: true, withExcerpt: true);
        }

        if (restore is not null)
        {
            undo?.SetUndo(restore);
        }

        return $"Typed {text.Length} characters into {element}.";
    }

    private async Task<string> SelectOptionAsync(
        [Description("ref")] string @ref,
        [Description("Option label or value.")] string value,
        [Description("What it is")] string element,
        CancellationToken cancellationToken = default)
    {
        var page = await session.GetActivePageAsync(cancellationToken);
        var locator = await ResolveAsync(page, @ref);
        var previous = await TryGetValueAsync(locator);
        await PointAtAsync(page, locator, PointerAction.Click, element, cancellationToken);
        try
        {
            await locator.SelectOptionAsync(new SelectOptionValue { Label = value }, new() { Timeout = 3000 });
        }
        catch (PlaywrightException)
        {
            await locator.SelectOptionAsync(value, new() { Timeout = ActionTimeoutMs });
        }

        await SettleAsync(page);
        if (previous is not null)
        {
            undo?.SetUndo(async _ =>
            {
                await locator.SelectOptionAsync(new SelectOptionValue { Value = previous }, new() { Timeout = ActionTimeoutMs });
                return $"Restored the previous choice in {element}.";
            });
        }

        return $"Selected '{value}' in {element}.";
    }

    private async Task<string> PressAsync(
        [Description("Key")] string keys,
        CancellationToken cancellationToken = default)
    {
        var page = await session.GetActivePageAsync(cancellationToken);
        var before = await CaptureAsync(cancellationToken);
        await page.Keyboard.PressAsync(keys);
        await SettleAsync(page);
        await RecordPageUndoAsync(before, cancellationToken);
        return $"Pressed {keys}. Page: {await SafeTitleAsync(page)} ({page.Url})";
    }

    private async Task<string> ScrollAsync(
        [Description("'down' or 'up'.")] string direction = "down",
        [Description("Number of screens to scroll.")] double screens = 1,
        CancellationToken cancellationToken = default)
    {
        var page = await session.GetActivePageAsync(cancellationToken);
        var sign = direction.Equals("up", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
        var viewport = await GetViewportAsync(page);
        await PointAtViewportAsync(page, new ScreenRect(viewport.InnerWidth / 2 - 1, viewport.InnerHeight / 2 - 1, 2, 2),
            PointerAction.Scroll, $"Scroll {direction}", cancellationToken);
        // Wait for the smooth scroll to finish ('scrollend'), at most 600 ms (nothing fires when the page can't scroll).
        await page.EvaluateAsync(
            """
            (dy) => new Promise(resolve => {
              const timer = setTimeout(resolve, 600);
              window.addEventListener('scrollend', () => { clearTimeout(timer); resolve(); }, { once: true });
              window.scrollBy({ top: dy, behavior: 'smooth' });
            })
            """, sign * screens * viewport.InnerHeight * 0.85);
        undo?.SkipRecording();
        return await DescribePageAsync(page, $"Scrolled {direction}");
    }

    private async Task<string> BackAsync(CancellationToken cancellationToken = default)
    {
        var page = await session.GetActivePageAsync(cancellationToken);
        var before = await CaptureAsync(cancellationToken);
        await page.GoBackAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 15000 });
        await SettleAsync(page);
        await RecordPageUndoAsync(before, cancellationToken);
        return await DescribePageAsync(page, "Went back", full: true);
    }

    private async Task<string> ReadTextAsync(
        [Description("Optional ref")] string? @ref = null,
        [Description("Keywords: return only matching paragraphs")] string? query = null,
        CancellationToken cancellationToken = default)
    {
        var page = await session.GetActivePageAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(@ref))
        {
            var text = await (await ResolveAsync(page, @ref)).InnerTextAsync(new() { Timeout = ActionTimeoutMs });
            return TextQuery.Filter(text, query, Options.ReadTextLimit);
        }

        var max = string.IsNullOrWhiteSpace(query) ? Options.ReadTextLimit : TextQuery.SourceLimit;
        var body = await page.EvaluateAsync<string>(PageScripts.ReadText, max);
        return $"{await SafeTitleAsync(page)} ({page.Url})\n{TextQuery.Filter(body, query, Options.ReadTextLimit)}";
    }

    private async Task<string> TabsAsync(
        [Description("'list', 'switch', 'new' or 'close'.")] string action = "list",
        [Description("Tab index")] int? index = null,
        [Description("URL for 'new'.")] string? url = null,
        CancellationToken cancellationToken = default)
    {
        var pages = await session.GetPagesAsync(cancellationToken);
        switch (action.ToLowerInvariant())
        {
            case "new":
                return await OpenAsync(string.IsNullOrWhiteSpace(url) ? "about:blank" : url, newTab: true, cancellationToken);

            case "switch" or "close" when index is null || index < 0 || index >= pages.Count:
                throw new ArgumentException($"Tab index must be between 0 and {pages.Count - 1}.");

            case "switch":
                var target = pages[index!.Value];
                var before = await CaptureAsync(cancellationToken);
                session.SetActivePage(target);
                await target.BringToFrontAsync();
                await ActivateChromeWindowAsync(target);
                await RecordPageUndoAsync(before, cancellationToken);
                return await DescribePageAsync(target, $"Switched to tab {index}", full: true);

            case "close":
                await pages[index!.Value].CloseAsync();
                return $"Closed tab {index}.";

            default:
                undo?.SkipRecording();
                var active = await session.GetActivePageAsync(cancellationToken);
                var builder = new StringBuilder();
                for (var i = 0; i < pages.Count; i++)
                {
                    builder.Append(pages[i] == active ? "* " : "- ")
                        .Append(i).Append(": ").Append(await SafeTitleAsync(pages[i])).Append(" — ").AppendLine(pages[i].Url);
                }

                return builder.ToString();
        }
    }

    private sealed record PageState(IPage Page, string Url, IReadOnlyList<IPage> Pages);

    private async Task<PageState> CaptureAsync(CancellationToken cancellationToken)
    {
        var page = await session.GetActivePageAsync(cancellationToken);
        return new PageState(page, page.Url, await session.GetPagesAsync(cancellationToken));
    }

    /// <summary>
    /// Undo for actions that may change tabs or navigate: close a tab they opened, switch back to the previous tab,
    /// or return to the previous URL; otherwise <paramref name="sameUrlUndo"/> (if any).
    /// </summary>
    private async Task RecordPageUndoAsync(PageState before, CancellationToken cancellationToken,
        Func<CancellationToken, Task<string>>? sameUrlUndo = null)
    {
        if (undo is null)
        {
            return;
        }

        var after = await session.GetActivePageAsync(cancellationToken);
        if (after != before.Page)
        {
            var opened = !before.Pages.Contains(after);
            undo.SetUndo(async _ =>
            {
                if (opened && !after.IsClosed)
                {
                    await after.CloseAsync();
                }

                if (before.Page.IsClosed)
                {
                    return opened ? "Closed the tab it opened." : "The previous tab is already closed.";
                }

                session.SetActivePage(before.Page);
                await before.Page.BringToFrontAsync();
                await ActivateChromeWindowAsync(before.Page);
                return opened ? "Closed the tab it opened." : "Switched back to the previous tab.";
            });
        }
        else if (after.Url != before.Url)
        {
            undo.SetUndo(async _ =>
            {
                if (before.Page.IsClosed)
                {
                    return "That tab is already closed.";
                }

                await before.Page.GotoAsync(before.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
                await SettleAsync(before.Page);
                return $"Went back to {before.Url}.";
            });
        }
        else if (sameUrlUndo is not null)
        {
            undo.SetUndo(sameUrlUndo);
        }
    }

    /// <summary>Current value of an input, textarea or select; null for other elements.</summary>
    private static async Task<string?> TryGetValueAsync(ILocator locator)
    {
        try
        {
            return await locator.InputValueAsync(new() { Timeout = 1500 });
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            return null;
        }
    }

    internal static string NormalizeUrl(string url)
    {
        url = url.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(url, "^(https?|file|about|data|chrome|view-source):", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            ? url
            : "https://" + url;
    }

    private async Task<string> DescribePageAsync(IPage page, string? header, bool full = false, bool withExcerpt = false)
    {
        var json = await page.EvaluateAsync<JsonElement>(PageScripts.Snapshot,
            new { limit = Options.SnapshotElementLimit, textBudget = 1500 });

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

        var diff = !full && _snapshots.TryGetValue(page, out var previous) && previous.Url == url
            ? SnapshotDiff.Describe(previous.Lines, lines)
            : null;
        _snapshots.AddOrUpdate(page, new PageSnapshot(url, lines));

        if (diff is not null)
        {
            return builder.Append(diff).ToString();
        }

        var excerpt = withExcerpt ? await page.EvaluateAsync<string>(PageScripts.ReadText, NavigationExcerptLength) : null;
        return AppendSnapshot(builder, lines, json.GetProperty("truncated").GetBoolean(), excerpt).ToString();
    }

    /// <summary>
    /// Writes the snapshot lines and, if given, the start of the page text. The excerpt already holds the page's text,
    /// so the snapshot's own "text:" lines are left out then (they would repeat it).
    /// </summary>
    internal static StringBuilder AppendSnapshot(StringBuilder builder, IReadOnlyList<string> lines, bool truncated, string? excerpt)
    {
        foreach (var line in lines)
        {
            if (excerpt is null || !line.StartsWith(TextLinePrefix, StringComparison.Ordinal))
            {
                builder.AppendLine(line);
            }
        }

        if (truncated)
        {
            builder.AppendLine("…(more elements below; scroll to see them)");
        }

        if (excerpt is not null)
        {
            builder.AppendLine().AppendLine("Page text (start):").AppendLine(excerpt);
        }

        return builder;
    }

    /// <summary>
    /// A real mouse click is preferred, but Playwright waits up to the full timeout when the element is hidden or covered
    /// (overlays, collapsed menus); then the DOM click usually still works and saves the model a step.
    /// </summary>
    private static async Task ClickWithFallbackAsync(ILocator locator, bool doubleClick)
    {
        if (await locator.IsVisibleAsync())
        {
            try
            {
                if (doubleClick)
                {
                    await locator.DblClickAsync(new() { Timeout = ClickTimeoutMs });
                }
                else
                {
                    await locator.ClickAsync(new() { Timeout = ClickTimeoutMs });
                }

                return;
            }
            catch (TimeoutException)
            {
            }
        }

        await locator.EvaluateAsync(
            """
            (e, dbl) => {
              e.scrollIntoView({ block: 'center' });
              e.click();
              if (dbl) { e.click(); e.dispatchEvent(new MouseEvent('dblclick', { bubbles: true })); }
            }
            """, doubleClick);
    }

    private async Task<ILocator> ResolveAsync(IPage page, string reference)
    {
        var normalized = reference.Trim().Trim('[', ']');
        var locator = page.Locator($"[{PageScripts.RefAttribute}='{normalized}']");
        if (await locator.CountAsync() == 0)
        {
            // Return the current state right away instead of making the model spend a step on browser_snapshot.
            throw new InvalidOperationException($"Ref '{normalized}' no longer exists. {await DescribePageAsync(page, null)}");
        }

        return locator.First;
    }

    /// <summary>The description comes from the model; double-check the element's real text for risky words.</summary>
    private async Task GuardRealNameAsync(ILocator locator, string description, CancellationToken cancellationToken)
    {
        if (riskPolicy.Mode != SafetyMode.ConfirmRisky || riskPolicy.TryFindRiskyKeyword(description, out _))
        {
            return;
        }

        string realName;
        try
        {
            realName = await locator.EvaluateAsync<string>(
                "e => (e.getAttribute('aria-label') || e.innerText || e.value || e.getAttribute('title') || '').trim().slice(0, 120)");
        }
        catch (PlaywrightException)
        {
            return;
        }

        if (riskPolicy.TryFindRiskyKeyword(realName, out var keyword))
        {
            await guard.EnsureApprovedAsync($"Click \"{realName}\"", $"The element looks risky (\"{keyword}\").", cancellationToken);
        }
    }

    private async Task PointAtAsync(IPage page, ILocator locator, PointerAction action, string label, CancellationToken cancellationToken)
    {
        try
        {
            await locator.ScrollIntoViewIfNeededAsync(new() { Timeout = 3000 });
            var box = await locator.BoundingBoxAsync(new() { Timeout = 3000 });
            if (box is not null)
            {
                await PointAtViewportAsync(page, new ScreenRect(box.X, box.Y, box.Width, box.Height), action, label, cancellationToken);
            }
        }
        catch (PlaywrightException ex)
        {
            logger.LogDebug(ex, "Could not position the AI pointer");
        }
    }

    private async Task PointAtViewportAsync(IPage page, ScreenRect cssRect, PointerAction action, string label, CancellationToken cancellationToken)
    {
        var hwnd = await ActivateChromeWindowAsync(page);
        if (windows.GetWindowBounds(hwnd) is not { } windowBounds)
        {
            return;
        }

        var viewport = await GetViewportAsync(page);
        await pointer.PointAsync(ViewportMapper.ToScreen(windowBounds, viewport, cssRect), action, label, cancellationToken);
    }

    private async Task<nint> ActivateChromeWindowAsync(IPage page)
    {
        var hwnd = windows.FindWindow("chrome", await SafeTitleAsync(page));
        if (hwnd != 0)
        {
            windows.Activate(hwnd);
        }

        return hwnd;
    }

    private static async Task<ViewportMetrics> GetViewportAsync(IPage page)
    {
        var json = await page.EvaluateAsync<JsonElement>(PageScripts.Viewport);
        return new ViewportMetrics(
            json.GetProperty("innerWidth").GetDouble(),
            json.GetProperty("innerHeight").GetDouble(),
            json.GetProperty("devicePixelRatio").GetDouble());
    }

    private static async Task SettleAsync(IPage page)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 10000 });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 600 });
        }
        catch (TimeoutException)
        {
            // Busy pages never become network-idle; the DOM is good enough.
        }
        catch (PlaywrightException)
        {
        }

        await Task.Delay(100);
    }

    private static async Task<string> SafeTitleAsync(IPage page)
    {
        try
        {
            return await page.TitleAsync();
        }
        catch (PlaywrightException)
        {
            return string.Empty;
        }
    }
}
