using Jarvis.Core.Agent;
using Jarvis.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Voice;

/// <summary>
/// Fast Parakeet pass; if it produced Cyrillic (Parakeet cannot be forced to Ukrainian and often outputs Russian),
/// the audio is re-transcribed by Whisper, whose language detection is limited to the allowed languages.
/// </summary>
public sealed class HybridTranscriber(
    ParakeetTranscriber parakeet,
    WhisperTranscriber whisper,
    AssistantStatus status,
    IOptions<JarvisOptions> options,
    ILogger<HybridTranscriber> logger) : ISpeechTranscriber
{
    /// <summary>Whisper language codes of languages written in Cyrillic.</summary>
    private static readonly HashSet<string> CyrillicLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "uk", "ru", "be", "bg", "sr", "mk", "kk", "ky", "tg", "mn", "ba", "tt",
    };

    public async Task<Transcript> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        var first = await parakeet.TranscribeAsync(samples, cancellationToken);
        if (!ContainsCyrillic(first.Text))
        {
            return first;
        }

        logger.LogInformation("Cyrillic detected, re-transcribing with Whisper");
        status.ReportActivity("Recognizing speech (Ukrainian)…");

        // With a single allowed Cyrillic language the answer is already known: skipping Whisper's language detection
        // saves a whole extra encoder pass (the slow part of Whisper).
        var voice = options.Value.Voice;
        var language = string.Equals(voice.Language, "auto", StringComparison.OrdinalIgnoreCase)
            ? SingleCyrillicLanguage(voice.AllowedLanguages)
            : null;
        return await whisper.TranscribeAsync(samples, language, cancellationToken);
    }

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await parakeet.PrepareAsync(cancellationToken);
        await whisper.PrepareAsync(cancellationToken);
    }

    internal static bool ContainsCyrillic(string text) => text.Any(c => c is >= 'Ѐ' and <= 'ӿ');

    /// <returns>The only Cyrillic-script language among <paramref name="allowedLanguages"/>, or null if there are none or several.</returns>
    internal static string? SingleCyrillicLanguage(IEnumerable<string> allowedLanguages)
    {
        var cyrillic = allowedLanguages.Where(CyrillicLanguages.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return cyrillic.Count == 1 ? cyrillic[0].ToLowerInvariant() : null;
    }
}
