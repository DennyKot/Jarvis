using Jarvis.Core.Agent;
using Jarvis.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jarvis.Voice;

/// <summary>Push-to-talk voice input: record one utterance, then transcribe it.</summary>
public sealed class VoiceInput(AudioRecorder recorder, ISpeechTranscriber transcriber, AssistantStatus status)
{
    private CancellationTokenSource? _stop;

    public bool IsListening => _stop is not null;

    /// <summary>Ends the current recording early (pressing the talk hotkey again).</summary>
    public void StopListening() => _stop?.Cancel();

    public async Task<Transcript> ListenAsync(CancellationToken cancellationToken)
    {
        using var stop = new CancellationTokenSource();
        _stop = stop;
        void OnStarted()
        {
            status.SetState(AssistantState.Listening);
            status.ReportActivity("Listening… speak now");
        }

        recorder.LevelChanged += status.ReportAudioLevel;
        recorder.Started += OnStarted;
        float[] samples;
        try
        {
            status.SetState(AssistantState.Transcribing);
            status.ReportActivity("Starting the microphone…");
            samples = await recorder.RecordAsync(stop.Token, cancellationToken);
        }
        finally
        {
            recorder.LevelChanged -= status.ReportAudioLevel;
            recorder.Started -= OnStarted;
            _stop = null;
        }

        if (samples.Length == 0)
        {
            return new Transcript(string.Empty, string.Empty);
        }

        status.SetState(AssistantState.Transcribing);
        status.ReportActivity("Recognizing speech…");
        return await Task.Run(() => transcriber.TranscribeAsync(samples, cancellationToken), cancellationToken);
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddJarvisVoice(this IServiceCollection services)
    {
        services.AddSingleton<AudioRecorder>();
        services.AddSingleton<WhisperTranscriber>();
        services.AddSingleton<ParakeetTranscriber>();
        services.AddSingleton<HybridTranscriber>();
        services.AddSingleton<ISpeechTranscriber>(sp =>
            sp.GetRequiredService<IOptions<JarvisOptions>>().Value.Voice.Engine switch
            {
                SpeechEngine.Whisper => sp.GetRequiredService<WhisperTranscriber>(),
                SpeechEngine.Parakeet => sp.GetRequiredService<ParakeetTranscriber>(),
                _ => sp.GetRequiredService<HybridTranscriber>(),
            });
        services.AddSingleton<VoiceInput>();
        services.AddSingleton<WakeWordListener>();
        return services;
    }
}
