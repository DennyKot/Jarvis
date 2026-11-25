using System.Text;

namespace Jarvis.Automation.Browser;

/// <summary>Describes a page snapshot relative to the previous one, so actions return only what changed.</summary>
internal static class SnapshotDiff
{
    /// <returns>A compact description, or null when so much changed that a full snapshot is cheaper to read.</returns>
    public static string? Describe(IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        var before = previous.ToHashSet(StringComparer.Ordinal);
        var after = current.ToHashSet(StringComparer.Ordinal);
        var added = current.Where(l => !before.Contains(l)).ToList();
        var removed = previous.Count(l => !after.Contains(l));

        if (added.Count == 0 && removed == 0)
        {
            return "No visible changes since the last snapshot; all refs are still valid.";
        }

        if (added.Count > Math.Max(10, current.Count / 2))
        {
            return null;
        }

        var builder = new StringBuilder()
            .Append("Changes since the last snapshot (").Append(removed)
            .AppendLine(" items gone; refs of unchanged elements are still valid). New or changed:");
        foreach (var line in added)
        {
            builder.AppendLine(line);
        }

        return builder.ToString();
    }
}
