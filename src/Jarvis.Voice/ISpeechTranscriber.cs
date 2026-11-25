namespace Jarvis.Voice;

public sealed record Transcript(string Text, string Language);

public interface ISpeechTranscriber
{
    Task<Transcript> TranscribeAsync(float[] samples, CancellationToken cancellationToken);
}
