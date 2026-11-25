using System.Text.RegularExpressions;
using Jarvis.Core.Configuration;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Safety;

public sealed record RiskAssessment(bool RequiresConfirmation, string Reason)
{
    public static RiskAssessment Allowed { get; } = new(false, string.Empty);
}

public sealed class RiskPolicy
{
    private readonly SafetyOptions _options;
    private readonly Regex _keywordRegex;

    public RiskPolicy(IOptions<JarvisOptions> options)
    {
        _options = options.Value.Safety;
        var alternatives = string.Join("|", _options.RiskyKeywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => Regex.Escape(k.Trim())));
        _keywordRegex = new Regex(
            alternatives.Length == 0 ? "(?!)" : $@"(?<![\p{{L}}\p{{N}}])({alternatives})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    public SafetyMode Mode => _options.Mode;

    public RiskAssessment Evaluate(JarvisTool tool, IReadOnlyDictionary<string, object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(arguments);

        if (_options.Mode == SafetyMode.Autonomous || tool.Risk == ToolRisk.ReadOnly)
        {
            return RiskAssessment.Allowed;
        }

        if (tool.Risk == ToolRisk.Risky)
        {
            return new RiskAssessment(true, $"'{tool.Name}' is marked as a risky action.");
        }

        if (_options.Mode == SafetyMode.ConfirmAll)
        {
            return new RiskAssessment(true, "Confirm-all mode is enabled.");
        }

        foreach (var name in tool.DescriptiveArguments)
        {
            if (arguments.TryGetValue(name, out var value) && value?.ToString() is { } text &&
                TryFindRiskyKeyword(text, out var keyword))
            {
                return new RiskAssessment(true, $"The target \"{Truncate(text, 80)}\" looks risky (\"{keyword}\").");
            }
        }

        return RiskAssessment.Allowed;
    }

    public bool TryFindRiskyKeyword(string? text, out string keyword)
    {
        keyword = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = _keywordRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        keyword = match.Groups[1].Value;
        return true;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "…";
}
