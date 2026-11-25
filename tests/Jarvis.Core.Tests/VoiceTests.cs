using Jarvis.Voice;

namespace Jarvis.Core.Tests;

public class VoiceTests
{
    private static readonly TimeSpan Chunk = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void Process_ReturnsTrue_WhenSilenceFollowsSpeech()
    {
        // Arrange
        var detector = new SpeechEndDetector(0.02f, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));
        detector.Process(0.1f, Chunk);

        // Act
        var results = Enumerable.Range(0, 4).Select(_ => detector.Process(0.001f, Chunk)).ToList();

        // Assert
        Assert.Equal([false, false, false, true], results);
        Assert.True(detector.SpeechDetected);
    }

    [Fact]
    public void Process_ReturnsTrue_WhenNobodySpeaksBeforeTimeout()
    {
        // Arrange
        var detector = new SpeechEndDetector(0.02f, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100));

        // Act
        var first = detector.Process(0.001f, Chunk);
        var second = detector.Process(0.001f, Chunk);

        // Assert
        Assert.False(first);
        Assert.True(second);
        Assert.False(detector.SpeechDetected);
    }

    [Theory]
    [InlineData(" [BLANK_AUDIO] ", "")]
    [InlineData(" Open Chrome (music) please", "Open Chrome please")]
    [InlineData(" Відкрий   Excel", "Відкрий Excel")]
    public void CleanUp_RemovesNonSpeechMarkers_WhenWhisperAddsThem(string raw, string expected)
    {
        // Act
        var result = WhisperTranscriber.CleanUp(raw);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("small", Whisper.net.Ggml.GgmlType.Small)]
    [InlineData("large-v3-turbo", Whisper.net.Ggml.GgmlType.LargeV3Turbo)]
    public void ParseModel_ReturnsGgmlType_WhenNameIsKnown(string name, Whisper.net.Ggml.GgmlType expected)
    {
        // Act
        var result = WhisperTranscriber.ParseModel(name);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(new[] { "en", "uk" }, "uk")]
    [InlineData(new[] { "UK", "en" }, "uk")]
    [InlineData(new[] { "en", "uk", "ru" }, null)]
    [InlineData(new[] { "en", "de" }, null)]
    public void SingleCyrillicLanguage_ReturnsLanguage_WhenExactlyOneCyrillicLanguageIsAllowed(string[] allowed, string? expected)
    {
        // Arrange
        var languages = allowed;

        // Act
        var result = HybridTranscriber.SingleCyrillicLanguage(languages);

        // Assert
        Assert.Equal(expected, result);
    }
}
