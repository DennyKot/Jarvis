using Jarvis.Core.Agent;
using Jarvis.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace Jarvis.Voice;

/// <summary>
/// Local speech-to-text with NVIDIA Parakeet TDT 0.6B v3 (int8), the default model of the Handy app,
/// run through sherpa-onnx. Downloads the model on first use.
/// </summary>
public sealed class ParakeetTranscriber(
    IOptions<JarvisOptions> options,
    AssistantStatus status,
    ILogger<ParakeetTranscriber> logger) : ISpeechTranscriber, IDisposable
{
    internal const string ModelName = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";
    internal static readonly string[] ModelFiles = ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"];
    private const string BaseUrl = $"https://huggingface.co/csukuangfj/{ModelName}/resolve/main/";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private OfflineRecognizer? _recognizer;

    public async Task<Transcript> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        if (samples.Length < AudioRecorder.SampleRate / 4)
        {
            return new Transcript(string.Empty, string.Empty);
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            _recognizer ??= CreateRecognizer(await EnsureModelAsync(cancellationToken));
            using var stream = _recognizer.CreateStream();
            stream.AcceptWaveform(AudioRecorder.SampleRate, samples);
            _recognizer.Decode(stream);
            var result = new Transcript(stream.Result.Text.Trim(), string.Empty);
            logger.LogInformation("Transcribed (parakeet): {Text}", result.Text);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _recognizer?.Dispose();

    private static OfflineRecognizer CreateRecognizer(string directory)
    {
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = AudioRecorder.SampleRate;
        config.ModelConfig.Transducer.Encoder = Path.Combine(directory, ModelFiles[0]);
        config.ModelConfig.Transducer.Decoder = Path.Combine(directory, ModelFiles[1]);
        config.ModelConfig.Transducer.Joiner = Path.Combine(directory, ModelFiles[2]);
        config.ModelConfig.Tokens = Path.Combine(directory, ModelFiles[3]);
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = Math.Max(2, Environment.ProcessorCount / 2);
        config.DecodingMethod = "greedy_search";
        return new OfflineRecognizer(config);
    }

    private async Task<string> EnsureModelAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(
            options.Value.Voice.ModelDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis", "models"),
            ModelName);
        Directory.CreateDirectory(directory);
        var missing = ModelFiles.Where(f => !File.Exists(Path.Combine(directory, f))).ToList();
        if (missing.Count == 0)
        {
            return directory;
        }

        status.ReportActivity("Downloading the Parakeet speech model (one time, ~670 MB)…");
        logger.LogInformation("Downloading Parakeet model to {Path}", directory);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var file in missing)
        {
            var path = Path.Combine(directory, file);
            var temp = path + ".download";
            await using (var source = await http.GetStreamAsync(BaseUrl + file, cancellationToken))
            await using (var target = File.Create(temp))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            File.Move(temp, path, overwrite: true);
        }

        return directory;
    }
}
