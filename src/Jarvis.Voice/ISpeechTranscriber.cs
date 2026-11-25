namespace Jarvis.Voice;

public sealed record Transcript(string Text, string Language);

public interface ISpeechTranscriber
{
    Task<Transcript> TranscribeAsync(float[] samples, CancellationToken cancellationToken);

    /// <summary>Downloads (if missing) and loads the model(s) so the first transcription is instant.</summary>
    Task PrepareAsync(CancellationToken cancellationToken);
}
