using Microsoft.Extensions.AI;

namespace Jarvis.Core.Tools;

public enum ToolRisk
{
    /// <summary>Only observes (snapshots, screenshots, listing windows).</summary>
    ReadOnly,

    /// <summary>Changes something, but is usually harmless (navigate, click, type).</summary>
    Action,

    /// <summary>Always needs user confirmation unless Jarvis runs autonomously.</summary>
    Risky,
}

/// <param name="Function">The function exposed to the LLM.</param>
/// <param name="Risk">Baseline risk of the tool.</param>
/// <param name="DescriptiveArguments">
/// Arguments that describe the target (for example a button label). The risk policy scans them for risky keywords.
/// </param>
public sealed record JarvisTool(AIFunction Function, ToolRisk Risk, IReadOnlyList<string> DescriptiveArguments)
{
    public string Name => Function.Name;

    public static JarvisTool Create(
        Delegate method,
        string name,
        string description,
        ToolRisk risk = ToolRisk.Action,
        params string[] descriptiveArguments) =>
        new(AIFunctionFactory.Create(method, name, description), risk, descriptiveArguments);
}

public interface IToolProvider
{
    IEnumerable<JarvisTool> GetTools();
}
