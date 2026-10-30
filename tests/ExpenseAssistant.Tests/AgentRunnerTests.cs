using System.Text.Json;
using ExpenseAssistant.App;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Tests;

public class AgentRunnerTests
{
    [Fact]
    public async Task RunAsync_WithDirectResponse_CompletesWithUsageAndDistinctRunIds()
    {
        var usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 3, TotalTokenCount = 13 };
        var response = Reply("Which date range?");
        response.Usage = usage;
        var client = new FakeChatClient((_, _) => Task.FromResult(response));
        var runner = CreateRunner(client);
        Guid sessionId = Guid.NewGuid();

        AgentRunResult first = await runner.RunAsync(sessionId, "Expenses?", CancellationToken.None);
        AgentRunResult second = await runner.RunAsync(sessionId, "Expenses?", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, first.Status);
        Assert.Equal(1, first.ModelStepCount);
        Assert.Equal(0, first.ToolCallCount);
        Assert.Null(first.StopReason);
        Assert.Equal(1, second.ModelStepCount);
        Assert.Equal(0, second.ToolCallCount);
        Assert.Same(response, first.Response);
        Assert.Equal(sessionId, first.SessionId);
        Assert.Equal(sessionId, second.SessionId);
        Assert.NotEqual(Guid.Empty, first.RunId);
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(13, first.Usage?.TotalTokenCount);
        Assert.All(client.ReceivedRequests, messages => Assert.Equal(2, messages.Count));
    }

    [Fact]
    public async Task RunAsync_WithTools_PreservesMessagesExecutesSequentiallyAndDisablesFinalTools()
    {
        List<int> executions = [];
        AIFunction function = AIFunctionFactory.Create(async (int value) =>
        {
            await Task.Yield();
            executions.Add(value);
            return ToolResult<string>.Success($"result-{value}");
        }, "Record");
        var initialMessage = new ChatMessage(ChatRole.Assistant, [
            new TextContent("Checking."), Call("first", "Record", "{\"value\":1}"),
            Call("second", "Record", "{\"value\":2}")]);
        var client = Client(new ChatResponse(initialMessage), Reply("Done."));

        AgentRunResult result = await CreateRunner(client, [function])
            .RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(2, result.ModelStepCount);
        Assert.Equal(2, result.ToolCallCount);
        Assert.Equal(new[] { 1, 2 }, executions);
        Assert.Equal(2, client.ReceivedRequests.Count);
        Assert.Same(function, Assert.Single(client.ReceivedOptions[0]!.Tools!));
        Assert.Equal(ChatToolMode.None, client.ReceivedOptions[1]!.ToolMode);
        Assert.Null(client.ReceivedOptions[1]!.Tools);
        IReadOnlyList<ChatMessage> messages = client.ReceivedRequests[1];
        Assert.Equal(4, messages.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal(ChatRole.User, messages[1].Role);
        Assert.Same(initialMessage, messages[2]);
        Assert.Equal(ChatRole.Tool, messages[3].Role);
        FunctionResultContent[] results = messages[3].Contents.OfType<FunctionResultContent>().ToArray();
        Assert.Equal(new[] { "first", "second" }, results.Select(item => item.CallId));
        Assert.Equal("result-1", Envelope(results[0]).GetProperty("result").GetString());
        Assert.Equal("result-2", Envelope(results[1]).GetProperty("result").GetString());
    }

    [Theory]
    [InlineData("Missing", "{}", "UNKNOWN_TOOL")]
    [InlineData("Echo", "{}", "INVALID_ARGUMENTS")]
    [InlineData("Echo", "{\"value\":false}", "INVALID_ARGUMENTS")]
    public async Task RunAsync_WithRejectedTool_ForwardsDispatcherErrorAndOriginalCallId(string name, string json, string code)
    {
        var client = Client(new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call("rejected", name, json)])), Reply("Unavailable."));
        AIFunction function = AIFunctionFactory.Create((string value) => ToolResult<string>.Success(value), "Echo");

        AgentRunResult result = await CreateRunner(client, [function]).RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(1, result.ToolCallCount);
        FunctionResultContent toolResult = Assert.Single(ToolResults(client));
        Assert.Equal("rejected", toolResult.CallId);
        Assert.Equal(code, Envelope(toolResult).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RunAsync_WithExcessCalls_ExecutesOnlyCapAndRespondsToEveryId()
    {
        int executions = 0;
        AIFunction function = AIFunctionFactory.Create(() => ToolResult<string>.Success($"{++executions}"), "Count");
        var client = Client(new ChatResponse(new ChatMessage(ChatRole.Assistant, [
            Call("first", "Count"), Call("second", "Count"), Call("third", "Count") ])), Reply("Done."));

        AgentRunResult result = await CreateRunner(client, [function], maxToolCalls: 1)
            .RunAsync(Guid.NewGuid(), "Count", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(3, result.ToolCallCount);
        Assert.Equal(1, executions);
        FunctionResultContent[] results = ToolResults(client);
        Assert.Equal(new[] { "first", "second", "third" }, results.Select(item => item.CallId));
        Assert.All(results.Skip(1), item => Assert.Equal("TOOL_CALL_LIMIT", Envelope(item).GetProperty("error").GetProperty("code").GetString()));
    }

    [Theory]
    [InlineData("duplicate", "duplicate")]
    [InlineData(" ", "valid")]
    public async Task RunAsync_WithInvalidIds_FailsBeforeTools(string firstId, string secondId)
    {
        int executions = 0;
        AIFunction function = AIFunctionFactory.Create(() => ++executions, "Count");
        var client = Client(new ChatResponse(new ChatMessage(ChatRole.Assistant, [
            Call(firstId, "Count"), Call(secondId, "Count") ])));

        AgentRunResult result = await CreateRunner(client, [function]).RunAsync(Guid.NewGuid(), "Count", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal(1, result.ModelStepCount);
        Assert.Equal(0, result.ToolCallCount);
        Assert.Null(result.Response);
        Assert.Equal(0, executions);
        Assert.Single(client.ReceivedRequests);
    }

    [Fact]
    public async Task RunAsync_WithAdditionalFinalTool_FailsWithoutExecutingIt()
    {
        int executions = 0;
        AIFunction function = AIFunctionFactory.Create(() => ++executions, "Count");
        var client = Client(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call("first", "Count")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call("extra", "Count")])));

        AgentRunResult result = await CreateRunner(client, [function]).RunAsync(Guid.NewGuid(), "Count", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Null(result.Response);
        Assert.Equal(1, executions);
        Assert.Equal(2, client.ReceivedRequests.Count);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task RunAsync_WhenModelIsInterrupted_ReturnsCancellationOrTimeout(int requestIndex, bool timeout)
    {
        using var cancellation = new CancellationTokenSource();
        int requestCount = 0;
        var client = new FakeChatClient(async (_, token) =>
        {
            if (requestCount++ == requestIndex)
            {
                if (!timeout)
                {
                    cancellation.Cancel();
                }
                await Task.Delay(Timeout.Infinite, token);
            }
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call("first", "Missing")]));
        });
        var runner = CreateRunner(client, timeout: timeout ? TimeSpan.FromMilliseconds(100) : null);

        AgentRunResult result = await runner.RunAsync(Guid.NewGuid(), "Expenses?", cancellation.Token);

        Assert.Equal(timeout ? AgentRunStatus.BudgetExceeded : AgentRunStatus.Cancelled, result.Status);
        Assert.Equal(timeout ? AgentStopReason.DurationLimit : (AgentStopReason?)null, result.StopReason);
        Assert.Equal(requestIndex + 1, result.ModelStepCount);
        Assert.Equal(requestIndex, result.ToolCallCount);
        Assert.Null(result.Response);
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task RunAsync_WhenAlreadyCancelled_DoesNotRequestModel()
    {
        var client = Client();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        AgentRunResult result = await CreateRunner(client).RunAsync(Guid.NewGuid(), "Expenses?", cancellation.Token);

        Assert.Equal(AgentRunStatus.Cancelled, result.Status);
        Assert.Equal(0, result.ModelStepCount);
        Assert.Equal(0, result.ToolCallCount);
        Assert.Empty(client.ReceivedRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_WhenToolIsInterrupted_DoesNotRequestFinalModel(bool timeout)
    {
        using var cancellation = new CancellationTokenSource();
        AIFunction function = AIFunctionFactory.Create(async (CancellationToken token) =>
        {
            if (!timeout)
            {
                cancellation.Cancel();
            }
            await Task.Delay(Timeout.Infinite, token);
            return "unreachable";
        }, "Cancel");
        var client = Client(new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call("first", "Cancel")])));

        AgentRunResult result = await CreateRunner(client, [function], timeout: timeout ? TimeSpan.FromMilliseconds(100) : null)
            .RunAsync(Guid.NewGuid(), "Expenses?", cancellation.Token);

        Assert.Equal(timeout ? AgentRunStatus.BudgetExceeded : AgentRunStatus.Cancelled, result.Status);
        Assert.Equal(timeout ? AgentStopReason.DurationLimit : (AgentStopReason?)null, result.StopReason);
        Assert.Equal(1, result.ModelStepCount);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Single(client.ReceivedRequests);
    }

    [Fact]
    public async Task RunAsync_WhenProviderFails_ReturnsSafeError()
    {
        var client = new FakeChatClient((_, _) => throw new InvalidOperationException("PRIVATE provider details"));

        AgentRunResult result = await CreateRunner(client).RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal(1, result.ModelStepCount);
        Assert.Equal("The assistant request could not be completed.", result.Error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RunAsync_AggregatesUsageWithoutInventingMissingCounts(bool missingUsage, bool missingCount)
    {
        var initial = new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call("first", "Missing")]))
        {
            Usage = missingUsage ? null : new UsageDetails { InputTokenCount = 10, OutputTokenCount = 3, TotalTokenCount = missingCount ? null : 13 }
        };
        var final = Reply("Done.");
        final.Usage = new UsageDetails { InputTokenCount = 20, OutputTokenCount = 5, TotalTokenCount = 25 };
        var client = Client(initial, final);

        AgentRunResult result = await CreateRunner(client).RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Same(final.Usage, result.Response?.Usage);
        Assert.Equal(25, final.Usage.TotalTokenCount);
        if (missingUsage)
        {
            Assert.Null(result.Usage);
        }
        else
        {
            Assert.Equal(30, result.Usage?.InputTokenCount);
            Assert.Equal(8, result.Usage?.OutputTokenCount);
            Assert.Equal(missingCount ? (long?)null : 38, result.Usage?.TotalTokenCount);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4294967295d)]
    public void Constructor_RejectsUnsupportedTimeout(double milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateRunner(Client(), timeout: TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void Constructor_RejectsNonPositiveCountLimits(int maxModelSteps, int maxToolCalls)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentRunner(Client(), new ToolDispatcher([]),
            "Use tools.", new AgentRunOptions(MaxModelSteps: maxModelSteps, MaxToolCalls: maxToolCalls)));
    }

    [Fact]
    public async Task RunAsync_WithDefaultOptions_CompletesWithoutExplicitDuration()
    {
        var client = Client(Reply("Which date range?"));
        var runner = new AgentRunner(client, new ToolDispatcher([]), "Use tools.", new AgentRunOptions());

        AgentRunResult result = await runner.RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(1, result.ModelStepCount);
        Assert.Equal(0, result.ToolCallCount);
        Assert.Null(result.Error);
        Assert.Null(result.StopReason);
    }

    [Fact]
    public async Task RunAsync_WithOneModelStepAndTools_StopsWithoutReturningIntermediateAnswer()
    {
        var initial = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new TextContent("PRIVATE_INTERMEDIATE_TEXT"), Call("first", "Missing")]))
        {
            Usage = new UsageDetails { TotalTokenCount = 13 }
        };
        var client = Client(initial);
        var runner = new AgentRunner(client, new ToolDispatcher([]), "Use tools.",
            new AgentRunOptions(MaxModelSteps: 1));

        AgentRunResult result = await runner.RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(AgentRunStatus.BudgetExceeded, result.Status);
        Assert.Equal(AgentStopReason.ModelStepLimit, result.StopReason);
        Assert.Null(result.Response);
        Assert.Equal(1, result.ModelStepCount);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Equal(13, result.Usage?.TotalTokenCount);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("PRIVATE_", result.Error);
        Assert.Single(client.ReceivedRequests);
    }

    [Fact]
    public async Task RunAsync_WithOneModelStepAndDirectAnswer_CompletesAtTheLimit()
    {
        var client = Client(Reply("Which date range?"));
        var runner = new AgentRunner(client, new ToolDispatcher([]), "Use tools.",
            new AgentRunOptions(MaxModelSteps: 1));

        AgentRunResult result = await runner.RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("Which date range?", result.Response?.Text);
        Assert.Null(result.StopReason);
        Assert.Equal(1, result.ModelStepCount);
        Assert.Equal(0, result.ToolCallCount);
        Assert.Single(client.ReceivedRequests);
    }

    private static AgentRunner CreateRunner(FakeChatClient client, IReadOnlyList<AIFunction>? functions = null,
        int maxToolCalls = 4, TimeSpan? timeout = null) =>
        new(client, new ToolDispatcher(functions ?? []), "Use tools.",
            new AgentRunOptions(MaxToolCalls: maxToolCalls, MaxDuration: timeout));

    private static ChatResponse Reply(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private static FakeChatClient Client(params ChatResponse[] responses)
    {
        var queue = new Queue<ChatResponse>(responses);
        return new FakeChatClient((_, _) => Task.FromResult(queue.Dequeue()));
    }

    private static FunctionCallContent Call(string id, string name, string json = "{}") =>
        new(id, name, JsonSerializer.Deserialize<Dictionary<string, object?>>(json));

    private static FunctionResultContent[] ToolResults(FakeChatClient client) =>
        client.ReceivedRequests[1].Last().Contents.OfType<FunctionResultContent>().ToArray();

    private static JsonElement Envelope(FunctionResultContent result) =>
        JsonSerializer.SerializeToElement(result.Result, JsonSerializerOptions.Web);
}