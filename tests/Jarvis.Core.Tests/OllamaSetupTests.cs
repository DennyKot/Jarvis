using Jarvis.Core.Llm;

namespace Jarvis.Core.Tests;

public class OllamaSetupTests
{
    [Theory]
    [InlineData("gemma4:latest", "gemma4", true)]
    [InlineData("gemma4", "GEMMA4:latest", true)]
    [InlineData("gemma4:27b", "gemma4", false)]
    [InlineData("gemma4:latest", "gemma3", false)]
    public void IsSameModel_MatchesWithImplicitLatestTag_WhenTagOmitted(string installed, string configured, bool expected)
    {
        // Arrange
        // Act
        var result = OllamaSetup.IsSameModel(installed, configured);

        // Assert
        Assert.Equal(expected, result);
    }
}
