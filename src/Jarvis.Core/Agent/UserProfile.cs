using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Agent;

public sealed record UserProfile(string? FirstName, string? FullName, string[] Sources);

internal sealed record NameCandidate(string FullName, string Source, int Weight);

/// <summary>
/// Figures out the user's name once (Windows account, Chrome/Edge Google profiles) and caches it in
/// %LOCALAPPDATA%\Jarvis\user-profile.json. Only the name is kept; e-mails are never stored or sent to the LLM.
/// </summary>
public sealed partial class UserProfileService(IOptions<JarvisOptions> options, ILogger<UserProfileService> logger)
{
    private readonly Lazy<UserProfile> _profile = new(() => Load(options.Value, logger));

    public UserProfile Profile => _profile.Value;

    private static UserProfile Load(JarvisOptions options, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(options.User.Name))
        {
            var name = options.User.Name.Trim();
            return new UserProfile(name.Split(' ')[0], name, ["config"]);
        }

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis", "user-profile.json");
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<UserProfile>(File.ReadAllText(path)) ?? new UserProfile(null, null, []);
            }

            var profile = Pick(Discover(logger));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(profile));
            logger.LogInformation("User profile discovered: {Name} (from {Sources})", profile.FullName, string.Join(", ", profile.Sources));
            return profile;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Could not load the user profile");
            return new UserProfile(null, null, []);
        }
    }

    /// <summary>Votes on the first name across sources, then picks the best full name sharing it.</summary>
    internal static UserProfile Pick(IEnumerable<NameCandidate> candidates)
    {
        var list = candidates
            .Select(c => c with { FullName = Normalize(c.FullName) })
            .Where(c => c.FullName.Length > 1)
            .ToList();
        var winner = list
            .GroupBy(c => FirstToken(c.FullName), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(c => c.Weight))
            .FirstOrDefault();
        if (winner is null)
        {
            return new UserProfile(null, null, []);
        }

        var full = winner
            .GroupBy(c => c.FullName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Max(c => c.FullName.Split(' ').Length > 1 ? 1 : 0))
            .ThenByDescending(g => g.Sum(c => c.Weight))
            .First().Key;
        return new UserProfile(FirstToken(full), full, winner.Select(c => c.Source).Distinct().ToArray());
    }

    private static string FirstToken(string name) => name.Split(' ')[0];

    /// <summary>"denys.kotenko" / "denys kotenko" → "Denys Kotenko".</summary>
    internal static string Normalize(string name)
    {
        var words = name.Replace('.', ' ').Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.All(char.IsLetter) || w.All(c => char.IsLetter(c) || c is '-' or '\''));
        return string.Join(' ', words.Select(w => w.Any(char.IsUpper) ? w : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(w)));
    }

    private static List<NameCandidate> Discover(ILogger logger)
    {
        var result = new List<NameCandidate>();
        try
        {
            if (GetWindowsFullName() is { Length: > 0 } fullName)
            {
                result.Add(new NameCandidate(fullName, "Windows account", 3));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Windows full name lookup failed");
        }

        result.Add(new NameCandidate(Environment.UserName, "Windows user name", 2));

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var (browser, dir) in new[] { ("Chrome", @"Google\Chrome\User Data"), ("Edge", @"Microsoft\Edge\User Data") })
        {
            try
            {
                var file = Path.Combine(local, dir, "Local State");
                if (!File.Exists(file))
                {
                    continue;
                }

                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (JsonNode.Parse(stream)?["profile"]?["info_cache"] is not JsonObject cache)
                {
                    continue;
                }

                foreach (var (_, info) in cache)
                {
                    if (info?["gaia_name"]?.GetValue<string>() is { Length: > 0 } gaia)
                    {
                        result.Add(new NameCandidate(gaia, $"{browser} profile", 1));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                logger.LogDebug(ex, "Reading {Browser} profiles failed", browser);
            }
        }

        return result;
    }

    private static string? GetWindowsFullName()
    {
        if (NetUserGetInfo(null, Environment.UserName, 10, out var buffer) != 0 || buffer == 0)
        {
            return null;
        }

        try
        {
            // USER_INFO_10 { name, comment, usr_comment, full_name }
            return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, 3 * IntPtr.Size))?.Trim();
        }
        finally
        {
            NetApiBufferFree(buffer);
        }
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetUserGetInfo(string? serverName, string userName, int level, out nint buffer);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(nint buffer);
}
