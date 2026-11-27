using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Tools;

/// <summary>
/// Answers factual questions in one step: searches Wikipedia and the web over HTTP (no browser) and returns readable text.
/// </summary>
public sealed partial class WebLookupToolProvider(
    IOptions<JarvisOptions> options,
    ILogger<WebLookupToolProvider> logger,
    HttpClient? httpClient = null) : IToolProvider
{
    private const int IntroLength = 700;
    private const int MaxWebResults = 4;

    private readonly HttpClient _http = httpClient ?? CreateHttpClient();

    public IEnumerable<JarvisTool> GetTools() =>
    [
        JarvisTool.Create(LookupAsync, "web_lookup",
            "Search the web + Wikipedia and return readable text. Best first step for factual questions.", ToolRisk.ReadOnly),
    ];

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Jarvis/1.0");
        return client;
    }

    internal async Task<string> LookupAsync(
        [Description("Search query")] string query,
        [Description("Wikipedia language code, e.g. 'en', 'uk' (default: from query)")] string? language = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        language = string.IsNullOrWhiteSpace(language) ? (CyrillicRegex().IsMatch(query) ? "uk" : "en") : language.Trim().ToLowerInvariant();

        var wikipedia = TryAsync(() => WikipediaAsync(query, language, cancellationToken), "Wikipedia");
        var web = TryAsync(() => WebResultsAsync(query, cancellationToken), "web search");
        var parts = (await Task.WhenAll(wikipedia, web)).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (parts.Count == 0)
        {
            return $"Nothing found for '{query}'. Try other words, or open a site with browser_open.";
        }

        var sources = SourceRegex().Matches(string.Join('\n', parts))
            .Select(m => (Name: m.Groups["name"].Value.Trim(), Url: m.Groups["url"].Value))
            .DistinctBy(s => s.Url)
            .Take(2)
            .Select(s => $"[{(s.Name.StartsWith("Wikipedia: ", StringComparison.Ordinal) ? "Wikipedia" : s.Name)}]({s.Url})");
        return string.Join("\n\n", parts) + "\n" + SourcePrefix + string.Join(", ", sources);
    }

    /// <summary>Last line of every web_lookup result; the agent appends it to the answer if the model forgot to cite.</summary>
    public const string SourcePrefix = "Source: ";

    [GeneratedRegex(@"^(?:- )?(?<name>[^\n(]+?) \((?<url>https?://[^\s)]+)\)", RegexOptions.Multiline)]
    private static partial Regex SourceRegex();

    private async Task<string?> TryAsync(Func<Task<string?>> action, string source)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogDebug(ex, "web_lookup: {Source} failed", source);
            return null;
        }
    }

    private async Task<string?> WikipediaAsync(string query, string language, CancellationToken cancellationToken)
    {
        var url = $"https://{Uri.EscapeDataString(language)}.wikipedia.org/w/api.php?action=query&format=json&formatversion=2" +
                  $"&generator=search&gsrlimit=1&gsrsearch={Uri.EscapeDataString(query)}&prop=extracts|info&inprop=url&explaintext=1&redirects=1";
        using var json = JsonDocument.Parse(await _http.GetStringAsync(url, cancellationToken));
        if (!json.RootElement.TryGetProperty("query", out var result) || !result.TryGetProperty("pages", out var pages) ||
            pages.GetArrayLength() == 0)
        {
            return null;
        }

        var page = pages[0];
        var title = page.GetProperty("title").GetString();
        var pageUrl = page.TryGetProperty("fullurl", out var u) ? u.GetString() : null;
        var extract = page.TryGetProperty("extract", out var e) ? e.GetString() ?? string.Empty : string.Empty;
        return FormatArticle(title, pageUrl, extract, query, options.Value.Browser.ReadTextLimit);
    }

    internal static string FormatArticle(string? title, string? url, string extract, string query, int limit)
    {
        var text = SectionHeadingRegex().Replace(extract, "\n");
        var firstBreak = text.IndexOf('\n', StringComparison.Ordinal);
        var intro = (firstBreak > 0 ? text[..firstBreak] : text).Trim();
        if (intro.Length > IntroLength)
        {
            intro = intro[..IntroLength] + "…";
        }

        var builder = new StringBuilder().Append("Wikipedia: ").Append(title).Append(" (").Append(url).AppendLine(")").AppendLine(intro);
        var rest = firstBreak > 0 ? text[firstBreak..] : string.Empty;
        if (rest.Length > 0 && limit - intro.Length > 300)
        {
            var matches = TextQuery.Filter(rest, query, limit - intro.Length);
            if (!matches.StartsWith("No paragraph", StringComparison.Ordinal))
            {
                builder.Append(matches);
            }
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<string?> WebResultsAsync(string query, CancellationToken cancellationToken)
    {
        var html = await _http.GetStringAsync("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query), cancellationToken);
        return FormatWebResults(html);
    }

    internal static string? FormatWebResults(string html)
    {
        var titles = ResultTitleRegex().Matches(html);
        var snippets = ResultSnippetRegex().Matches(html);
        if (titles.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder("Web results:\n");
        for (var i = 0; i < Math.Min(MaxWebResults, titles.Count); i++)
        {
            builder.Append("- ").Append(Clean(titles[i].Groups["title"].Value))
                .Append(" (").Append(ResultUrl(titles[i].Groups["href"].Value)).Append(')');
            if (i < snippets.Count)
            {
                builder.Append(": ").Append(Clean(snippets[i].Groups["text"].Value));
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>DuckDuckGo wraps result links in a redirect (//duckduckgo.com/l/?uddg=...).</summary>
    private static string ResultUrl(string href)
    {
        href = WebUtility.HtmlDecode(href);
        var marker = href.IndexOf("uddg=", StringComparison.Ordinal);
        if (marker < 0)
        {
            return href;
        }

        var encoded = href[(marker + 5)..];
        var end = encoded.IndexOf('&', StringComparison.Ordinal);
        return Uri.UnescapeDataString(end >= 0 ? encoded[..end] : encoded);
    }

    private static string Clean(string html) =>
        WhitespaceRegex().Replace(WebUtility.HtmlDecode(TagRegex().Replace(html, string.Empty)), " ").Trim();

    [GeneratedRegex(@"\p{IsCyrillic}")]
    private static partial Regex CyrillicRegex();

    [GeneratedRegex(@"\n*\s*={2,}\s*[^=\n]+?\s*={2,}\s*\n*")]
    private static partial Regex SectionHeadingRegex();

    [GeneratedRegex("""class="result__a"[^>]*href="(?<href>[^"]+)"[^>]*>(?<title>.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex ResultTitleRegex();

    [GeneratedRegex("""class="result__snippet"[^>]*>(?<text>.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex ResultSnippetRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
