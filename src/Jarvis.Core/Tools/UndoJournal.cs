namespace Jarvis.Core.Tools;

public sealed record UndoEntry(string Description, Func<CancellationToken, Task<string>>? Undo);

/// <summary>
/// Remembers how to revert the agent's recent actions. The agent tracks every action tool call; while it runs the tool
/// may attach an undo step (<see cref="SetUndo"/>). Actions without one are kept as barriers so undo never skips over
/// a newer change and silently reverts an older one.
/// </summary>
public sealed class UndoJournal
{
    public const int Capacity = 20;

    private readonly List<UndoEntry> _entries = [];
    private readonly AsyncLocal<PendingAction?> _pending = new();

    private sealed class PendingAction
    {
        public Func<CancellationToken, Task<string>>? Undo;
        public bool Skip;
    }

    public bool CanUndo
    {
        get
        {
            lock (_entries)
            {
                return _entries.Count > 0;
            }
        }
    }

    public IReadOnlyList<UndoEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>Runs an action and records it (with the undo step the tool attached, if any) when it succeeds.</summary>
    public async Task<T> TrackAsync<T>(string description, Func<Task<T>> action)
    {
        var pending = new PendingAction();
        _pending.Value = pending;
        try
        {
            var result = await action();
            if (!pending.Skip)
            {
                Push(new UndoEntry(description, pending.Undo));
            }

            return result;
        }
        finally
        {
            _pending.Value = null;
        }
    }

    /// <summary>Called by a tool while it runs: how to revert what it just did.</summary>
    public void SetUndo(Func<CancellationToken, Task<string>> undo)
    {
        if (_pending.Value is { } pending)
        {
            pending.Undo = undo;
        }
    }

    /// <summary>Called by a tool whose action changes nothing worth undoing (e.g. scrolling).</summary>
    public void SkipRecording()
    {
        if (_pending.Value is { } pending)
        {
            pending.Skip = true;
        }
    }

    public async Task<string> UndoLastAsync(CancellationToken cancellationToken)
    {
        UndoEntry entry;
        lock (_entries)
        {
            if (_entries.Count == 0)
            {
                return "There is nothing to undo.";
            }

            entry = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
        }

        if (entry.Undo is null)
        {
            return $"I can't undo \"{entry.Description}\" automatically." +
                   (CanUndo ? " Undo again to revert the step before it." : string.Empty);
        }

        try
        {
            return $"Undid \"{entry.Description}\": {await entry.Undo(cancellationToken)}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Could not undo \"{entry.Description}\": {ex.GetBaseException().Message}";
        }
    }

    public void Clear()
    {
        lock (_entries)
        {
            _entries.Clear();
        }
    }

    private void Push(UndoEntry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
            if (_entries.Count > Capacity)
            {
                _entries.RemoveAt(0);
            }
        }
    }
}

public sealed class UndoToolProvider(UndoJournal journal) : IToolProvider
{
    public const string ToolName = "undo_last_action";

    public IEnumerable<JarvisTool> GetTools() =>
    [
        JarvisTool.Create(journal.UndoLastAsync, ToolName, "Revert your most recent action (when the user says undo)."),
    ];
}
