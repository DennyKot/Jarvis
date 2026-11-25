using System.Diagnostics;
using Microsoft.Win32;

namespace Jarvis.Automation.Desktop.Apps;

public sealed record AppCandidate(string DisplayName, string Target, int Score);

/// <summary>Resolves spoken app names ("word", "vs code", "калькулятор") to something that can be started.</summary>
public sealed class AppLauncher
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["word"] = "winword", ["microsoft word"] = "winword", ["ворд"] = "winword",
        ["excel"] = "excel", ["microsoft excel"] = "excel", ["ексель"] = "excel",
        ["powerpoint"] = "powerpnt", ["outlook"] = "outlook",
        ["vs code"] = "code", ["vscode"] = "code", ["visual studio code"] = "code",
        ["chrome"] = "chrome", ["google chrome"] = "chrome", ["хром"] = "chrome",
        ["edge"] = "msedge", ["notepad"] = "notepad", ["блокнот"] = "notepad",
        ["calculator"] = "calc", ["калькулятор"] = "calc",
        ["paint"] = "mspaint", ["explorer"] = "explorer", ["file explorer"] = "explorer", ["провідник"] = "explorer",
        ["terminal"] = "wt", ["windows terminal"] = "wt", ["cmd"] = "cmd", ["command prompt"] = "cmd",
        ["powershell"] = "pwsh", ["settings"] = "ms-settings:", ["налаштування"] = "ms-settings:",
        ["task manager"] = "taskmgr",
    };

    private readonly Lazy<IReadOnlyList<(string Name, string Path)>> _shortcuts = new(LoadStartMenuShortcuts);

    public IReadOnlyList<AppCandidate> Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var query = name.Trim();
        var candidates = new List<AppCandidate>();

        if (Aliases.TryGetValue(query, out var alias))
        {
            query = alias;
            candidates.Add(new AppCandidate(name, ResolveExecutable(alias) ?? alias, 100));
        }

        foreach (var (shortcutName, path) in _shortcuts.Value)
        {
            var score = Score(shortcutName, name);
            if (score > 0)
            {
                candidates.Add(new AppCandidate(shortcutName, path, score));
            }
        }

        if (ResolveExecutable(query) is { } exe)
        {
            candidates.Add(new AppCandidate(query, exe, 90));
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .DistinctBy(c => c.Target, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public AppCandidate Launch(string name, string? arguments)
    {
        var candidate = Resolve(name).FirstOrDefault() ?? new AppCandidate(name, name, 0);
        var startInfo = new ProcessStartInfo(candidate.Target) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            startInfo.Arguments = arguments;
        }

        using var _ = Process.Start(startInfo);
        return candidate;
    }

    internal static int Score(string candidate, string query)
    {
        var c = candidate.Trim();
        var q = query.Trim();
        if (c.Equals(q, StringComparison.OrdinalIgnoreCase))
        {
            return 95;
        }

        if (c.StartsWith(q, StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        if (c.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            return 60;
        }

        var queryWords = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidateWords = c.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matched = queryWords.Count(w => candidateWords.Any(cw => cw.StartsWith(w, StringComparison.OrdinalIgnoreCase)));
        return matched == queryWords.Length ? 40 : 0;
    }

    private static string? ResolveExecutable(string name)
    {
        if (name.Contains(':') || name.Contains('\\'))
        {
            return name.EndsWith(':') ? name : File.Exists(name) ? name : null;
        }

        var exe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"')))
            {
                return path.Trim('"');
            }
        }

        if (name.Equals("code", StringComparison.OrdinalIgnoreCase))
        {
            var userInstall = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe");
            if (File.Exists(userInstall))
            {
                return userInstall;
            }
        }

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries);
        return pathDirs.Select(d => Path.Combine(d.Trim(), exe)).FirstOrDefault(File.Exists);
    }

    private static IReadOnlyList<(string Name, string Path)> LoadStartMenuShortcuts()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };

        return roots
            .Where(Directory.Exists)
            .SelectMany(r => SafeEnumerate(r, "*.lnk").Concat(SafeEnumerate(r, "*.url")))
            .Select(p => (Path.GetFileNameWithoutExtension(p), p))
            .Where(s => !s.Item1.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
