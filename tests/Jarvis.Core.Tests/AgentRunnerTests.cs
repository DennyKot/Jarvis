using Jarvis.Core.Agent;
using Jarvis.Core.Configuration;
using Jarvis.Core.Llm;
using Jarvis.Core.Safety;
using Jarvis.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Jarvis.Core.Tests;

public class AgentRunnerTests
{
    private readonly ScriptedChatClient _client = new();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();
    private readonly AgentObservations _observations = new();
    private readonly List<string> _invocations = [];
    private readonly AssistantStatus _status = new();

    private AgentRunner CreateRunner(int maxSteps = 10, params JarvisTool[] tools)
    {
        var options = Options.Create(new JarvisOptions { Llm = new LlmOptions { MaxSteps = maxSteps } });
        var factory = Substitute.For<IChatClientFactory>();
        factory.Create(Arg.Any<LlmOptions>()).Returns(_client);
        var provider = Substitute.For<IToolProvider>();
        provider.GetTools().Returns(tools);
        var status = _status;
        return new AgentRunner(factory, [provider], new RiskPolicy(options), new ActionGuard(_confirmation, status, options),
            status, _observations, options, NullLogger<AgentRunner>.Instance);
    }

    private JarvisTool ClickTool(ToolRisk risk = ToolRisk.Action) =>
        JarvisTool.Create((string element) =>
        {
            _invocations.Add(element);
            return $"clicked {element}";
        }, "click", "Click", risk, "element");

    private static ChatResponse Call(string name, string element) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-" + element, name, new Dictionary<string, object?> { ["element"] = element })]));

    private static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private JarvisTool NamedTool(string name) =>
        JarvisTool.Create((string element) =>
        {
            _invocations.Add(name);
            return "ok";
        }, name, name);

    [Fact]
    public async Task RunAsync_RevealsWebTools_AfterBrowserOpen()
    {
        // Arrange
        _client.Enqueue(Call("browser_open", "Wikipedia"), Text("Opened."));
        var runner = CreateRunner(tools: [NamedTool("browser_open"), NamedTool("browser_click"), NamedTool("ui_click")]);

        // Act
        await runner.RunAsync("open wikipedia", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["browser_open"], _client.ToolNames[0]);
        Assert.Equal(["browser_open", "browser_click"], _client.ToolNames[1]);
    }

    [Fact]
    public async Task RunAsync_InvokesHiddenTool_WhenModelCallsItDirectly()
    {
        // Arrange
        _client.Enqueue(Call("ui_click", "OK button"), Text("Clicked."));
        var runner = CreateRunner(tools: [NamedTool("launch_app"), NamedTool("ui_click")]);

        // Act
        await runner.RunAsync("click ok", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["ui_click"], _invocations);
        Assert.Contains("ui_click", _client.ToolNames[1]);
    }

    [Fact]
    public async Task RunAsync_ReturnsFinalAnswer_AfterInvokingRequestedTool()
    {
        // Arrange
        _client.Enqueue(Call("click", "Search button"), Text("Done searching."));
        var runner = CreateRunner(tools: ClickTool());

        // Act
        var answer = await runner.RunAsync("search something", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Done searching.", answer);
        Assert.Equal(["Search button"], _invocations);
        var toolResult = _client.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Equal("clicked Search button", toolResult.Result);
    }

    [Fact]
    public async Task RunAsync_StreamsOnlyFinalAnswer_WhenStepsCallTools()
    {
        // Arrange
        var streamed = new List<string>();
        _status.ReplyStreamed += streamed.Add;
        _client.Enqueue(Call("click", "Search button"), Text("Done searching."));
        var runner = CreateRunner(tools: ClickTool());

        // Act
        await runner.RunAsync("search something", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Done searching.", streamed.Last());
    }

    [Fact]
    public async Task RunAsync_SkipsTool_WhenUserDeniesRiskyAction()
    {
        // Arrange
        _confirmation.ConfirmAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>()).Returns(false);
        _client.Enqueue(Call("click", "Delete account"), Text("Okay, I did not delete it."));
        var runner = CreateRunner(tools: ClickTool());

        // Act
        await runner.RunAsync("delete my account", TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_invocations);
        var toolResult = _client.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.StartsWith("Denied", toolResult.Result?.ToString());
    }

    [Fact]
    public async Task RunAsync_InvokesTool_WhenUserApprovesRiskyAction()
    {
        // Arrange
        _confirmation.ConfirmAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>()).Returns(true);
        _client.Enqueue(Call("click", "Send"), Text("Sent."));
        var runner = CreateRunner(tools: ClickTool());

        // Act
        await runner.RunAsync("send it", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["Send"], _invocations);
        await _confirmation.Received(1).ConfirmAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_StopsWithMessage_WhenMaxStepsReached()
    {
        // Arrange
        _client.Enqueue(Call("click", "a"), Call("click", "b"), Call("click", "c"));
        var runner = CreateRunner(maxSteps: 2, ClickTool());

        // Act
        var answer = await runner.RunAsync("loop", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, _invocations.Count);
        Assert.Contains("2 steps", answer);
    }

    [Fact]
    public async Task RunAsync_SendsScreenshotToModel_WhenToolAddsImage()
    {
        // Arrange
        var screenshot = JarvisTool.Create(() =>
        {
            _observations.AddImage([1, 2, 3], "image/jpeg", "Screen");
            return "taken";
        }, "screenshot", "Screenshot", ToolRisk.ReadOnly);
        _client.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "screenshot")])), Text("I see it."));
        var runner = CreateRunner(tools: screenshot);

        // Act
        await runner.RunAsync("look", TestContext.Current.CancellationToken);

        // Assert
        var image = _client.Requests[1].Last().Contents.OfType<DataContent>().Single();
        Assert.Equal("image/jpeg", image.MediaType);
    }

    [Fact]
    public async Task RunAsync_KeepsOnlyLatestScreenshot_WhenToolAddsSeveral()
    {
        // Arrange
        var screenshot = JarvisTool.Create(() =>
        {
            _observations.AddImage([1, 2, 3], "image/jpeg", "Screen");
            return "taken";
        }, "screenshot", "Screenshot", ToolRisk.ReadOnly);
        _client.Enqueue(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "screenshot")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c2", "screenshot", new Dictionary<string, object?> { ["n"] = 2 })])),
            Text("Done."));
        var runner = CreateRunner(tools: screenshot);

        // Act
        await runner.RunAsync("look twice", TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(_client.Requests[2].SelectMany(m => m.Contents).OfType<DataContent>());
    }

    [Fact]
    public async Task RunAsync_ReportsErrorToModel_WhenToolIsUnknown()
    {
        // Arrange
        _client.Enqueue(Call("does_not_exist", "x"), Text("Sorry."));
        var runner = CreateRunner(tools: ClickTool());

        // Act
        await runner.RunAsync("do it", TestContext.Current.CancellationToken);

        // Assert
        var toolResult = _client.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Contains("unknown tool", toolResult.Result?.ToString());
    }

    [Fact]
    public async Task RunAsync_IncludesPreviousTurn_WhenCalledTwice()
    {
        // Arrange
        _client.Enqueue(Text("First answer."), Text("Second answer."));
        var runner = CreateRunner(tools: ClickTool());

        // Act
        await runner.RunAsync("first", TestContext.Current.CancellationToken);
        await runner.RunAsync("second", TestContext.Current.CancellationToken);

        // Assert
        var texts = _client.Requests[1].Select(m => m.Text).ToList();
        Assert.Contains("first", texts);
        Assert.Contains("First answer.", texts);
    }

    [Fact]
    public async Task RunAsync_DoesNotInvokeToolAgain_WhenSameCallRepeatsThreeTimes()
    {
        // Arrange
        _client.Enqueue(Call("click", "Next"), Call("click", "Next"), Call("click", "Next"), Text("Gave up."));
        var runner = CreateRunner(tools: ClickTool());

        // Act
        await runner.RunAsync("click next", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, _invocations.Count);
        var lastResult = _client.Requests[3].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
        Assert.Contains("Do something different", lastResult.Result?.ToString());
    }

    [Fact]
    public void CompactToolResults_TruncatesOnlyLongOutputs_WhenCalled()
    {
        // Arrange
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Tool, [new FunctionResultContent("c1", new string('x', 5000))]),
            new(ChatRole.Tool, [new FunctionResultContent("c2", "short")]),
        };

        // Act
        AgentRunner.CompactToolResults(messages, maxLength: 100);

        // Assert
        var results = messages.Select(m => (string)((FunctionResultContent)m.Contents[0]).Result!).ToList();
        Assert.StartsWith(new string('x', 100) + "\n…(older output truncated", results[0]);
        Assert.Equal("short", results[1]);
    }

    [Fact]
    public void TrimHistory_DropsOldestWholeTurns_WhenOverLimit()
    {
        // Arrange
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "turn 1"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "click")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "ok")]),
            new(ChatRole.Assistant, "done 1"),
            new(ChatRole.User, "turn 2"),
            new(ChatRole.Assistant, "done 2"),
        };

        // Act
        AgentRunner.TrimHistory(history, 3);

        // Assert
        Assert.Equal(["turn 2", "done 2"], history.Select(m => m.Text));
    }

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new();

        public List<List<ChatMessage>> Requests { get; } = [];

        public List<string[]> ToolNames { get; } = [];

        public void Enqueue(params ChatResponse[] responses)
        {
            foreach (var response in responses)
            {
                _responses.Enqueue(response);
            }
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            ToolNames.Add(options?.Tools?.Select(t => t.Name).ToArray() ?? []);
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : Text("(no more scripted responses)"));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
