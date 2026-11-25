using Jarvis.Core.Configuration;

namespace Jarvis.Core.Agent;

internal enum ToolGroup
{
    /// <summary>Always sent: entry points into the web and desktop groups.</summary>
    Core,
    Web,
    Desktop,
}

/// <summary>
/// Splits the tools into groups so each step sends only what the current task needs
/// (web tasks don't need ocr/ui_tree, desktop tasks don't need browser_click).
/// </summary>
internal static class ToolGroups
{
    private static readonly HashSet<string> CoreTools = ["web_lookup", "browser_open", "launch_app",     "focus_window", "list_windows", "undo_last_action"];

    private static readonly HashSet<string> DesktopTools =
        ["ui_tree", "ui_find", "ui_click", "ui_type", "type_text", "send_keys", "screenshot", "ocr", "click_at"];

    public static ToolGroup Of(string toolName) =>
        CoreTools.Contains(toolName) ? ToolGroup.Core
        : toolName.StartsWith("browser_", StringComparison.Ordinal) ? ToolGroup.Web
        : DesktopTools.Contains(toolName) ? ToolGroup.Desktop
        : ToolGroup.Core;

    /// <summary>Groups the model is about to need after calling <paramref name="toolName"/>.</summary>
    public static IEnumerable<ToolGroup> ActivatedBy(string toolName, BrowserMode browserMode)
    {
        var group = toolName switch
        {
            "browser_open" => ToolGroup.Web,
            "launch_app" or "focus_window" or "list_windows" => ToolGroup.Desktop,
            _ => Of(toolName),
        };
        yield return group;

        // The user's own Chrome window is driven with the desktop tools (ui_click, send_keys, ocr).
        if (group == ToolGroup.Web && browserMode == BrowserMode.UserWindow)
        {
            yield return ToolGroup.Desktop;
        }
    }
}
