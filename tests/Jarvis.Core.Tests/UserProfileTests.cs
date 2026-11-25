using Jarvis.Core.Agent;
using Jarvis.Voice;

namespace Jarvis.Core.Tests;

public class UserProfileTests
{
    [Fact]
    public void Pick_ReturnsMajorityFirstName_WhenSourcesDisagree()
    {
        // Arrange
        NameCandidate[] candidates =
        [
            new("denys kotenko", "Windows user name", 2),
            new("Denys Kot", "Chrome profile", 1),
            new("Piano Tutorials", "Chrome profile", 1),
            new("Denny Kote", "Chrome profile", 1),
        ];

        // Act
        var result = UserProfileService.Pick(candidates);

        // Assert
        Assert.Equal("Denys", result.FirstName);
        Assert.Equal("Denys Kotenko", result.FullName);
    }

    [Fact]
    public void Pick_ReturnsEmptyProfile_WhenNoUsableNames()
    {
        // Arrange
        NameCandidate[] candidates = [new("admin123", "Windows user name", 2)];

        // Act
        var result = UserProfileService.Pick(candidates);

        // Assert
        Assert.Null(result.FirstName);
    }

    [Theory]
    [InlineData("Відкрий ютуб", true)]
    [InlineData("Open YouTube", false)]
    public void ContainsCyrillic_DetectsCyrillicText_WhenPresent(string text, bool expected)
    {
        // Act
        var result = HybridTranscriber.ContainsCyrillic(text);

        // Assert
        Assert.Equal(expected, result);
    }
}
