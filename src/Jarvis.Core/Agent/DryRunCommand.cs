using System.Text.RegularExpressions;

namespace Jarvis.Core.Agent;

/// <summary>Recognizes commands that ask for a plan preview instead of execution, e.g. "dry run: send the report".</summary>
public static partial class DryRunCommand
{
    [GeneratedRegex(@"^\s*(dry[\s-]?run|пробний запуск)\b[\s:,.\-—]*(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Prefix();

    public static bool TryParse(string command, out string rest)
    {
        rest = command;
        var match = Prefix().Match(command ?? string.Empty);
        if (!match.Success || match.Groups["rest"].Value.Trim() is not { Length: > 0 } text)
        {
            return false;
        }

        rest = text;
        return true;
    }
}
