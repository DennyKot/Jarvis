using Jarvis.Core.Configuration;
using Jarvis.Core.Llm;
using Microsoft.Extensions.AI;

namespace Jarvis.Core.Tests;

public class ChatClientFactoryTests
{
    [Fact]
    public void Create_Throws_WhenAzureEndpointIsMissing()
    {
        // Arrange
        var factory = new ChatClientFactory();
        var options = new LlmOptions { Provider = LlmProvider.AzureOpenAI, Model = "gpt-4.1", ApiKey = "key" };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create(options));

        // Assert
        Assert.Contains("Endpoint", exception.Message);
    }

    [Theory]
    [InlineData(LlmProvider.OpenAI)]
    [InlineData(LlmProvider.Anthropic)]
    [InlineData(LlmProvider.Ollama)]
    public void Create_ReturnsClient_WhenProviderIsConfigured(LlmProvider provider)
    {
        // Arrange
        var factory = new ChatClientFactory();
        var options = new LlmOptions { Provider = provider, Model = "some-model", ApiKey = "test-key" };

        // Act
        using var client = factory.Create(options);

        // Assert
        Assert.NotNull(client);
    }

    [Theory]
    [InlineData(false, "\"think\":false")]
    [InlineData(true, "\"think\":true")]
    public async Task Create_SendsThinkFlag_WhenOllamaChatRequestIsMade(bool thinking, string expected)
    {
        // Arrange
        using var handler = new CapturingHandler();
        var factory = new ChatClientFactory(handler);
        var options = new LlmOptions { Provider = LlmProvider.Ollama, Model = "gemma4", Thinking = thinking, KeepAlive = "30m" };
        using var client = factory.Create(options);

        // Act
        await client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(handler.ChatBody);
        Assert.Contains(expected, handler.ChatBody);
        Assert.Contains("\"keep_alive\":\"30m\"", handler.ChatBody);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? ChatBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/api/chat", StringComparison.Ordinal) == true && request.Content is not null)
            {
                ChatBody = (await request.Content.ReadAsStringAsync(cancellationToken)).Replace(" ", string.Empty, StringComparison.Ordinal);
            }

            const string reply = """{"model":"gemma4","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"ok"},"done":true,"done_reason":"stop"}""";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(reply + "\n", System.Text.Encoding.UTF8, "application/x-ndjson"),
            };
        }
    }
}
