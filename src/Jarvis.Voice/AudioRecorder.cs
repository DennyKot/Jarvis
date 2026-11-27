using Jarvis.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;

namespace Jarvis.Voice;

public sealed class AudioRecorder(IOptions<JarvisOptions> options, ILogger<AudioRecorder> logger)
{
    public const int SampleRate = 16000;
    private static readonly TimeSpan NoSpeechTimeout = TimeSpan.FromSeconds(7);

    public event Action<float>? LevelChanged;

    public event Action? Started;

    /// <returns>Samples in [-1, 1], or an empty array when no speech was detected.</returns>
    public async Task<float[]> RecordAsync(CancellationToken stopToken, CancellationToken cancellationToken)
    {
        var voice = options.Value.Voice;
        var detector = new SpeechEndDetector(voice.SilenceThreshold, TimeSpan.FromMilliseconds(voice.SilenceTimeoutMs), NoSpeechTimeout);
        var samples = new List<float>(SampleRate * 10);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var maxSamples = voice.MaxRecordingSeconds * SampleRate;

        var started = false;
        using var waveIn = new WaveIn { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 50 };
        waveIn.DataAvailable += (_, e) =>
        {
            if (!started)
            {
                started = true;
                Started?.Invoke();
            }

            var count = e.BytesRecorded / 2;
            double sumSquares = 0;
            lock (samples)
            {
                for (var i = 0; i < count; i++)
                {
                    var sample = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                    samples.Add(sample);
                    sumSquares += sample * sample;
                }
            }

            var rms = count == 0 ? 0 : (float)Math.Sqrt(sumSquares / count);
            LevelChanged?.Invoke(rms);
            if (detector.Process(rms, TimeSpan.FromMilliseconds(count * 1000.0 / SampleRate)) || samples.Count >= maxSamples)
            {
                finished.TrySetResult();
            }
        };
        waveIn.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                finished.TrySetException(e.Exception);
            }
            else
            {
                finished.TrySetResult();
            }
        };

        waveIn.StartRecording();
        using (stopToken.Register(() => finished.TrySetResult()))
        using (cancellationToken.Register(() => finished.TrySetCanceled(cancellationToken)))
        {
            try
            {
                await finished.Task;
            }
            finally
            {
                waveIn.StopRecording();
            }
        }

        logger.LogInformation("Recorded {Seconds:0.0}s, speech detected: {Speech}", samples.Count / (double)SampleRate, detector.SpeechDetected);
        lock (samples)
        {
            return detector.SpeechDetected || stopToken.IsCancellationRequested ? samples.ToArray() : [];
        }
    }
}

public sealed class SpeechEndDetector(float threshold, TimeSpan silenceTimeout, TimeSpan noSpeechTimeout)
{
    private TimeSpan _elapsed;
    private TimeSpan _silence;

    public bool SpeechDetected { get; private set; }

    /// <returns>True when recording should stop.</returns>
    public bool Process(float rms, TimeSpan duration)
    {
        _elapsed += duration;
        if (rms >= threshold)
        {
            SpeechDetected = true;
            _silence = TimeSpan.Zero;
            return false;
        }

        if (!SpeechDetected)
        {
            return _elapsed >= noSpeechTimeout;
        }

        _silence += duration;
        return _silence >= silenceTimeout;
    }
}
