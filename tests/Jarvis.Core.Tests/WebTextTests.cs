using System.Net;
using Jarvis.Core.Configuration;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Tests;

public class WebTextTests
{
    private const string Page = """
        Main menu
        Kyiv is the capital and most populous city of Ukraine.
        The population of Kyiv was about 2.9 million in 2022.
        Kyiv has a humid continental climate.
        """;

    [Fact]
    public void Filter_ReturnsOnlyMatchingParagraphs_WhenQueryGiven()
    {
        // Act
        var result = TextQuery.Filter(Page, "Kyiv population", 2500);

        // Assert
        Assert.Contains("2.9 million", result);
        Assert.DoesNotContain("Main menu", result);
        Assert.StartsWith("3 matching paragraph(s):", result);
    }

    [Fact]
    public void Filter_KeepsBestParagraphsInPageOrder_WhenOverLimit()
    {
        // Act
        var result = TextQuery.Filter(Page, "population of Kyiv 2022", 60);

        // Assert
        Assert.Contains("showing 1", result);
        Assert.Contains("2.9 million", result);
        Assert.DoesNotContain("climate", result);
    }

    [Fact]
    public void Filter_ReturnsStartOfText_WhenNothingMatches()
    {
        // Act
        var result = TextQuery.Filter(Page, "zebra", 2500);

        // Assert
        Assert.StartsWith("No paragraph matches 'zebra'", result);
        Assert.Contains("Main menu", result);
    }

    [Fact]
    public void FormatWebResults_DecodesRedirectLinksAndSnippets()
    {
        // Arrange
        const string html = """
            <a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fkyiv&amp;rut=x">Kyiv <b>city</b></a>
            <a class="result__snippet" href="x">Capital &amp; largest city.</a>
            """;

        // Act
        var result = WebLookupToolProvider.FormatWebResults(html);

        // Assert
        Assert.Equal("Web results:\n- Kyiv city (https://example.com/kyiv): Capital & largest city.", result);
    }

    [Fact]
    public async Task LookupAsync_ReturnsWikipediaIntroAndMatchingParagraphs_WhenArticleFound()
    {
        // Arrange
        const string wiki = """
            {"query":{"pages":[{"title":"Kyiv","fullurl":"https://en.wikipedia.org/wiki/Kyiv",
            "extract":"Kyiv is the capital of Ukraine.\n\n\n== Demographics ==\nThe population was 2.9 million.\nUnrelated text here."}]}}
            """;
        var handler = new StubHandler(uri => uri.Host.EndsWith("wikipedia.org", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(wiki) }
            : new HttpResponseMessage(HttpStatusCode.Forbidden));
        var provider = new WebLookupToolProvider(Options.Create(new JarvisOptions()), NullLogger<WebLookupToolProvider>.Instance,
            new HttpClient(handler));

        // Act
        var result = await provider.LookupAsync("Kyiv population", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.StartsWith("Wikipedia: Kyiv (https://en.wikipedia.org/wiki/Kyiv)", result);
        Assert.Contains("Kyiv is the capital of Ukraine.", result);
        Assert.Contains("The population was 2.9 million.", result);
        Assert.DoesNotContain("Unrelated", result);
        Assert.DoesNotContain("Demographics", result);
        Assert.Contains(handler.Requests, u => u.Host == "en.wikipedia.org");
    }

    private sealed class StubHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!);
            }

            return Task.FromResult(respond(request.RequestUri!));
        }
    }
}
