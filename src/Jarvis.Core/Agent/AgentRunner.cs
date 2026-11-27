using System.Text.Json;
using Jarvis.Core.Configuration;
using Jarvis.Core.Llm;
using Jarvis.Core.Safety;
using Jarvis.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Agent;

public interface IAgentRunner
{
    Task<string> RunAsync(string command, CancellationToken cancellationToken);

    /// <summary>Dry run: plans the command with read-only tools only; actions are recorded, not executed.</summary>
    Task<AgentPlan> PreviewAsync(string command, CancellationToken cancellationToken);

    void ResetConversation();
}

/// <param name="RiskReason">Why the action would need confirmation, or null if it would run without asking.</param>
public sealed record PlannedAction(string Description, string? RiskReason);

public sealed record AgentPlan(IReadOnlyList<PlannedAction> Actions, string Summary)
{
    public string Format()
    {
        if (Actions.Count == 0)
        {
            return Summary;
        }

        var builder = new System.Text.StringBuilder("Dry run — nothing was changed. Planned actions:\n");
        for (var i = 0; i < Actions.Count; i++)
        {
            builder.Append(i + 1).Append(". ").Append(Actions[i].RiskReason is null ? "" : "⚠ ").AppendLine(Actions[i].Description);
        }

        return builder.Append('\n').Append(Summary).ToString().TrimEnd();
    }
}

/// <summary>Runs the "think → call tools → observe" loop until the model gives a final answer.</summary>
public sealed class AgentRunner : IAgentRunner
{
    private const string ObservationMarker = "jarvis.observation";

    /// <summary>Tools where identical consecutive calls are normal (scroll down, press Down/Tab several times).</summary>
    private static readonly HashSet<string> RepeatableTools = ["browser_scroll", "browser_press", "send_keys"];

    internal const string DryRunInstruction =
        "[DRY RUN: action tools are simulated and change nothing; read-only tools work normally. " +
        "Call the actions you would take, in order, then reply with a one-sentence summary of the plan.]\n";

    private readonly UndoJournal _undo;
    private List<PlannedAction>? _plan;
    private readonly IChatClientFactory _clientFactory;
    private readonly IReadOnlyDictionary<string, JarvisTool> _tools;
    private readonly RiskPolicy _riskPolicy;
    private readonly IActionGuard _actionGuard;
    private readonly AssistantStatus _status;
    private readonly AgentObservations _observations;
    private readonly JarvisOptions _options;
    private readonly ILogger<AgentRunner> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly UserProfileService? _userProfile;
    private readonly List<ChatMessage> _history = [];
    private readonly HashSet<ToolGroup> _activeGroups = [ToolGroup.Core];
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private IChatClient? _client;
    private string? _lastCallSignature;
    private int _repeatCount;
    private long _totalInputTokens;
    private long _totalOutputTokens;

    public AgentRunner(
        IChatClientFactory clientFactory,
        IEnumerable<IToolProvider> toolProviders,
        RiskPolicy riskPolicy,
        IActionGuard actionGuard,
        AssistantStatus status,
        AgentObservations observations,
        IOptions<JarvisOptions> options,
        ILogger<AgentRunner> logger,
        TimeProvider? timeProvider = null,
        UserProfileService? userProfile = null,
        UndoJournal? undo = null)
    {
        _userProfile = userProfile;
        _undo = undo ?? new UndoJournal();
        _clientFactory = clientFactory;
        _riskPolicy = riskPolicy;
        _actionGuard = actionGuard;
        _status = status;
        _observations = observations;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _tools = toolProviders.SelectMany(p => p.GetTools()).ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> ToolNames => _tools.Keys.ToArray();

    public void ResetConversation()
    {
        lock (_history)
        {
            _history.Clear();
            _activeGroups.Clear();
            _activeGroups.Add(ToolGroup.Core);
        }
    }

    public async Task<string> RunAsync(string command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        await _runLock.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Llm.RunTimeout);
            return await RunCoreAsync(command, dryRun: false, timeout.Token);
        }
        finally
        {
            _runLock.Release();
        }
    }

    public async Task<AgentPlan> PreviewAsync(string command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        await _runLock.WaitAsync(cancellationToken);
        _plan = [];
        ToolGroup[] groupsBefore;
        lock (_history)
        {
            groupsBefore = [.. _activeGroups];
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Llm.RunTimeout);
            var summary = await RunCoreAsync(DryRunInstruction + command, dryRun: true, timeout.Token);
            return new AgentPlan(_plan, summary);
        }
        finally
        {
            // A dry run must not reveal tool groups for later real runs.
            lock (_history)
            {
                _activeGroups.Clear();
                _activeGroups.UnionWith(groupsBefore);
            }

            _plan = null;
            _runLock.Release();
        }
    }

    private async Task<string> RunCoreAsync(string command, bool dryRun, CancellationToken cancellationToken)
    {
        _client ??= _clientFactory.Create(_options.Llm);
        _observations.Drain();

        var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt.Build(_timeProvider.GetLocalNow(), _options.Browser.Mode, _userProfile?.Profile.FullName)) };
        lock (_history)
        {
            messages.AddRange(_history);
        }

        var turnStart = messages.Count;
        messages.Add(new ChatMessage(ChatRole.User, command));

        var chatOptions = new ChatOptions
        {
            Tools = VisibleTools(),
            Temperature = _options.Llm.Temperature,
            MaxOutputTokens = _options.Llm.MaxOutputTokens,
            AllowMultipleToolCalls = _options.Llm.AllowMultipleToolCalls,
        };

        string answer;
        try
        {
            answer = await LoopAsync(messages, chatOptions, cancellationToken);
        }
        finally
        {
            // A dry run must not leave simulated "done" actions in the conversation.
            if (!dryRun)
            {
                RememberTurn(messages.Skip(turnStart));
            }
        }

        _logger.LogInformation("{Kind}: {Answer}", dryRun ? "Plan" : "Answer", answer);
        if (!dryRun)
        {
            _status.PostAssistantMessage(answer);
        }

        return answer;
    }

    private async Task<string> LoopAsync(List<ChatMessage> messages, ChatOptions chatOptions, CancellationToken cancellationToken)
    {
        _lastCallSignature = null;
        _repeatCount = 0;
        _totalInputTokens = 0;
        _totalOutputTokens = 0;
        string? lastTool = null;
        string? sourceLine = null;
        for (var step = 1; step <= _options.Llm.MaxSteps; step++)
        {
            _status.SetState(AssistantState.Thinking);
            _status.ReportActivity(lastTool switch
            {
                null => "Thinking…",
                "web_lookup" => "Reading the web results…",
                "browser_read_text" or "browser_open" => "Reading the page…",
                _ => $"Thinking… (step {step})",
            });

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var response = await GetStreamedResponseAsync(messages, chatOptions, cancellationToken);
            messages.AddRange(response.Messages);

            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();
            var input = response.Usage?.InputTokenCount ?? 0;
            var output = response.Usage?.OutputTokenCount ?? 0;
            _totalInputTokens += input;
            _totalOutputTokens += output;
            _logger.LogInformation("LLM step {Step}: {Elapsed:0.0}s, {Calls} tool call(s), tokens in={In} out={Out}",
                step, stopwatch.Elapsed.TotalSeconds, calls.Count, input, output);

            if (calls.Count == 0)
            {
                _logger.LogInformation("Command used {In} input + {Out} output tokens in {Steps} step(s)", _totalInputTokens, _totalOutputTokens, step);
                var answer = string.IsNullOrWhiteSpace(response.Text) ? "Done." : response.Text.Trim();
                return sourceLine is null || answer.Contains("http", StringComparison.OrdinalIgnoreCase)
                    ? answer
                    : answer + "\n" + sourceLine;
            }

            // Only the newest page snapshot / UI tree matters; older ones just burn context.
            CompactToolResults(messages);
            lastTool = calls[^1].Name;

            foreach (var call in calls)
            {
                var signature = $"{call.Name}:{JsonSerializer.Serialize(call.Arguments)}";
                _repeatCount = signature == _lastCallSignature ? _repeatCount + 1 : 1;
                _lastCallSignature = signature;

                var result = _repeatCount >= (RepeatableTools.Contains(call.Name) ? 10 : 3)
                    ? $"You already called {call.Name} with the same arguments {_repeatCount - 1} times in a row. " +
                      "Do something different, or give your final answer."
                    : await InvokeToolAsync(call, cancellationToken);
                if (call.Name == "web_lookup" && result.LastIndexOf("\n" + WebLookupToolProvider.SourcePrefix, StringComparison.Ordinal) is var at and >= 0)
                {
                    sourceLine = result[(at + 1)..].Trim();
                }
                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]));
            }

            if (ActivateGroups(calls))
            {
                chatOptions.Tools = VisibleTools();
            }

            AddObservations(messages);
        }

        return $"I stopped after {_options.Llm.MaxSteps} steps without finishing. Tell me how to continue.";
    }

    private void AddObservations(List<ChatMessage> messages)
    {
        var images = _observations.Drain();
        if (images.Count == 0)
        {
            return;
        }

        // A screenshot costs ~1k+ tokens on every later step; only the newest one is useful.
        RemoveImages(messages);
        var contents = new List<AIContent>();
        foreach (var image in images)
        {
            contents.Add(new TextContent(image.Caption));
            contents.Add(new DataContent(image.Data, image.MediaType));
        }

        messages.Add(new ChatMessage(ChatRole.User, contents)
        {
            AdditionalProperties = new() { [ObservationMarker] = true },
        });
    }

    /// <summary>Tools sent to the model; kept in a stable order so the prompt prefix stays cacheable.</summary>
    private List<AITool> VisibleTools()
    {
        lock (_history)
        {
            return _tools.Values
                .Where(t => !_options.Llm.DynamicTools || _activeGroups.Contains(ToolGroups.Of(t.Name)))
                .Select(t => (AITool)t.Function)
                .ToList();
        }
    }

    /// <summary>Hidden tools stay callable; using one (or an entry point like browser_open) reveals its group.</summary>
    private bool ActivateGroups(IEnumerable<FunctionCallContent> calls)
    {
        var changed = false;
        foreach (var call in calls.Where(c => _tools.ContainsKey(c.Name)))
        {
            foreach (var group in ToolGroups.ActivatedBy(call.Name, _options.Browser.Mode))
            {
                lock (_history)
                {
                    changed |= _activeGroups.Add(group);
                }
            }
        }

        return changed && _options.Llm.DynamicTools;
    }

    private async Task<ChatResponse> GetStreamedResponseAsync(List<ChatMessage> messages, ChatOptions chatOptions, CancellationToken cancellationToken)
    {
        var updates = new List<ChatResponseUpdate>();
        var text = new System.Text.StringBuilder();
        var hasToolCall = false;
        await foreach (var update in _client!.GetStreamingResponseAsync(messages, chatOptions, cancellationToken))
        {
            updates.Add(update);
            hasToolCall |= update.Contents.Any(c => c is FunctionCallContent);
            if (hasToolCall || string.IsNullOrEmpty(update.Text))
            {
                continue;
            }

            text.Append(update.Text);
            if (text.ToString().Trim() is { Length: > 0 } soFar)
            {
                _status.StreamReply(soFar);
            }
        }

        return updates.ToChatResponse();
    }

    internal async Task<string> InvokeToolAsync(FunctionCallContent call, CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
        {
            return $"Error: unknown tool '{call.Name}'.";
        }

        var arguments = call.Arguments ?? new Dictionary<string, object?>();
        var description = Describe(tool, arguments);
        _logger.LogInformation("Tool {Tool} {Arguments}", call.Name, Redact(arguments));

        try
        {
            var assessment = _riskPolicy.Evaluate(tool, arguments.AsReadOnly());
            if (_plan is not null && tool.Risk != ToolRisk.ReadOnly)
            {
                _plan.Add(new PlannedAction(description, assessment.RequiresConfirmation ? assessment.Reason : null));
                _status.ReportActivity($"Planning: {description}");
                return $"Dry run: not executed. Assume \"{description}\" worked and continue; don't repeat it.";
            }

            if (assessment.RequiresConfirmation)
            {
                await _actionGuard.EnsureApprovedAsync(description, assessment.Reason, cancellationToken);
            }

            _status.SetState(tool.Risk == ToolRisk.ReadOnly ? AssistantState.Thinking : AssistantState.Acting);
            _status.ReportActivity(description);

            var functionArguments = new AIFunctionArguments(arguments);
            if (tool.Risk == ToolRisk.ReadOnly || tool.Name == UndoToolProvider.ToolName)
            {
                return ToText(await tool.Function.InvokeAsync(functionArguments, cancellationToken));
            }

            return await _undo.TrackAsync(description, async () =>
            {
                var text = ToText(await tool.Function.InvokeAsync(functionArguments, cancellationToken));
                if (text.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                {
                    // A reported failure changed nothing; recording it would leave an un-undoable barrier.
                    _undo.SkipRecording();
                }

                return text;
            });
        }
        catch (ActionDeniedException ex)
        {
            return $"Denied: {ex.Message}. Do not retry this action.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool {Tool} failed", call.Name);
            return $"Error: {ex.GetBaseException().Message}";
        }
    }

    private static string ToText(object? result) => result switch
    {
        null => "OK",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? "OK",
        _ => result.ToString() ?? "OK",
    };

    internal static string Describe(JarvisTool tool, IDictionary<string, object?> arguments)
    {
        if (tool.Name == "web_lookup" && arguments.TryGetValue("query", out var query) && query?.ToString() is { Length: > 0 } q)
        {
            return $"Searching the web: {(q.Length > 60 ? q[..60] + "…" : q)}";
        }

        if (tool.Name == "browser_read_text")
        {
            return arguments.TryGetValue("query", out var find) && find?.ToString() is { Length: > 0 } f
                ? $"Reading the page for: {(f.Length > 60 ? f[..60] + "…" : f)}"
                : "Reading the page…";
        }

        foreach (var key in tool.DescriptiveArguments.Concat(["element", "url", "query", "name", "title", "keys"]))
        {
            if (arguments.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } text)
            {
                return $"{Humanize(tool.Name)}: {(text.Length > 60 ? text[..60] + "…" : text)}";
            }
        }

        return Humanize(tool.Name);
    }

    private static string Humanize(string toolName)
    {
        var text = toolName.Replace('_', ' ');
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string Redact(IDictionary<string, object?> arguments) =>
        string.Join(", ", arguments.Select(a =>
            a.Key is "text" or "value" ? $"{a.Key}=<{a.Value?.ToString()?.Length ?? 0} chars>" : $"{a.Key}={a.Value}"));

    private void RememberTurn(IEnumerable<ChatMessage> turn)
    {
        lock (_history)
        {
            var messages = turn.Select(StripImages).ToList();
            CompactToolResults(messages);
            _history.AddRange(messages);
            TrimHistory(_history, _options.Llm.HistoryMessageLimit);
        }
    }

    internal static void RemoveImages(List<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            for (var i = 0; i < message.Contents.Count; i++)
            {
                if (message.Contents[i] is DataContent)
                {
                    message.Contents[i] = new TextContent("[old screenshot removed]");
                }
            }
        }
    }

    /// <summary>Shortens large tool outputs (page snapshots, UI trees) that are already in the conversation.</summary>
    internal static void CompactToolResults(List<ChatMessage> messages, int maxLength = 600)
    {
        foreach (var message in messages.Where(m => m.Role == ChatRole.Tool))
        {
            for (var i = 0; i < message.Contents.Count; i++)
            {
                if (message.Contents[i] is FunctionResultContent { Result: string text } result && text.Length > maxLength)
                {
                    message.Contents[i] = new FunctionResultContent(result.CallId,
                        text[..maxLength] + "\n…(older output truncated; take a new snapshot if you need it)");
                }
            }
        }
    }

    /// <summary>Keeps old screenshots out of the context: they are expensive and stale.</summary>
    private static ChatMessage StripImages(ChatMessage message)
    {
        if (!message.Contents.Any(c => c is DataContent))
        {
            return message;
        }

        var contents = message.Contents
            .Select(c => c is DataContent ? new TextContent("[old screenshot removed]") : c)
            .ToList();
        return new ChatMessage(message.Role, contents) { AdditionalProperties = message.AdditionalProperties };
    }

    private static bool IsObservation(ChatMessage message) =>
        message.AdditionalProperties?.ContainsKey(ObservationMarker) == true;

    /// <summary>Drops whole turns (starting at a user command) so tool calls and their results stay paired.</summary>
    internal static void TrimHistory(List<ChatMessage> history, int limit)
    {
        while (history.Count > limit)
        {
            var nextTurn = history.FindIndex(1, m => m.Role == ChatRole.User && !IsObservation(m));
            if (nextTurn <= 0)
            {
                history.Clear();
                return;
            }

            history.RemoveRange(0, nextTurn);
        }
    }
}
