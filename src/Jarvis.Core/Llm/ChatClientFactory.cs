using System.ClientModel;
using Anthropic.SDK;
using Azure.AI.OpenAI;
using Jarvis.Core.Configuration;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;
using OpenAI;

namespace Jarvis.Core.Llm;

public interface IChatClientFactory
{
    IChatClient Create(LlmOptions options);
}

public sealed class ChatClientFactory : IChatClientFactory
{
    private readonly HttpMessageHandler? _ollamaHandler;

    public ChatClientFactory()
    {
    }

    /// <summary>Test hook: routes Ollama requests through <paramref name="ollamaHandler"/>.</summary>
    internal ChatClientFactory(HttpMessageHandler ollamaHandler) => _ollamaHandler = ollamaHandler;

    public IChatClient Create(LlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new InvalidOperationException("Jarvis:Llm:Model is not configured.");
        }

        return options.Provider switch
        {
            LlmProvider.OpenAI => CreateOpenAI(options),
            LlmProvider.AzureOpenAI => CreateAzureOpenAI(options),
            LlmProvider.Anthropic => CreateAnthropic(options),
            LlmProvider.Ollama => CreateOllama(options),
            _ => throw new NotSupportedException($"LLM provider '{options.Provider}' is not supported."),
        };
    }

    internal static string ResolveApiKey(LlmOptions options, string environmentVariable, bool required = true)
    {
        var key = !string.IsNullOrWhiteSpace(options.ApiKey)
            ? options.ApiKey
            : Environment.GetEnvironmentVariable(environmentVariable);

        if (required && string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                $"No API key for {options.Provider}. Set it with 'dotnet user-secrets set Jarvis:Llm:ApiKey <key>' " +
                $"in src/Jarvis.App or the {environmentVariable} environment variable.");
        }

        return key ?? string.Empty;
    }

    private static IChatClient CreateOpenAI(LlmOptions options)
    {
        var apiKey = ResolveApiKey(options, "OPENAI_API_KEY", required: string.IsNullOrWhiteSpace(options.Endpoint));
        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            clientOptions.Endpoint = new Uri(options.Endpoint);
        }

        var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? "unused" : apiKey);
        return new OpenAIClient(credential, clientOptions).GetChatClient(options.Model).AsIChatClient();
    }

    private static IChatClient CreateAzureOpenAI(LlmOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new InvalidOperationException("Jarvis:Llm:Endpoint must be set to the Azure OpenAI resource URL.");
        }

        var apiKey = ResolveApiKey(options, "AZURE_OPENAI_API_KEY");
        var client = new AzureOpenAIClient(new Uri(options.Endpoint), new ApiKeyCredential(apiKey));
        return client.GetChatClient(options.Model).AsIChatClient();
    }

    private static IChatClient CreateAnthropic(LlmOptions options)
    {
        var apiKey = ResolveApiKey(options, "ANTHROPIC_API_KEY");
        var client = new AnthropicClient(new APIAuthentication(apiKey));
        return new ChatClientBuilder(client.Messages)
            .ConfigureOptions(o => o.ModelId ??= options.Model)
            .Build();
    }

    private IChatClient CreateOllama(LlmOptions options)
    {
        var endpoint = string.IsNullOrWhiteSpace(options.Endpoint) ? "http://localhost:11434" : options.Endpoint;

        // Local models can be slow (first load, CPU offloading); the default 100 s HttpClient timeout is too short.
        var http = _ollamaHandler is null ? new HttpClient() : new HttpClient(_ollamaHandler, disposeHandler: false);
        http.BaseAddress = new Uri(endpoint);
        http.Timeout = options.RunTimeout;

        // Ollama defaults to a small context window and silently drops the start of longer prompts
        // (system prompt and the user's command), so request a window large enough for tools and page snapshots.
        return new ChatClientBuilder(new OllamaApiClient(http, options.Model))
            .ConfigureOptions(o =>
            {
                o.AddOllamaOption(OllamaOption.NumCtx, options.ContextLength);

                // Ollama unloads idle models after 5 minutes; reloading costs 10-20 s on the next command.
                // Ollama also enables thinking by default for models that support it (gemma4: ~3x the output tokens
                // per tool call), so send "think" explicitly. Per-request flag: it doesn't reload the model.
                o.RawRepresentationFactory ??= (Func<IChatClient, object?>)(_ => new OllamaSharp.Models.Chat.ChatRequest
                {
                    KeepAlive = options.KeepAlive,
                    Think = options.Thinking,
                });
            })
            .Build();
    }
}
