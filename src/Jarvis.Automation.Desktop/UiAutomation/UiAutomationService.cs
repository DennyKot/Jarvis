using System.Diagnostics;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Jarvis.Core.Tools;

namespace Jarvis.Automation.Desktop.UiAutomation;

public sealed record UiNode(string Id, string ControlType, string Name, string? AutomationId, string? Value, bool IsEnabled, ScreenRect Bounds);

/// <summary>Reads UI Automation trees and remembers element ids ("u12") so the model can act on them.</summary>
public sealed class UiAutomationService : IDisposable
{
    private static readonly HashSet<ControlType> Transparent = [ControlType.Pane, ControlType.Group, ControlType.Custom];
    private static readonly HashSet<ControlType> Skipped = [ControlType.ScrollBar, ControlType.Thumb, ControlType.TitleBar, ControlType.Separator];

    private readonly UIA3Automation _automation = new();
    private readonly Dictionary<string, AutomationElement> _elements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public UIA3Automation Automation => _automation;

    public string DescribeTree(nint windowHandle, int maxDepth, int maxElements, TimeSpan budget)
    {
        lock (_lock)
        {
            _elements.Clear();
            var root = _automation.FromHandle(windowHandle);
            var builder = new StringBuilder();
            var stopwatch = Stopwatch.StartNew();
            var count = 0;
            Walk(root, 0, maxDepth, maxElements, budget, stopwatch, builder, ref count);

            // Chromium/Electron apps (VS Code, Teams, Chrome) build their accessibility tree only after
            // the first UI Automation request, so an almost empty tree is worth one more look.
            if (count < 8)
            {
                Thread.Sleep(1500);
                _elements.Clear();
                builder.Clear();
                count = 0;
                stopwatch.Restart();
                Walk(root, 0, maxDepth, maxElements, budget, stopwatch, builder, ref count);
            }

            if (count >= maxElements || stopwatch.Elapsed > budget)
            {
                builder.AppendLine("… (tree truncated; use a more specific window or ui_find to narrow down)");
            }

            return builder.ToString();
        }
    }

    /// <summary>Describes only the web page inside a browser window (its Document element), not the browser chrome.</summary>
    public string? DescribeDocument(nint windowHandle, int maxElements, TimeSpan budget)
    {
        lock (_lock)
        {
            var document = FindDocument(windowHandle);
            if (document is null)
            {
                return null;
            }

            _elements.Clear();
            var builder = new StringBuilder();
            var count = 0;
            Walk(document, 0, 40, maxElements, budget, Stopwatch.StartNew(), builder, ref count);
            if (count >= maxElements)
            {
                builder.AppendLine("… (more elements; use ui_find to search for a specific one)");
            }

            return builder.ToString();
        }
    }

    /// <summary>Plain text of the web page in a browser window via the UIA Text pattern.</summary>
    public string? ReadDocumentText(nint windowHandle, int maxLength)
    {
        lock (_lock)
        {
            var document = FindDocument(windowHandle);
            if (document is null || SafeValue(() => document.Patterns.Text.IsSupported) != true)
            {
                return null;
            }

            return Safe(() => document.Patterns.Text.Pattern.DocumentRange.GetText(maxLength));
        }
    }

    private AutomationElement? FindDocument(nint windowHandle)
    {
        var root = _automation.FromHandle(windowHandle);
        var document = Safe<AutomationElement>(() => root.FindFirstDescendant(cf => cf.ByControlType(ControlType.Document))!);
        if (document is null)
        {
            // Chromium builds its accessibility tree lazily after the first request.
            Thread.Sleep(1500);
            document = Safe<AutomationElement>(() => root.FindFirstDescendant(cf => cf.ByControlType(ControlType.Document))!);
        }

        return document;
    }

    public IReadOnlyList<UiNode> Find(nint windowHandle, string text, int maxResults)
    {
        lock (_lock)
        {
            var root = _automation.FromHandle(windowHandle);
            var results = new List<UiNode>();
            foreach (var element in root.FindAllDescendants())
            {
                var name = Safe(() => element.Name) ?? string.Empty;
                var automationId = Safe(() => element.AutomationId);
                if (!name.Contains(text, StringComparison.OrdinalIgnoreCase) &&
                    automationId?.Contains(text, StringComparison.OrdinalIgnoreCase) != true)
                {
                    continue;
                }

                if (SafeValue(() => element.IsOffscreen) == true)
                {
                    continue;
                }

                results.Add(Register(element));
                if (results.Count >= maxResults)
                {
                    break;
                }
            }

            return results;
        }
    }

    public AutomationElement GetElement(string id)
    {
        lock (_lock)
        {
            return _elements.TryGetValue(id.Trim(), out var element)
                ? element
                : throw new InvalidOperationException($"Unknown element id '{id}'. Call ui_tree or ui_find again to get fresh ids.");
        }
    }

    public static ScreenRect GetBounds(AutomationElement element)
    {
        var r = element.BoundingRectangle;
        return new ScreenRect(r.X, r.Y, r.Width, r.Height);
    }

    public void Dispose() => _automation.Dispose();

    private void Walk(AutomationElement element, int depth, int maxDepth, int maxElements, TimeSpan budget,
        Stopwatch stopwatch, StringBuilder builder, ref int count)
    {
        if (count >= maxElements || stopwatch.Elapsed > budget)
        {
            return;
        }

        var controlType = SafeValue(() => element.ControlType) ?? ControlType.Unknown;
        if (Skipped.Contains(controlType) || (depth > 0 && SafeValue(() => element.IsOffscreen) == true))
        {
            return;
        }

        var name = Safe(() => element.Name) ?? string.Empty;
        var transparent = depth > 0 && Transparent.Contains(controlType) && string.IsNullOrWhiteSpace(name);
        var childDepth = depth;
        if (!transparent)
        {
            var node = Register(element);
            builder.Append(' ', depth * 2).AppendLine(Format(node));
            count++;
            childDepth = depth + 1;
        }

        if (childDepth > maxDepth)
        {
            return;
        }

        foreach (var child in Safe(() => element.FindAllChildren()) ?? [])
        {
            Walk(child, childDepth, maxDepth, maxElements, budget, stopwatch, builder, ref count);
        }
    }

    private UiNode Register(AutomationElement element)
    {
        var id = $"u{_elements.Count + 1}";
        _elements[id] = element;

        string? value = null;
        if (SafeValue(() => element.Patterns.Value.IsSupported) == true)
        {
            value = Safe(() => element.Patterns.Value.Pattern.Value.Value);
        }

        return new UiNode(
            id,
            (SafeValue(() => element.ControlType) ?? ControlType.Unknown).ToString(),
            Safe(() => element.Name) ?? string.Empty,
            Safe(() => element.AutomationId),
            value,
            SafeValue(() => element.IsEnabled) ?? true,
            SafeValue(() => GetBounds(element)) ?? default);
    }

    internal static string Format(UiNode node)
    {
        var builder = new StringBuilder($"[{node.Id}] {node.ControlType}");
        if (!string.IsNullOrWhiteSpace(node.Name))
        {
            builder.Append(" \"").Append(Truncate(node.Name, 80)).Append('"');
        }

        if (!string.IsNullOrWhiteSpace(node.AutomationId) && !int.TryParse(node.AutomationId, out _))
        {
            builder.Append(" id=").Append(Truncate(node.AutomationId, 40));
        }

        if (!string.IsNullOrEmpty(node.Value))
        {
            builder.Append(" value=\"").Append(Truncate(node.Value, 120)).Append('"');
        }

        if (!node.IsEnabled)
        {
            builder.Append(" (disabled)");
        }

        return builder.ToString();
    }

    private static string Truncate(string text, int length)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= length ? singleLine : singleLine[..length] + "…";
    }

    private static T? SafeValue<T>(Func<T> getter)
        where T : struct
    {
        try
        {
            return getter();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static T? Safe<T>(Func<T> getter)
        where T : class
    {
        try
        {
            return getter();
        }
        catch (Exception)
        {
            // UIA providers throw for stale or unsupported properties; treat as missing.
            return default;
        }
    }
}
