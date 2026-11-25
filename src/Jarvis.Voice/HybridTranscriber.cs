using Jarvis.Core.Agent;
using Microsoft.Extensions.Logging;

namespace Jarvis.Voice;

/// <summary>
/// Fast Parakeet pass; if it produced Cyrillic (Parakeet cannot be forced to Ukrainian and often outputs Russian),
/// the audio is re-transcribed by Whisper, whose language detection is limited to the allowed languages.
/// </summary>
public sealed class HybridTranscriber(
    ParakeetTranscriber parakeet,
    WhisperTranscriber whisper,
    AssistantStatus status,
    ILogger<HybridTranscriber> logger) : ISpeechTranscriber
{
    public async Task<Transcript> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        var first = await parakeet.TranscribeAsync(samples, cancellationToken);
        if (!ContainsCyrillic(first.Text))
        {
            return first;
        }

        logger.LogInformation("Cyrillic detected, re-transcribing with Whisper");
        status.ReportActivity("Recognizing speech (Ukrainian)…");
        return await whisper.TranscribeAsync(samples, cancellationToken);
    }

    internal static bool ContainsCyrillic(string text) => text.Any(c => c is >= '\u0400' and <= '\u04FF');
}
