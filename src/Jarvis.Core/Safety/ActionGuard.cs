using Jarvis.Core.Agent;
using Jarvis.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Safety;

public sealed record ConfirmationRequest(string Action, string Reason);

/// <summary>Implemented by the UI: asks the user to allow or deny an action.</summary>
public interface IConfirmationService
{
    Task<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken);
}

public sealed class ActionDeniedException(string action)
    : Exception($"The user did not allow: {action}");

/// <summary>Lets the agent and individual tools ask for permission before doing something risky.</summary>
public interface IActionGuard
{
    /// <exception cref="ActionDeniedException">The user denied the action or did not answer in time.</exception>
    Task EnsureApprovedAsync(string action, string reason, CancellationToken cancellationToken);
}

public sealed class ActionGuard(
    IConfirmationService confirmationService,
    AssistantStatus status,
    IOptions<JarvisOptions> options) : IActionGuard
{
    public async Task EnsureApprovedAsync(string action, string reason, CancellationToken cancellationToken)
    {
        var safety = options.Value.Safety;
        if (safety.Mode == SafetyMode.Autonomous)
        {
            return;
        }

        var previous = status.State;
        status.SetState(AssistantState.Confirming);
        status.ReportActivity($"Waiting for your permission: {action}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(safety.ConfirmationTimeout);

        bool approved;
        try
        {
            approved = await confirmationService.ConfirmAsync(new ConfirmationRequest(action, reason), timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            approved = false;
        }
        finally
        {
            status.SetState(previous);
        }

        if (!approved)
        {
            throw new ActionDeniedException(action);
        }
    }
}
