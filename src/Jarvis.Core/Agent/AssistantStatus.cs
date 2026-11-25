namespace Jarvis.Core.Agent;

public enum AssistantState
{
    Idle,
    Listening,
    Transcribing,
    Thinking,
    Acting,
    Confirming,
    Speaking,
    Error,
}

/// <summary>Central place where the voice pipeline and agent publish what Jarvis is doing; the UI subscribes.</summary>
public sealed class AssistantStatus
{
    public AssistantState State { get; private set; } = AssistantState.Idle;

    public event Action<AssistantState>? StateChanged;
    public event Action<string>? ActivityChanged;
    public event Action<string, bool>? MessagePosted;
    public event Action<float>? AudioLevelChanged;

    public void SetState(AssistantState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(state);
    }

    public void ReportActivity(string activity) => ActivityChanged?.Invoke(activity);

    public void PostUserMessage(string text) => MessagePosted?.Invoke(text, true);

    public void PostAssistantMessage(string text) => MessagePosted?.Invoke(text, false);

    /// <summary>Raised with the reply text received so far while the final answer streams in.</summary>
    public event Action<string>? ReplyStreamed;

    public void StreamReply(string textSoFar) => ReplyStreamed?.Invoke(textSoFar);

    public void ReportAudioLevel(float level) => AudioLevelChanged?.Invoke(level);
}
