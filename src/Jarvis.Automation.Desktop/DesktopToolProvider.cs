using System.ComponentModel;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Jarvis.Automation.Desktop.Apps;
using Jarvis.Automation.Desktop.Input;
using Jarvis.Automation.Desktop.Screen;
using Jarvis.Automation.Desktop.UiAutomation;
using Jarvis.Automation.Desktop.Windows;
using System.Diagnostics;
using Jarvis.Core.Agent;
using Jarvis.Core.Configuration;
using Microsoft.Extensions.Options;
using Jarvis.Core.Safety;
using Jarvis.Core.Tools;

namespace Jarvis.Automation.Desktop;

public sealed class DesktopToolProvider(
    AppLauncher launcher,
    WindowService windows,
    UiAutomationService uia,
    InputSimulator input,
    ScreenCaptureService capture,
    OcrService ocr,
    IPointerService pointer,
    IActionGuard guard,
    AgentObservations observations,
    RiskPolicy riskPolicy,
    IOptions<JarvisOptions> options,
    UndoJournal? undo = null) : IToolProvider
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(3);
    private nint _currentWindow;

    private bool UserBrowser => options.Value.Browser.Mode == BrowserMode.UserWindow;

    public IEnumerable<JarvisTool> GetTools() => UserBrowser ? [.. CoreTools(), .. UserBrowserTools()] : CoreTools();

    private IEnumerable<JarvisTool> CoreTools() =>
    [
        JarvisTool.Create(LaunchAppAsync, "launch_app",
            "Start app by name, e.g. 'excel', 'vs code'."),
        JarvisTool.Create(ListWindows, "list_windows", "List open windows.", ToolRisk.ReadOnly),
        JarvisTool.Create(FocusWindow, "focus_window", "Bring window to front (title part or process name)."),
        JarvisTool.Create(UiTree, "ui_tree",
            "UI elements of a window (default: foreground) with ids [uN].",
            ToolRisk.ReadOnly),
        JarvisTool.Create(UiFind, "ui_find", "Find UI elements by name text.", ToolRisk.ReadOnly),
        JarvisTool.Create(UiClickAsync, "ui_click", "Click UI element by id.", ToolRisk.Action, "element"),
        JarvisTool.Create(UiTypeAsync, "ui_type", "Type into UI element by id.", ToolRisk.Action, "element"),
        JarvisTool.Create(TypeText, "type_text", "Type into focused control."),
        JarvisTool.Create(SendKeys, "send_keys",
            "Press keys, e.g. 'ctrl+s', 'ctrl+k ctrl+o'."),
        JarvisTool.Create(Screenshot, "screenshot",
            "Screenshot of window or screen (last resort).",
            ToolRisk.ReadOnly),
        JarvisTool.Create(OcrAsync, "ocr", "OCR text with positions of window or screen.",
            ToolRisk.ReadOnly),
        JarvisTool.Create(ClickAtAsync, "click_at",
            "Click at x,y of last screenshot (or 'screen' pixels from ocr).",
            ToolRisk.Action, "element"),
    ];

    private IEnumerable<JarvisTool> UserBrowserTools() =>
    [
        JarvisTool.Create(UserBrowserOpenAsync, "browser_open",
            "Open URL in the user's Chrome (new tab by default). Returns page elements with ids [uN] for ui_click/ui_type."),
        JarvisTool.Create(UserBrowserSnapshot, "browser_snapshot", "Page elements of the user's Chrome tab with ids [uN].", ToolRisk.ReadOnly),
        JarvisTool.Create(UserBrowserReadTextAsync, "browser_read_text", "Read the text of the page in the user's Chrome; pass query to get only matching paragraphs.", ToolRisk.ReadOnly),
    ];

    private async Task<string> UserBrowserOpenAsync(
        [Description("URL")] string url,
        [Description("Open in a new tab (default) instead of the current one")] bool newTab = true,
        CancellationToken cancellationToken = default)
    {
        url = url.Trim();
        if (!url.Contains("://", StringComparison.Ordinal) && !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        var chrome = FindUserChrome();
        if (chrome is null)
        {
            using var _ = Process.Start(new ProcessStartInfo("chrome", url) { UseShellExecute = true });
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
            while ((chrome = FindUserChrome()) is null && Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(200, cancellationToken);
            }

            chrome ??= FindUserChrome() ?? throw new InvalidOperationException("Chrome did not open a window.");
        }
        else
        {
            windows.Activate(chrome.Handle);
            await Task.Delay(150, cancellationToken);
            input.SendKeys(newTab ? "ctrl+t" : "ctrl+l");
            await Task.Delay(350, cancellationToken);
            input.TypeText(url);

            // Drop Chrome's inline autocomplete suggestion so Enter opens exactly this URL.
            input.SendKeys("delete enter");
            var tab = chrome;
            undo?.SetUndo(async ct =>
            {
                ActivateOrThrow(tab);
                await Task.Delay(150, ct);
                input.SendKeys(newTab ? "ctrl+w" : "alt+left");
                return newTab ? "Closed the tab it opened." : "Went back to the previous page.";
            });
        }

        _currentWindow = chrome.Handle;
        await Task.Delay(2500, cancellationToken);
        return $"Opened {url} in the user's Chrome.\n{UserBrowserSnapshot()}";
    }

    private string UserBrowserSnapshot()
    {
        var chrome = FindUserChrome() ?? throw new InvalidOperationException("No Chrome window is open. Use browser_open.");
        _currentWindow = chrome.Handle;
        var tree = uia.DescribeDocument(chrome.Handle, 120, TimeSpan.FromSeconds(6));
        return tree is null
            ? $"Window: {chrome.Title}\nThe page content is not accessible; use ocr(window='chrome') or screenshot."
            : $"Window: {chrome.Title}\n{tree}";
    }

    private async Task<string> UserBrowserReadTextAsync(
        [Description("Keywords: return only matching paragraphs")] string? query = null,
        CancellationToken cancellationToken = default)
    {
        var chrome = FindUserChrome() ?? throw new InvalidOperationException("No Chrome window is open.");
        var limit = options.Value.Browser.ReadTextLimit;
        var text = uia.ReadDocumentText(chrome.Handle, string.IsNullOrWhiteSpace(query) ? limit : TextQuery.SourceLimit);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return $"{chrome.Title}\n{TextQuery.Filter(text, query, limit)}";
        }

        // Fall back to OCR for pages that do not expose text (canvas apps, PDFs).
        using var bitmap = capture.CaptureBitmap(chrome.Bounds);
        var lines = await ocr.RecognizeAsync(bitmap, chrome.Bounds, null, cancellationToken);
        var ocrText = string.Join('\n', lines.Select(l => l.Text));
        return $"{chrome.Title} (OCR)\n{TextQuery.Filter(ocrText, query, limit)}";
    }

    /// <summary>The user's Chrome window: the current one if Jarvis is already working in it, otherwise the topmost.</summary>
    private WindowInfo? FindUserChrome()
    {
        var chromes = windows.ListWindows()
            .Where(w => w.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return chromes.FirstOrDefault(w => w.Handle == _currentWindow) ?? chromes.FirstOrDefault();
    }

    [Description("Starts an application.")]
    private async Task<string> LaunchAppAsync(
        [Description("App name, e.g. 'excel' or 'visual studio code'.")] string name,
        [Description("Optional args")] string? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var before = windows.SnapshotHandles();
        var candidate = launcher.Launch(name, arguments);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(400, cancellationToken);
            var created = windows.ListWindows().FirstOrDefault(w => !before.Contains(w.Handle));
            if (created is not null)
            {
                _currentWindow = created.Handle;
                windows.Activate(created.Handle);
                undo?.SetUndo(_ => Task.FromResult(windows.Close(created.Handle)
                    ? $"Asked {created.Title} to close."
                    : $"{created.Title} is already closed."));
                return $"Started {candidate.DisplayName}. New window: {created}";
            }
        }

        var existing = windows.FindWindow(name);
        if (existing is not null)
        {
            _currentWindow = existing.Handle;
            windows.Activate(existing.Handle);
            return $"Started {candidate.DisplayName}; it reused an existing window: {existing}";
        }

        return $"Started {candidate.DisplayName} ({candidate.Target}), but no new window appeared yet. Use list_windows to check.";
    }

    private string ListWindows()
    {
        var list = windows.ListWindows();
        var foreground = windows.GetForegroundWindow();
        var builder = new StringBuilder();
        foreach (var window in list)
        {
            builder.Append(window.Handle == foreground?.Handle ? "* " : "- ").AppendLine(window.ToString());
        }

        return builder.Length == 0 ? "No windows." : builder.ToString();
    }

    private string FocusWindow([Description("Window title fragment or process name.")] string title)
    {
        var window = windows.FindWindow(title) ?? throw new InvalidOperationException($"No window matches '{title}'.");
        if (windows.GetForegroundWindow() is { } previous && previous.Handle != window.Handle)
        {
            undo?.SetUndo(_ =>
            {
                ActivateOrThrow(previous);
                return Task.FromResult($"Focused {previous.Title} again.");
            });
        }

        _currentWindow = window.Handle;
        return windows.Activate(window.Handle)
            ? $"Focused {window}"
            : $"Found {window}, but Windows did not allow bringing it to the foreground.";
    }

    private string UiTree(
        [Description("Window; empty = foreground")] string? window = null,
        [Description("Maximum depth (default 25).")] int maxDepth = 25)
    {
        var target = ResolveWindow(window);
        var tree = uia.DescribeTree(target.Handle, Math.Clamp(maxDepth, 1, 60), 350, TimeSpan.FromSeconds(8));
        return $"Window: {target}\n{tree}";
    }

    private string UiFind(
        [Description("Text")] string text,
        [Description("Window; empty = foreground")] string? window = null)
    {
        var target = ResolveWindow(window);
        var nodes = uia.Find(target.Handle, text, 25);
        return nodes.Count == 0
            ? $"No elements containing '{text}' in {target.Title}."
            : string.Join('\n', nodes.Select(UiAutomationService.Format));
    }

    private async Task<string> UiClickAsync(
        [Description("Element id, e.g. 'u12'.")] string id,
        [Description("What it is")] string element,
        [Description("Double-click instead of a single click.")] bool doubleClick = false,
        [Description("Right-click (context menu).")] bool rightClick = false,
        CancellationToken cancellationToken = default)
    {
        var target = uia.GetElement(id);
        var bounds = UiAutomationService.GetBounds(target);
        await GuardElementNameAsync(target, element, cancellationToken);
        ActivateCurrentWindow();

        var action = rightClick ? PointerAction.RightClick : doubleClick ? PointerAction.DoubleClick : PointerAction.Click;
        if (!bounds.IsEmpty)
        {
            await pointer.PointAsync(bounds, action, element, cancellationToken);
        }

        if (!doubleClick && !rightClick && await TryInvokePatternAsync(target))
        {
            return $"Clicked {element}.";
        }

        if (bounds.IsEmpty)
        {
            throw new InvalidOperationException($"{element} is no longer on screen (the page or window changed). Refresh ids with ui_tree or browser_snapshot.");
        }

        input.Click((int)bounds.CenterX, (int)bounds.CenterY, rightClick ? MouseButton.Right : MouseButton.Left, doubleClick);
        return $"Clicked {element} with the mouse.";
    }

    private async Task<string> UiTypeAsync(
        [Description("Element id, e.g. 'u7'.")] string id,
        [Description("Text to type.")] string text,
        [Description("What it is")] string element,
        [Description("Replace existing content instead of appending.")] bool clear = false,
        CancellationToken cancellationToken = default)
    {
        var target = uia.GetElement(id);
        var isPassword = SafeIsPassword(target);
        if (isPassword)
        {
            await guard.EnsureApprovedAsync($"Type into password field '{element}'", "Password fields are sensitive.", cancellationToken);
        }

        ActivateCurrentWindow();
        var typeBounds = UiAutomationService.GetBounds(target);
        if (!typeBounds.IsEmpty)
        {
            await pointer.PointAsync(typeBounds, PointerAction.Type, element, cancellationToken);
        }

        if (clear && target.Patterns.Value.IsSupported && !target.Patterns.Value.Pattern.IsReadOnly.ValueOrDefault)
        {
            var valuePattern = target.Patterns.Value.Pattern;
            // Passwords are never kept in memory for undo.
            if (!isPassword && valuePattern.Value.ValueOrDefault is { } previous)
            {
                undo?.SetUndo(_ =>
                {
                    valuePattern.SetValue(previous);
                    return Task.FromResult($"Restored the previous text of {element}.");
                });
            }

            valuePattern.SetValue(text);
            return $"Set {element} to the text.";
        }

        target.Focus();
        await Task.Delay(100, cancellationToken);
        if (clear)
        {
            input.SendKeys("ctrl+a");
        }

        input.TypeText(text);
        RecordTypingUndo();
        return $"Typed {text.Length} characters into {element}.";
    }

    private string TypeText([Description("Text to type. Use \\n for new lines.")] string text)
    {
        input.TypeText(text);
        RecordTypingUndo();
        return $"Typed {text.Length} characters into {windows.GetForegroundWindow()?.Title ?? "the foreground window"}.";
    }

    /// <summary>Typing is undone with Ctrl+Z in the same window (apps may undo it in one or several chunks).</summary>
    private void RecordTypingUndo()
    {
        if (windows.GetForegroundWindow() is not { } window)
        {
            return;
        }

        undo?.SetUndo(async ct =>
        {
            ActivateOrThrow(window);
            await Task.Delay(150, ct);
            input.SendKeys("ctrl+z");
            return $"Pressed Ctrl+Z in {window.Title}.";
        });
    }

    /// <summary>Undo sends keys, so it must never land in whatever window happens to be in front.</summary>
    private void ActivateOrThrow(WindowInfo window)
    {
        if (!windows.Activate(window.Handle))
        {
            throw new InvalidOperationException($"Could not bring \"{window.Title}\" to the front (it may be closed).");
        }
    }

    private string SendKeys([Description("Key chords, e.g. 'ctrl+s' or 'ctrl+shift+p'.")] string keys)
    {
        input.SendKeys(keys);
        return $"Pressed {keys}.";
    }

    private string Screenshot([Description("Window; empty = screen")] string? window = null)
    {
        var region = string.IsNullOrWhiteSpace(window) ? default : ResolveWindow(window).Bounds;
        var image = capture.CaptureForModel(region);
        var m = image.Mapping;
        observations.AddImage(image.Data, image.MediaType,
            $"Screenshot {m.ImageWidth}x{m.ImageHeight} of {(string.IsNullOrWhiteSpace(window) ? "the screen" : window)}.");
        return $"Screenshot taken ({m.ImageWidth}x{m.ImageHeight} px, attached in the next message). " +
               "Use click_at with coordinates='screenshot' to click on something you see.";
    }

    private async Task<string> OcrAsync(
        [Description("Window; empty = screen")] string? window = null,
        [Description("Language, e.g. en, uk")] string? language = null,
        CancellationToken cancellationToken = default)
    {
        var region = string.IsNullOrWhiteSpace(window) ? ScreenCaptureService.VirtualScreen : ResolveWindow(window).Bounds;
        using var bitmap = capture.CaptureBitmap(region);
        var lines = await ocr.RecognizeAsync(bitmap, region, language, cancellationToken);
        if (lines.Count == 0)
        {
            return "No text recognized.";
        }

        return string.Join('\n', lines.Take(300).Select(l => $"\"{l.Text}\" at ({l.Bounds.CenterX:0},{l.Bounds.CenterY:0})"));
    }

    private async Task<string> ClickAtAsync(
        [Description("X coordinate.")] double x,
        [Description("Y coordinate.")] double y,
        [Description("What it is")] string element,
        [Description("screenshot|screen")] string coordinates = "screenshot",
        [Description("Double-click.")] bool doubleClick = false,
        [Description("Right-click.")] bool rightClick = false,
        CancellationToken cancellationToken = default)
    {
        int screenX, screenY;
        if (coordinates.Equals("screen", StringComparison.OrdinalIgnoreCase))
        {
            (screenX, screenY) = ((int)x, (int)y);
        }
        else
        {
            var mapping = capture.LastMapping ?? throw new InvalidOperationException("Take a screenshot first.");
            (screenX, screenY) = mapping.ToScreen(x, y);
        }

        await pointer.PointAsync(ScreenRect.FromPoint(screenX, screenY),
            rightClick ? PointerAction.RightClick : doubleClick ? PointerAction.DoubleClick : PointerAction.Click, element, cancellationToken);
        input.Click(screenX, screenY, rightClick ? MouseButton.Right : MouseButton.Left, doubleClick);
        return $"Clicked {element} at screen ({screenX},{screenY}).";
    }

    private WindowInfo ResolveWindow(string? query)
    {
        WindowInfo? window;
        if (!string.IsNullOrWhiteSpace(query))
        {
            window = windows.FindWindow(query) ?? throw new InvalidOperationException($"No window matches '{query}'. Use list_windows.");
        }
        else
        {
            window = windows.GetForegroundWindow()
                ?? windows.ListWindows().FirstOrDefault(w => w.Handle == _currentWindow)
                ?? throw new InvalidOperationException("No foreground window. Use list_windows and pass a window title.");
        }

        _currentWindow = window.Handle;
        return window;
    }

    private void ActivateCurrentWindow()
    {
        if (_currentWindow != 0)
        {
            windows.Activate(_currentWindow);
        }
    }

    /// <summary>The model's description may be wrong, so also check the real element name.</summary>
    private async Task GuardElementNameAsync(AutomationElement element, string description, CancellationToken cancellationToken)
    {
        string? name = null;
        try
        {
            name = element.Name;
        }
        catch (Exception)
        {
            // Stale element; the click itself will report the failure.
        }

        if (riskPolicy.Mode == Core.Configuration.SafetyMode.ConfirmRisky &&
            !riskPolicy.TryFindRiskyKeyword(description, out _) &&
            riskPolicy.TryFindRiskyKeyword(name, out var keyword))
        {
            await guard.EnsureApprovedAsync($"Click \"{name}\"", $"The element looks risky (\"{keyword}\").", cancellationToken);
        }
    }

    private static bool SafeIsPassword(AutomationElement element)
    {
        try
        {
            return element.Properties.IsPassword.ValueOrDefault;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<bool> TryInvokePatternAsync(AutomationElement element)
    {
        // Invoke can block until a modal dialog that it opened is closed, so never wait on it forever.
        var task = Task.Run(() =>
        {
            var patterns = element.Patterns;
            if (patterns.Invoke.IsSupported)
            {
                patterns.Invoke.Pattern.Invoke();
                return true;
            }

            if (patterns.Toggle.IsSupported)
            {
                patterns.Toggle.Pattern.Toggle();
                return true;
            }

            if (patterns.SelectionItem.IsSupported)
            {
                patterns.SelectionItem.Pattern.Select();
                return true;
            }

            if (patterns.ExpandCollapse.IsSupported)
            {
                var state = patterns.ExpandCollapse.Pattern.ExpandCollapseState.ValueOrDefault;
                if (state == ExpandCollapseState.Collapsed)
                {
                    patterns.ExpandCollapse.Pattern.Expand();
                }
                else
                {
                    patterns.ExpandCollapse.Pattern.Collapse();
                }

                return true;
            }

            return false;
        });

        try
        {
            return await task.WaitAsync(PatternTimeout);
        }
        catch (TimeoutException)
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
