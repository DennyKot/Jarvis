using System.Text;
using System.Text.RegularExpressions;

namespace Jarvis.Core.Tools;

/// <summary>Cuts long page text down to the paragraphs that match a query, so reading a page costs few tokens.</summary>
public static partial class TextQuery
{
    /// <summary>Text to fetch before filtering when a query is given (the filtered result is still capped by the limit).</summary>
    public const int SourceLimit = 200_000;

    public static string Truncate(string text, int limit) =>
        text.Length > limit ? text[..limit] + "\n…(truncated)" : text;

    /// <summary>
    /// Returns paragraphs containing the most query words (in page order, best first when over the limit),
    /// or the start of the text when nothing matches.
    /// </summary>
    public static string Filter(string text, string? query, int limit)
    {
        var terms = Terms(query);
        if (terms.Count == 0)
        {
            return Truncate(text, limit);
        }

        var paragraphs = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var scored = paragraphs
            .Select((p, i) => (Index: i, Text: p, Score: terms.Count(t => p.Contains(t, StringComparison.OrdinalIgnoreCase))))
            .Where(p => p.Score > 0 && p.Text.Length > 2)
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Index)
            .ToList();
        if (scored.Count == 0)
        {
            return $"No paragraph matches '{query}'. Start of the page:\n{Truncate(text, Math.Min(limit, 1500))}";
        }

        var picked = new List<(int Index, string Text)>();
        var used = 0;
        foreach (var (index, paragraph, _) in scored)
        {
            var piece = paragraph.Length > 600 ? paragraph[..600] + "…" : paragraph;
            if (used + piece.Length > limit && picked.Count > 0)
            {
                break;
            }

            picked.Add((index, piece));
            used += piece.Length + 1;
        }

        var builder = new StringBuilder();
        builder.Append(scored.Count).Append(" matching paragraph(s)");
        if (picked.Count < scored.Count)
        {
            builder.Append(", showing ").Append(picked.Count);
        }

        builder.AppendLine(":");
        foreach (var (_, piece) in picked.OrderBy(p => p.Index))
        {
            builder.AppendLine(piece);
        }

        return builder.ToString();
    }

    private static List<string> Terms(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : WordRegex().Matches(query).Select(m => m.Value).Where(w => w.Length > 2 || char.IsDigit(w[0])).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();
}
