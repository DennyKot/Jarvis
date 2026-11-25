using Jarvis.Core.Agent;

namespace Jarvis.Core.Tests;

public class ReplyLinksTests
{
    [Fact]
    public void Parse_ReturnsNamedLink_WhenMarkdownLinkGiven()
    {
        // Act
        var segments = ReplyLinks.Parse("Kyiv has 2.9M people.\nSource: [Wikipedia](https://en.wikipedia.org/wiki/Kyiv)");

        // Assert
        Assert.Equal(
            [new ReplySegment("Kyiv has 2.9M people.\nSource: "), new ReplySegment("Wikipedia", new Uri("https://en.wikipedia.org/wiki/Kyiv"))],
            segments);
    }

    [Fact]
    public void Parse_ShowsHostAndKeepsTrailingPunctuation_WhenBareUrlGiven()
    {
        // Act
        var segments = ReplyLinks.Parse("See https://www.example.com/a?b=1.");

        // Assert
        Assert.Equal(
            [new ReplySegment("See "), new ReplySegment("example.com", new Uri("https://www.example.com/a?b=1")), new ReplySegment(".")],
            segments);
    }

    [Fact]
    public void Parse_ReturnsPlainText_WhenNoLinks()
    {
        // Act
        var segments = ReplyLinks.Parse("Done.");

        // Assert
        Assert.Equal([new ReplySegment("Done.")], segments);
    }
}
