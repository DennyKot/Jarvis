using Jarvis.Core.Agent;
using Jarvis.Core.Tools;

namespace Jarvis.Core.Tests;

public class UndoJournalTests
{
    private readonly UndoJournal _journal = new();

    private Task TrackAsync(string description, Action<UndoJournal> duringAction) =>
        _journal.TrackAsync(description, () =>
        {
            duringAction(_journal);
            return Task.FromResult<object?>(null);
        });

    [Fact]
    public async Task UndoLastAsync_DoesNotRevertOlderAction_WhenNewestHasNoUndo()
    {
        // Arrange
        var olderUndone = false;
        await TrackAsync("Type: name", j => j.SetUndo(_ =>
        {
            olderUndone = true;
            return Task.FromResult("restored");
        }));
        await TrackAsync("Click: Save", _ => { });

        // Act
        var message = await _journal.UndoLastAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.False(olderUndone);
        Assert.Contains("can't undo \"Click: Save\"", message);
        Assert.Contains("Undo again", message);
    }

    [Fact]
    public async Task TrackAsync_RecordsNothing_WhenToolSkipsRecording()
    {
        // Arrange
        await TrackAsync("Type: name", j => j.SetUndo(_ => Task.FromResult("restored")));

        // Act
        await TrackAsync("Scroll", j => j.SkipRecording());

        // Assert
        Assert.Equal(["Type: name"], _journal.Entries.Select(e => e.Description));
    }

    [Fact]
    public async Task UndoLastAsync_ReportsFailure_WhenUndoThrows()
    {
        // Arrange
        await TrackAsync("Launch app: notepad", j => j.SetUndo(_ => throw new InvalidOperationException("window is gone")));

        // Act
        var message = await _journal.UndoLastAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Could not undo \"Launch app: notepad\": window is gone", message);
        Assert.False(_journal.CanUndo);
    }

    [Theory]
    [InlineData("dry run: send the report", "send the report")]
    [InlineData("Dry-run open gmail", "open gmail")]
    [InlineData("пробний запуск, відкрий пошту", "відкрий пошту")]
    public void TryParse_ReturnsCommand_WhenPrefixedWithDryRun(string input, string expected)
    {
        // Act
        var parsed = DryRunCommand.TryParse(input, out var rest);

        // Assert
        Assert.True(parsed);
        Assert.Equal(expected, rest);
    }

    [Theory]
    [InlineData("open the dry run report")]
    [InlineData("dry run")]
    public void TryParse_ReturnsFalse_WhenNotADryRunCommand(string input)
    {
        // Act
        var parsed = DryRunCommand.TryParse(input, out _);

        // Assert
        Assert.False(parsed);
    }
}
