using System.Runtime.InteropServices;
using Jarvis.Automation.Browser;
using Jarvis.Automation.Desktop.Input;
using Jarvis.Automation.Desktop.Interop;
using Jarvis.Automation.Desktop.Screen;
using Jarvis.Core.Tools;

namespace Jarvis.Core.Tests;

public class AutomationHelpersTests
{
    [Fact]
    public void Parse_ReturnsModifiersAndKey_WhenChordHasModifiers()
    {
        // Arrange
        const string chord = "ctrl+shift+s";

        // Act
        var result = KeyChordParser.Parse(chord);

        // Assert
        Assert.Equal([(ushort)0x11, (ushort)0x10], result.Modifiers);
        Assert.Equal((ushort)'S', result.Key);
    }

    [Theory]
    [InlineData("f5", 0x74)]
    [InlineData("Enter", 0x0D)]
    [InlineData("alt", 0x12)]
    [InlineData("ctrl++", 0xBB)]
    public void Parse_ReturnsVirtualKey_WhenKeyIsNamed(string chord, int expected)
    {
        // Act
        var result = KeyChordParser.Parse(chord);

        // Assert
        Assert.Equal((ushort)expected, result.Key);
    }

    [Fact]
    public void ParseSequence_ReturnsEachChord_WhenSeparatedBySpaces()
    {
        // Act
        var result = KeyChordParser.ParseSequence("ctrl+k ctrl+o");

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal((ushort)'O', result[1].Key);
    }

    [Fact]
    public void Parse_Throws_WhenModifierIsUnknown()
    {
        // Act & Assert
        Assert.Throws<FormatException>(() => KeyChordParser.Parse("hyper+s"));
    }

    [Fact]
    public void SizeOf_MatchesNativeInputStruct_OnCurrentPlatform()
    {
        // Act
        var size = Marshal.SizeOf<INPUT>();

        // Assert: sizeof(INPUT) is 40 bytes on 64-bit Windows and 28 on 32-bit.
        Assert.Equal(Environment.Is64BitProcess ? 40 : 28, size);
    }

    [Fact]
    public void ToScreen_MapsCssPixelsToPhysicalPixels_WhenDisplayIsScaled()
    {
        // Arrange: a 1920x1040 Chrome window at 150% DPI with a 1280x600 CSS viewport.
        var window = new ScreenRect(0, 0, 1920, 1040);
        var viewport = new ViewportMetrics(1280, 600, 1.5);

        // Act
        var result = ViewportMapper.ToScreen(window, viewport, new ScreenRect(100, 50, 20, 10));

        // Assert: viewport is 1920x900 px, so it starts 140 px below the window top.
        Assert.Equal(new ScreenRect(150, 215, 30, 15), result);
    }

    [Theory]
    [InlineData("en.wikipedia.org/wiki/Kyiv", "https://en.wikipedia.org/wiki/Kyiv")]
    [InlineData("localhost:3000", "https://localhost:3000")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("data:text/html,<b>hi</b>", "data:text/html,<b>hi</b>")]
    [InlineData("http://example.com", "http://example.com")]
    public void NormalizeUrl_AddsHttps_WhenSchemeIsMissing(string input, string expected)
    {
        // Act
        var result = BrowserToolProvider.NormalizeUrl(input);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Describe_ReturnsOnlyNewLines_WhenFewElementsChanged()
    {
        // Arrange
        var previous = Enumerable.Range(1, 20).Select(i => $"[e{i}] link \"Item {i}\"").ToList();
        var current = previous.Skip(1).Append("[e21] button \"Load more\"").ToList();

        // Act
        var result = SnapshotDiff.Describe(previous, current);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("1 items gone", result);
        Assert.Contains("[e21] button \"Load more\"", result);
        Assert.DoesNotContain("Item 5", result);
    }

    [Fact]
    public void AppendSnapshot_OmitsTextLines_WhenExcerptIsIncluded()
    {
        // Arrange
        string[] lines = ["[e1] searchbox \"Search\"", "heading: Kyiv", "text: Kyiv is the capital of Ukraine."];

        // Act
        var result = BrowserToolProvider.AppendSnapshot(new System.Text.StringBuilder(), lines, truncated: false, "Kyiv is the capital of Ukraine.").ToString();

        // Assert
        Assert.Contains("[e1] searchbox \"Search\"", result);
        Assert.Contains("heading: Kyiv", result);
        Assert.DoesNotContain("text: ", result);
        Assert.Contains("Page text (start):", result);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, "capital of Ukraine"));
    }

    [Fact]
    public void AppendSnapshot_KeepsTextLines_WhenNoExcerpt()
    {
        // Arrange
        string[] lines = ["[e1] link \"Home\"", "text: Welcome to the site."];

        // Act
        var result = BrowserToolProvider.AppendSnapshot(new System.Text.StringBuilder(), lines, truncated: true, excerpt: null).ToString();

        // Assert
        Assert.Contains("text: Welcome to the site.", result);
        Assert.Contains("more elements below", result);
        Assert.DoesNotContain("Page text", result);
    }

    [Fact]
    public void Describe_ReturnsNull_WhenMostOfThePageChanged()
    {
        // Arrange
        var previous = Enumerable.Range(1, 30).Select(i => $"old {i}").ToList();
        var current = Enumerable.Range(1, 30).Select(i => $"new {i}").ToList();

        // Act
        var result = SnapshotDiff.Describe(previous, current);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData(Jarvis.Core.Configuration.BrowserMode.JarvisProfile, true)]
    [InlineData(Jarvis.Core.Configuration.BrowserMode.UserWindow, false)]
    public void GetTools_ExposesDevToolsBrowserTools_OnlyInJarvisProfileMode(Jarvis.Core.Configuration.BrowserMode mode, bool expected)
    {
        // Arrange
        var options = Microsoft.Extensions.Options.Options.Create(new Jarvis.Core.Configuration.JarvisOptions
        {
            Browser = new Jarvis.Core.Configuration.BrowserOptions { Mode = mode },
        });
        var provider = new BrowserToolProvider(null!, null!, null!, null!, null!, options, null!);

        // Act
        var tools = provider.GetTools().ToList();

        // Assert
        Assert.Equal(expected, tools.Count > 0);
    }

    [Fact]
    public void ToScreen_ScalesAndOffsets_WhenScreenshotWasDownscaled()
    {
        // Arrange
        var mapping = new ScreenshotMapping(new ScreenRect(-1920, 100, 3136, 1764), 2.0, 1568, 882);

        // Act
        var (x, y) = mapping.ToScreen(100, 50);

        // Assert
        Assert.Equal((-1720, 200), (x, y));
    }
}
