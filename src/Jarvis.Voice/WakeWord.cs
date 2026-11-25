using System.Text.RegularExpressions;
using Jarvis.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Voice;

/// <summary>Detects a wake phrase at the start of a transcript and returns the command that follows it.</summary>
public static partial class WakeWordMatcher
{
    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();

    /// <param name="command">Text after the wake phrase (empty when the user only said the wake phrase).</param>
    public static bool TryMatch(string transcript, IReadOnlyList<string> wakeWords, out string command)
    {
        command = string.Empty;
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }

        var words = WordRegex().Matches(transcript);
        foreach (var phrase in wakeWords.OrderByDescending(w => w.Length))
        {
            var expected = WordRegex().Matches(phrase).Select(m => m.Value).ToArray();
            if (expected.Length == 0 || expected.Length > words.Count)
            {
                continue;
            }

            if (expected.Select((w, i) => IsSameWord(w, words[i].Value)).All(x => x))
            {
                var last = words[expected.Length - 1];
                command = transcript[(last.Index + last.Length)..].TrimStart(' ', ',', '.', '!', '?', '-', ':', ';').Trim();
                return true;
            }
        }

        return false;
    }

    // Tolerate one-letter ASR slips ("Jervis") on longer words.
    private static bool IsSameWord(string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return expected.Length >= 5 && Distance(expected.ToLowerInvariant(), actual.ToLowerInvariant()) <= 1;
    }

    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return previous[b.Length];
    }
}

/// <summary>
/// Background wake-word listener: records speech segments, transcribes them locally and raises
/// <see cref="Detected"/> when one starts with a wake phrase. Suspend it while Jarvis is busy.
/// </summary>
public sealed class WakeWordListener(
    AudioRecorder recorder,
    ISpeechTranscriber transcriber,
    IOptions<JarvisOptions> options,
    ILogger<WakeWordListener> logger) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _segment;
    private TaskCompletionSource _resumed = CompletedSignal();
    private Task? _loop;

    /// <summary>Raised on a background thread with the command spoken after the wake phrase (may be empty).</summary>
    public event Action<string>? Detected;

    public bool IsEnabled => options.Value.Voice.WakeWordEnabled;

    public void Start()
    {
        if (IsEnabled && _loop is null)
        {
            _loop = Task.Run(() => RunAsync(_lifetime.Token));
        }
    }

    /// <summary>Stops listening (and drops the current segment) until <see cref="Resume"/>.</summary>
    public void Suspend()
    {
        lock (_gate)
        {
            if (_resumed.Task.IsCompleted)
            {
                _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _segment?.Cancel();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _resumed.TrySetResult();
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        Resume();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Wake word listener started");
        while (!cancellationToken.IsCancellationRequested)
        {
            CancellationTokenSource segment;
            Task resumed;
            lock (_gate)
            {
                resumed = _resumed.Task;
                segment = _segment = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }

            try
            {
                await resumed.WaitAsync(cancellationToken);
                var samples = await recorder.RecordAsync(CancellationToken.None, segment.Token);
                if (samples.Length == 0)
                {
                    continue;
                }

                var transcript = await transcriber.TranscribeAsync(samples, segment.Token);
                if (!segment.IsCancellationRequested
                    && WakeWordMatcher.TryMatch(transcript.Text, options.Value.Voice.WakeWords, out var command))
                {
                    logger.LogInformation("Wake word detected");
                    Detected?.Invoke(command);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Wake word listening failed");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
            finally
            {
                lock (_gate)
                {
                    _segment = null;
                }

                segment.Dispose();
            }
        }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }
}
