using System.Text.RegularExpressions;

namespace Jarvis.Core.Agent;

/// <summary>A piece of a reply: plain text, or a link shown as <see cref="Text"/> that opens <see cref="Url"/>.</summary>
public sealed record ReplySegment(string Text, Uri? Url = null);

/// <summary>Splits a reply into text and links: Markdown links [Site](url) and bare http(s) URLs (shown as their host).</summary>
public static partial class ReplyLinks
{
    public static IReadOnlyList<ReplySegment> Parse(string text)
    {
        var segments = new List<ReplySegment>();
        var position = 0;
        foreach (Match match in LinkRegex().Matches(text))
        {
            var url = match.Groups["url"].Success ? match.Groups["url"].Value : match.Groups["bare"].Value.TrimEnd('.', ',', ';', ':', '!', '?');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                continue;
            }

            if (match.Index > position)
            {
                segments.Add(new ReplySegment(text[position..match.Index]));
            }

            var label = match.Groups["label"].Success ? match.Groups["label"].Value.Trim() : uri.Host.StartsWith("www.", StringComparison.Ordinal) ? uri.Host[4..] : uri.Host;
            segments.Add(new ReplySegment(label, uri));
            position = match.Index + (match.Groups["url"].Success ? match.Length : url.Length);
        }

        if (position < text.Length)
        {
            segments.Add(new ReplySegment(text[position..]));
        }

        return segments;
    }

    [GeneratedRegex(@"\[(?<label>[^\]\n]+)\]\((?<url>https?://[^\s)]+)\)|(?<bare>https?://[^\s<>()\[\]]+)")]
    private static partial Regex LinkRegex();
}
