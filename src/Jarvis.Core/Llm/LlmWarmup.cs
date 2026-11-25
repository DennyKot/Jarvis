using Jarvis.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

namespace Jarvis.Core.Llm;

public interface ILlmWarmup
{
    /// <summary>Loads the model in the background so the first agent step doesn't pay the load time. Never throws.</summary>
    void WarmUp();
}

/// <summary>Preloads a local Ollama model (same num_ctx as chat requests, or Ollama would reload it). No-op for cloud providers.</summary>
public sealed class LlmWarmup(IOptions<JarvisOptions> options, ILogger<LlmWarmup> logger, TimeProvider timeProvider) : ILlmWarmup
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);
    private readonly Lock _lock = new();
    private DateTimeOffset _lastWarmUp = DateTimeOffset.MinValue;

    public void WarmUp()
    {
        var llm = options.Value.Llm;
        if (llm.Provider != LlmProvider.Ollama)
        {
            return;
        }

        lock (_lock)
        {
            var now = timeProvider.GetUtcNow();
            if (now - _lastWarmUp < MinInterval)
            {
                return;
            }

            _lastWarmUp = now;
        }

        _ = Task.Run(() => WarmUpOllamaAsync(llm));
    }

    private async Task WarmUpOllamaAsync(LlmOptions llm)
    {
        try
        {
            var endpoint = string.IsNullOrWhiteSpace(llm.Endpoint) ? "http://localhost:11434" : llm.Endpoint;
            using var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = llm.RunTimeout };
            using var client = new OllamaApiClient(http, llm.Model);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // An empty prompt only loads the model into memory.
            var request = new GenerateRequest
            {
                Model = llm.Model,
                Prompt = string.Empty,
                KeepAlive = llm.KeepAlive,
                Options = new RequestOptions { NumCtx = llm.ContextLength },
            };
            await foreach (var _ in client.GenerateAsync(request))
            {
            }

            logger.LogInformation("Model {Model} warmed up in {Elapsed:0.0}s", llm.Model, stopwatch.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Model warm-up failed");
        }
    }
}
