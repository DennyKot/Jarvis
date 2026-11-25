using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Jarvis.Automation.Desktop.Files;

/// <summary>
/// Clipboard and file-system shortcuts: replace select-all/copy click sequences and Explorer navigation.
/// </summary>
public sealed partial class ClipboardFileToolProvider(ILogger<ClipboardFileToolProvider> logger) : IToolProvider
{
    private const int MaxClipboardRead = 4000;
    private const int DefaultMaxResults = 15;
    private const int MaxScannedDirectories = 50_000;
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "node_modules", ".git", "bin", "obj", "$Recycle.Bin", ".nuget", ".vs",
    };

    public IEnumerable<JarvisTool> GetTools() =>
    [
        JarvisTool.Create(ClipboardRead, "clipboard_read", "Read text from the clipboard.", ToolRisk.ReadOnly),
        JarvisTool.Create(ClipboardWrite, "clipboard_write", "Put text on the clipboard (then paste with send_keys ctrl+v)."),
        JarvisTool.Create(FileFind, "file_find",
            "Find files by name (wildcards ok) in user folders; returns full paths.", ToolRisk.ReadOnly),
    ];

    internal static string ClipboardRead()
    {
        var text = GetClipboardText();
        if (string.IsNullOrEmpty(text))
        {
            return "Clipboard has no text.";
        }

        return text.Length > MaxClipboardRead ? text[..MaxClipboardRead] + $"… ({text.Length} chars total)" : text;
    }

    internal string ClipboardWrite([Description("Text to copy")] string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        SetClipboardText(text);
        logger.LogInformation("clipboard_write: {Length} chars", text.Length);
        return $"Copied {text.Length} chars to clipboard.";
    }

    internal static string FileFind(
        [Description("File name or part of it, wildcards allowed, e.g. 'report*.pdf'")] string name,
        [Description("Folder to search (default: Desktop, Documents, Downloads, then user profile)")] string? folder = null,
        [Description("Max results (default 15)")] int maxResults = DefaultMaxResults,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        maxResults = Math.Clamp(maxResults, 1, 100);
        IEnumerable<string> roots;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            var expanded = Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"'));
            if (!Directory.Exists(expanded))
            {
                return $"Folder not found: {expanded}";
            }

            roots = [expanded];
        }
        else
        {
            roots = DefaultRoots();
        }

        var matches = Find(roots, name, maxResults, cancellationToken);
        if (matches.Count == 0)
        {
            return $"No files matching '{name}'.";
        }

        var builder = new StringBuilder();
        foreach (var file in matches)
        {
            builder.Append(file.FullName).Append(" (").Append(FormatSize(file.Length)).Append(", ")
                .Append(file.LastWriteTime.ToString("yyyy-MM-dd")).AppendLine(")");
        }

        return builder.ToString().TrimEnd();
    }

    internal static List<FileInfo> Find(IEnumerable<string> roots, string name, int maxResults, CancellationToken cancellationToken)
    {
        name = name.Trim();
        var pattern = name.Contains('*') || name.Contains('?') ? name : $"*{name}*";
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        var results = new List<FileInfo>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            // Breadth-first so shallow (more likely relevant) files come first.
            var queue = new Queue<DirectoryInfo>([new DirectoryInfo(root)]);
            while (queue.Count > 0 && visited.Count < MaxScannedDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = queue.Dequeue();
                if (!visited.Add(directory.FullName))
                {
                    continue;
                }

                try
                {
                    foreach (var file in directory.EnumerateFiles(pattern, options))
                    {
                        results.Add(file);
                        if (results.Count >= maxResults)
                        {
                            return results;
                        }
                    }

                    foreach (var sub in directory.EnumerateDirectories("*", options))
                    {
                        if (!SkippedDirectories.Contains(sub.Name))
                        {
                            queue.Enqueue(sub);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return results;
    }

    private static IEnumerable<string> DefaultRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(profile, "Downloads"),
                profile,
            }
            .Where(Directory.Exists);
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    private static string? GetClipboardText()
    {
        OpenClipboardWithRetry();
        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            var pointer = handle == 0 ? 0 : GlobalLock(handle);
            if (pointer == 0)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void SetClipboardText(string text)
    {
        var chars = (text + '\0').ToCharArray();
        var memory = GlobalAlloc(GmemMoveable, (nuint)(chars.Length * sizeof(char)));
        var pointer = memory == 0 ? 0 : GlobalLock(memory);
        if (pointer == 0)
        {
            throw new InvalidOperationException("Out of memory for clipboard.");
        }

        Marshal.Copy(chars, 0, pointer, chars.Length);
        GlobalUnlock(memory);

        OpenClipboardWithRetry();
        try
        {
            EmptyClipboard();
            if (SetClipboardData(CfUnicodeText, memory) == 0)
            {
                GlobalFree(memory);
                throw new InvalidOperationException($"SetClipboardData failed ({Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Another app may hold the clipboard briefly.</summary>
    private static void OpenClipboardWithRetry()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(0))
            {
                return;
            }

            Thread.Sleep(30);
        }

        throw new InvalidOperationException("Clipboard is busy.");
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalFree(nint memory);
}
