using System.Text;
using Jarvis.Core.Agent;
using Jarvis.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whisper.net;
using Whisper.net.Ggml;

namespace Jarvis.Voice;

/// <summary>Local, offline speech-to-text with Whisper.net. Downloads the model on first use.</summary>
public sealed class WhisperTranscriber(
    IOptions<JarvisOptions> options,
    AssistantStatus status,
    ILogger<WhisperTranscriber> logger) : ISpeechTranscriber, IDisposable
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private WhisperFactory? _factory;

    private VoiceOptions Voice => options.Value.Voice;

    public async Task<Transcript> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        if (samples.Length < AudioRecorder.SampleRate / 4)
        {
            return new Transcript(string.Empty, string.Empty);
        }

        var factory = await GetFactoryAsync(cancellationToken);
        var language = Voice.Language;
        var builder = factory.CreateBuilder().WithThreads(Math.Max(2, Environment.ProcessorCount / 2)).WithNoContext();

        if (string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase) && Voice.AllowedLanguages.Length > 0)
        {
            using var detector = factory.CreateBuilder().WithLanguage("auto").Build();
            (language, _) = detector.DetectLanguageWithProbability(samples, Voice.AllowedLanguages);
        }

        await using var processor = builder.WithLanguage(string.IsNullOrWhiteSpace(language) ? "auto" : language).Build();
        var text = new StringBuilder();
        string? detected = null;
        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken))
        {
            text.Append(segment.Text);
            detected ??= segment.Language;
        }

        var result = new Transcript(CleanUp(text.ToString()), detected ?? language ?? string.Empty);
        logger.LogInformation("Transcribed ({Language}): {Text}", result.Language, result.Text);
        return result;
    }

    /// <summary>Whisper emits markers such as "[BLANK_AUDIO]" or "(music)" for non-speech.</summary>
    internal static string CleanUp(string text)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", " ");
        return System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    public Task PrepareAsync(CancellationToken cancellationToken) => GetFactoryAsync(cancellationToken);

    public void Dispose() => _factory?.Dispose();

    private async Task<WhisperFactory> GetFactoryAsync(CancellationToken cancellationToken)
    {
        if (_factory is not null)
        {
            return _factory;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_factory is null)
            {
                var path = await EnsureModelAsync(cancellationToken);
                _factory = WhisperFactory.FromPath(path);
            }

            return _factory;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<string> EnsureModelAsync(CancellationToken cancellationToken)
    {
        var type = ParseModel(Voice.WhisperModel);
        var directory = Voice.ModelDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis", "models");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"ggml-{type.ToString().ToLowerInvariant()}.bin");
        if (File.Exists(path))
        {
            return path;
        }

        status.ReportActivity($"Downloading the Whisper '{Voice.WhisperModel}' speech model (one time)…");
        logger.LogInformation("Downloading Whisper model {Type} to {Path}", type, path);
        var temp = path + ".download";
        await using (var model = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type, QuantizationType.NoQuantization, cancellationToken))
        await using (var file = File.Create(temp))
        {
            await model.CopyToAsync(file, cancellationToken);
        }

        File.Move(temp, path, overwrite: true);
        return path;
    }

    internal static GgmlType ParseModel(string name) => name.Replace("-", string.Empty).ToLowerInvariant() switch
    {
        "tiny" => GgmlType.Tiny,
        "base" => GgmlType.Base,
        "small" => GgmlType.Small,
        "medium" => GgmlType.Medium,
        "largev3" or "large" => GgmlType.LargeV3,
        "largev3turbo" or "turbo" => GgmlType.LargeV3Turbo,
        _ => throw new InvalidOperationException($"Unknown Whisper model '{name}'. Use tiny, base, small, medium, large-v3 or large-v3-turbo."),
    };
}
