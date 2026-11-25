using Jarvis.Core.Configuration;
using Jarvis.Core.Safety;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Tests;

public class RiskPolicyTests
{
    private static RiskPolicy CreatePolicy(SafetyMode mode = SafetyMode.ConfirmRisky) =>
        new(Options.Create(new JarvisOptions { Safety = new SafetyOptions { Mode = mode } }));

    private static JarvisTool CreateTool(ToolRisk risk) =>
        JarvisTool.Create((string element) => "ok", "test_click", "Test tool", risk, "element");

    private static Dictionary<string, object?> Args(string element) => new() { ["element"] = element };

    [Theory]
    [InlineData("Send email button")]
    [InlineData("Delete repository")]
    [InlineData("Кнопка Видалити")]
    [InlineData("Надіслати лист")]
    public void Evaluate_RequiresConfirmation_WhenTargetContainsRiskyKeyword(string element)
    {
        // Arrange
        var policy = CreatePolicy();
        var tool = CreateTool(ToolRisk.Action);

        // Act
        var result = policy.Evaluate(tool, Args(element));

        // Assert
        Assert.True(result.RequiresConfirmation);
    }

    [Theory]
    [InlineData("Border settings")]
    [InlineData("Search box")]
    [InlineData("Reorder columns")]
    public void Evaluate_Allowed_WhenKeywordOnlyAppearsInsideAnotherWord(string element)
    {
        // Arrange
        var policy = CreatePolicy();
        var tool = CreateTool(ToolRisk.Action);

        // Act
        var result = policy.Evaluate(tool, Args(element));

        // Assert
        Assert.False(result.RequiresConfirmation);
    }

    [Fact]
    public void Evaluate_RequiresConfirmation_WhenToolIsMarkedRisky()
    {
        // Arrange
        var policy = CreatePolicy();
        var tool = CreateTool(ToolRisk.Risky);

        // Act
        var result = policy.Evaluate(tool, Args("Harmless"));

        // Assert
        Assert.True(result.RequiresConfirmation);
    }

    [Fact]
    public void Evaluate_Allowed_WhenModeIsAutonomous()
    {
        // Arrange
        var policy = CreatePolicy(SafetyMode.Autonomous);
        var tool = CreateTool(ToolRisk.Risky);

        // Act
        var result = policy.Evaluate(tool, Args("Delete everything"));

        // Assert
        Assert.False(result.RequiresConfirmation);
    }

    [Theory]
    [InlineData(ToolRisk.Action, true)]
    [InlineData(ToolRisk.ReadOnly, false)]
    public void Evaluate_ConfirmsOnlyActions_WhenModeIsConfirmAll(ToolRisk risk, bool expected)
    {
        // Arrange
        var policy = CreatePolicy(SafetyMode.ConfirmAll);
        var tool = CreateTool(risk);

        // Act
        var result = policy.Evaluate(tool, Args("Search box"));

        // Assert
        Assert.Equal(expected, result.RequiresConfirmation);
    }
}
