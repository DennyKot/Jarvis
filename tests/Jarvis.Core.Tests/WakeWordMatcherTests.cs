using Jarvis.Voice;

namespace Jarvis.Core.Tests;

public sealed class WakeWordMatcherTests
{
    private static readonly string[] WakeWords = ["hey jarvis", "jarvis", "привіт джарвіс"];

    [Theory]
    [InlineData("Hey, Jarvis! Open notepad.", "Open notepad.")]
    [InlineData("hey jarvis", "")]
    [InlineData("Jervis, what time is it?", "what time is it?")]
    [InlineData("Привіт, Джарвіс, відкрий Excel", "відкрий Excel")]
    public void TryMatch_ReturnsCommand_WhenTranscriptStartsWithWakePhrase(string transcript, string expected)
    {
        // Arrange & Act
        var matched = WakeWordMatcher.TryMatch(transcript, WakeWords, out var command);

        // Assert
        Assert.True(matched);
        Assert.Equal(expected, command);
    }

    [Theory]
    [InlineData("I told Jarvis about it")]
    [InlineData("Hey there")]
    [InlineData("")]
    public void TryMatch_ReturnsFalse_WhenWakePhraseIsNotAtStart(string transcript)
    {
        // Arrange & Act
        var matched = WakeWordMatcher.TryMatch(transcript, WakeWords, out _);

        // Assert
        Assert.False(matched);
    }
}
