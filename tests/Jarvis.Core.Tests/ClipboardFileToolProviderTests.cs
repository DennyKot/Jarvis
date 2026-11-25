using Jarvis.Automation.Desktop.Files;

namespace Jarvis.Core.Tests;

public sealed class ClipboardFileToolProviderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("jarvis-find-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Find_ReturnsShallowFileFirst_WhenNameMatchesAtSeveralDepths()
    {
        // Arrange
        var deep = Directory.CreateDirectory(Path.Combine(_root, "a", "b")).FullName;
        File.WriteAllText(Path.Combine(deep, "Report-2025.pdf"), "x");
        File.WriteAllText(Path.Combine(_root, "report.pdf"), "x");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");

        // Act
        var result = ClipboardFileToolProvider.Find([_root], "report", 10, CancellationToken.None);

        // Assert
        Assert.Equal(["report.pdf", "Report-2025.pdf"], result.Select(f => f.Name));
    }

    [Fact]
    public void Find_SkipsNoisyFolders_WhenTheyContainMatches()
    {
        // Arrange
        var modules = Directory.CreateDirectory(Path.Combine(_root, "node_modules")).FullName;
        File.WriteAllText(Path.Combine(modules, "report.js"), "x");

        // Act
        var result = ClipboardFileToolProvider.Find([_root], "*.js", 10, CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void FileFind_ReturnsNotFoundMessage_WhenFolderMissing()
    {
        // Arrange
        var missing = Path.Combine(_root, "missing");

        // Act
        var result = ClipboardFileToolProvider.FileFind("x", missing, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.StartsWith("Folder not found", result);
    }

    [Fact]
    public void ClipboardRead_ReturnsWrittenText_WhenWrittenByClipboardWrite()
    {
        // Arrange
        var provider = new ClipboardFileToolProvider(Microsoft.Extensions.Logging.Abstractions.NullLogger<ClipboardFileToolProvider>.Instance);
        const string text = "Привіт, Jarvis ✓";

        // Act
        provider.ClipboardWrite(text);
        var result = ClipboardFileToolProvider.ClipboardRead();

        // Assert
        Assert.Equal(text, result);
    }
}
