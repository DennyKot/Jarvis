using Jarvis.Core.Configuration;
using Jarvis.Core.Llm;

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
}
