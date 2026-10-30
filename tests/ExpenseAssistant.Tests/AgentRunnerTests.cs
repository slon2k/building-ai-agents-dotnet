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
    public async Task RunAsync_WithTools_PreservesMessagesExecutesSequentiallyAndKeepsToolsAvailable()
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
        Assert.NotEqual(ChatToolMode.None, client.ReceivedOptions[1]!.ToolMode);
        Assert.Same(function, Assert.Single(client.ReceivedOptions[1]!.Tools!));
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
    public async Task RunAsync_WithExcessCalls_RejectsWholeBatchWithoutAnotherModelRequest()
    {
        int executions = 0;
        AIFunction function = AIFunctionFactory.Create(() => ToolResult<string>.Success($"{++executions}"), "Count");
        var client = Client(new ChatResponse(new ChatMessage(ChatRole.Assistant, [
            Call("first", "Count"), Call("second", "Count"), Call("third", "Count") ])), Reply("Done."));

        AgentRunResult result = await CreateRunner(client, [function], maxToolCalls: 1)
            .RunAsync(Guid.NewGuid(), "Count", CancellationToken.None);

        Assert.Equal(AgentRunStatus.BudgetExceeded, result.Status);
        Assert.Equal(AgentStopReason.ToolCallLimit, result.StopReason);
        Assert.Null(result.Response);
        Assert.Equal(1, result.ModelStepCount);
        Assert.Equal(3, result.ToolCallCount);
        Assert.Equal(0, executions);
        Assert.Single(client.ReceivedRequests);
        Assert.Equal(2, client.ReceivedRequests[0].Count);
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
    public async Task RunAsync_WithSuccessiveToolRequests_ExecutesBothRoundsAndCompletes()
    {
        int executions = 0;
        AIFunction function = AIFunctionFactory.Create(() => ++executions, "Count");
        var client = Client(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call("first", "Count")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call("extra", "Count")])),
            Reply("Done."));

        AgentRunResult result = await CreateRunner(client, [function]).RunAsync(Guid.NewGuid(), "Count", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("Done.", result.Response?.Text);
        Assert.Equal(2, executions);
        Assert.Equal(2, result.ToolCallCount);
        Assert.Equal(3, result.ModelStepCount);
        Assert.Equal(3, client.ReceivedRequests.Count);
        Assert.Equal(new[] { 2, 4, 6 }, client.ReceivedRequests.Select(messages => messages.Count));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
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
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call($"call-{requestCount}", "Missing")]));
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

    [Fact]
    public async Task RunAsync_WhenModelKeepsRequestingTools_StopsAtModelStepLimit()
    {
        int requests = 0;
        int executions = 0;
        AIFunction function = AIFunctionFactory.Create(() => ++executions, "Count");
        var client = new FakeChatClient((_, _) => Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call($"call-{++requests}", "Count")]))));

        AgentRunResult result = await CreateRunner(client, [function], maxToolCalls: 10, maxModelSteps: 2)
            .RunAsync(Guid.NewGuid(), "Count", CancellationToken.None);

        Assert.Equal(AgentRunStatus.BudgetExceeded, result.Status);
        Assert.Equal(AgentStopReason.ModelStepLimit, result.StopReason);
        Assert.Null(result.Response);
        Assert.Equal(2, requests);
        Assert.Equal(2, executions);
        Assert.Equal(2, result.ModelStepCount);
        Assert.Equal(2, result.ToolCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_WhenToolBudgetIsExactlyConsumed_StopsWithoutAnotherModelRequest(bool unknown)
    {
        int executions = 0;
        AIFunction function = AIFunctionFactory.Create(() => ++executions, "Count");
        var client = Client(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [Call("first", unknown ? "Missing" : "Count")])));

        AgentRunResult result = await CreateRunner(client, [function], maxToolCalls: 1)
            .RunAsync(Guid.NewGuid(), "Count", CancellationToken.None);

        Assert.Equal(AgentRunStatus.BudgetExceeded, result.Status);
        Assert.Equal(AgentStopReason.ToolCallLimit, result.StopReason);
        Assert.Null(result.Response);
        Assert.Equal(unknown ? 0 : 1, executions);
        Assert.Equal(1, result.ModelStepCount);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Single(client.ReceivedRequests);
    }

    [Fact]
    public async Task RunAsync_WhenLaterBatchExceedsRemainingBudget_ExecutesNoneOfThatBatch()
    {
        int executions = 0;
        AIFunction function = AIFunctionFactory.Create(() => ++executions, "Count");
        var client = Client(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call("first", "Count")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call("second", "Count"), Call("third", "Count")])));

        AgentRunResult result = await CreateRunner(client, [function], maxToolCalls: 2)
            .RunAsync(Guid.NewGuid(), "Count", CancellationToken.None);

        Assert.Equal(AgentRunStatus.BudgetExceeded, result.Status);
        Assert.Equal(AgentStopReason.ToolCallLimit, result.StopReason);
        Assert.Null(result.Response);
        Assert.Equal(1, executions);
        Assert.Equal(2, result.ModelStepCount);
        Assert.Equal(3, result.ToolCallCount);
        Assert.Equal(2, client.ReceivedRequests.Count);
        Assert.Equal(4, client.ReceivedRequests[1].Count);
        Assert.Equal("first", Assert.Single(ToolResults(client)).CallId);
    }

    [Fact]
    public async Task RunAsync_WithDependentToolCalls_UsesPriorResultsAndAggregatesAllSteps()
    {
        AIFunction summary = AIFunctionFactory.Create(() => ToolResult<string>.Success("groceries"), "Summary");
        List<string> categories = [];
        AIFunction search = AIFunctionFactory.Create((string category) =>
        {
            categories.Add(category);
            return ToolResult<string>.Success("purchase");
        }, "Search");
        int requests = 0;
        var client = new FakeChatClient((messages, _) =>
        {
            requests++;
            ChatResponse response;
            if (requests == 1)
            {
                response = new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call("summary", "Summary")]));
            }
            else if (requests == 2)
            {
                FunctionResultContent result = Assert.IsType<FunctionResultContent>(Assert.Single(messages.Last().Contents));
                string category = Envelope(result).GetProperty("result").GetString()!;
                response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [Call("search", "Search", JsonSerializer.Serialize(new { category }))]));
            }
            else
            {
                response = Reply("Groceries: purchase.");
            }
            response.Usage = new UsageDetails { InputTokenCount = requests, OutputTokenCount = 1, TotalTokenCount = requests + 1 };
            return Task.FromResult(response);
        });

        AgentRunResult run = await CreateRunner(client, [summary, search])
            .RunAsync(Guid.NewGuid(), "Which category and purchases?", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(new[] { "groceries" }, categories);
        Assert.Equal(3, run.ModelStepCount);
        Assert.Equal(2, run.ToolCallCount);
        Assert.Equal(6, run.Usage?.InputTokenCount);
        Assert.Equal(3, run.Usage?.OutputTokenCount);
        Assert.Equal(9, run.Usage?.TotalTokenCount);
        Assert.Equal(4, run.Response?.Usage?.TotalTokenCount);
        Assert.Equal("Groceries: purchase.", run.Response?.Text);
        Assert.Equal(new[] { 2, 4, 6 }, client.ReceivedRequests.Select(messages => messages.Count));
        Assert.All(client.ReceivedOptions, options => Assert.Equal(2, options!.Tools!.Count));
        Assert.Equal(new[] { "summary", "search" }, client.ReceivedRequests[2]
            .SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.CallId));
    }

    [Fact]
    public async Task RunAsync_WhenToolArgumentsAreCorrected_ContinuesAfterValidationError()
    {
        List<string> values = [];
        AIFunction function = AIFunctionFactory.Create((string value) =>
        {
            values.Add(value);
            return value;
        }, "Echo");
        var client = Client(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call("invalid", "Echo")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call("corrected", "Echo", "{\"value\":\"valid\"}")])),
            Reply("Done."));

        AgentRunResult result = await CreateRunner(client, [function]).RunAsync(Guid.NewGuid(), "Echo", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(new[] { "valid" }, values);
        Assert.Equal(2, result.ToolCallCount);
        Assert.Equal("INVALID_ARGUMENTS", Envelope(Assert.Single(ToolResults(client))).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RunAsync_WithReusedCallIdAcrossSteps_FailsBeforeSecondExecution()
    {
        int executions = 0;
        AIFunction function = AIFunctionFactory.Create(() => ++executions, "Count");
        var client = Client(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call("reused", "Count")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call("reused", "Count")])));

        AgentRunResult result = await CreateRunner(client, [function]).RunAsync(Guid.NewGuid(), "Count", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Null(result.Response);
        Assert.Equal(1, executions);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Equal(2, result.ModelStepCount);
    }

    [Fact]
    public async Task RunAsync_WithMultipleAssistantMessages_PreservesCompleteExchanges()
    {
        List<string> executions = [];
        AIFunction function = AIFunctionFactory.Create((string value) =>
        {
            executions.Add(value);
            return value == "fail"
                ? ToolResult<string>.Failure(new Error("QUERY_FAILED", "The query could not be completed."))
                : ToolResult<string>.Success(value);
        }, "Record");
        var introduction = new ChatMessage(ChatRole.Assistant, "Checking both requests.");
        var first = new ChatMessage(ChatRole.Assistant, [
            new TextContent("First batch."), Call("first", "Record", "{\"value\":\"one\"}"), Call("unknown", "Missing")]);
        var between = new ChatMessage(ChatRole.Assistant, "Checking the remaining requests.");
        var second = new ChatMessage(ChatRole.Assistant, [
            Call("invalid", "Record"), Call("failure", "Record", "{\"value\":\"fail\"}"),
            Call("second", "Record", "{\"value\":\"two\"}")]);
        var closing = new ChatMessage(ChatRole.Assistant, "Ready to summarize.");
        var final = new ChatResponse([
            new ChatMessage(ChatRole.Assistant, "First answer."),
            new ChatMessage(ChatRole.Assistant, "Second answer.")]);
        var client = Client(new ChatResponse([introduction, first, between, second, closing]), final);

        AgentRunResult result = await CreateRunner(client, [function], maxToolCalls: 6)
            .RunAsync(Guid.NewGuid(), "Record both", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Same(final, result.Response);
        Assert.Equal(new[] { "one", "fail", "two" }, executions);
        Assert.Equal(5, result.ToolCallCount);
        Assert.All(client.ReceivedRequests, AssertCompleteToolExchanges);
        IReadOnlyList<ChatMessage> messages = client.ReceivedRequests[1];
        Assert.Equal(9, messages.Count);
        Assert.Same(introduction, messages[2]);
        Assert.Same(first, messages[3]);
        Assert.Same(between, messages[5]);
        Assert.Same(second, messages[6]);
        Assert.Same(closing, messages[8]);
        FunctionResultContent[] firstResults = messages[4].Contents.OfType<FunctionResultContent>().ToArray();
        FunctionResultContent[] secondResults = messages[7].Contents.OfType<FunctionResultContent>().ToArray();
        Assert.Equal("one", Envelope(firstResults[0]).GetProperty("result").GetString());
        Assert.Equal("UNKNOWN_TOOL", Envelope(firstResults[1]).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("INVALID_ARGUMENTS", Envelope(secondResults[0]).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("QUERY_FAILED", Envelope(secondResults[1]).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("two", Envelope(secondResults[2]).GetProperty("result").GetString());
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("timeout")]
    [InlineData("budget")]
    [InlineData("ids")]
    public async Task RunAsync_WhenBatchIsIncomplete_DoesNotSendPartialHistoryOrCarryItToNextRun(string interruption)
    {
        using var cancellation = new CancellationTokenSource();
        List<int> executions = [];
        AIFunction function = AIFunctionFactory.Create(async (int index, CancellationToken cancellationToken) =>
        {
            executions.Add(index);
            if (index == 2)
            {
                if (interruption == "cancel")
                {
                    cancellation.Cancel();
                }
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return ToolResult<string>.Success($"result-{index}");
        }, "Record");
        var firstMessage = new ChatMessage(ChatRole.Assistant,
            [new TextContent("UNFINISHED"), Call("first", "Record", "{\"index\":1}")]);
        var secondMessage = new ChatMessage(ChatRole.Assistant, [
            Call(interruption == "ids" ? "first" : "second", "Record", "{\"index\":2}"),
            Call("third", "Record", "{\"index\":3}")]);
        var client = Client(new ChatResponse([firstMessage, secondMessage]), Reply("Fresh answer."));
        var runner = CreateRunner(client, [function], maxToolCalls: interruption == "budget" ? 1 : 4,
            timeout: interruption == "timeout" ? TimeSpan.FromSeconds(1) : null);
        Guid sessionId = Guid.NewGuid();

        AgentRunResult incomplete = await runner.RunAsync(sessionId, "First question", cancellation.Token);

        Assert.Equal(interruption switch
        {
            "cancel" => AgentRunStatus.Cancelled,
            "ids" => AgentRunStatus.Failed,
            _ => AgentRunStatus.BudgetExceeded
        }, incomplete.Status);
        Assert.Equal(interruption switch
        {
            "timeout" => AgentStopReason.DurationLimit,
            "budget" => AgentStopReason.ToolCallLimit,
            _ => (AgentStopReason?)null
        }, incomplete.StopReason);
        Assert.Null(incomplete.Response);
        Assert.Equal(1, incomplete.ModelStepCount);
        Assert.Equal(interruption switch { "budget" => 3, "ids" => 0, _ => 2 }, incomplete.ToolCallCount);
        Assert.Equal(interruption is "budget" or "ids" ? Array.Empty<int>() : new[] { 1, 2 }, executions);
        Assert.Single(client.ReceivedRequests);

        AgentRunResult fresh = await runner.RunAsync(sessionId, "Second question", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, fresh.Status);
        Assert.Equal("Fresh answer.", fresh.Response?.Text);
        Assert.Equal(sessionId, fresh.SessionId);
        Assert.NotEqual(incomplete.RunId, fresh.RunId);
        Assert.Equal(1, fresh.ModelStepCount);
        Assert.Equal(0, fresh.ToolCallCount);
        Assert.Equal(2, client.ReceivedRequests.Count);
        Assert.All(client.ReceivedRequests, messages =>
        {
            Assert.Equal(2, messages.Count);
            AssertCompleteToolExchanges(messages);
        });
        Assert.Equal("Second question", client.ReceivedRequests[1][1].Text);
        Assert.DoesNotContain(firstMessage, client.ReceivedRequests[1]);
        Assert.DoesNotContain(secondMessage, client.ReceivedRequests[1]);
    }

    private static void AssertCompleteToolExchanges(IReadOnlyList<ChatMessage> messages)
    {
        List<string> pendingCallIds = [];
        foreach (ChatMessage message in messages)
        {
            if (pendingCallIds.Count > 0)
            {
                Assert.Equal(ChatRole.Tool, message.Role);
            }
            if (message.Role == ChatRole.Tool)
            {
                Assert.NotEmpty(pendingCallIds);
                Assert.All(message.Contents, content => Assert.IsType<FunctionResultContent>(content));
                Assert.Equal(pendingCallIds, message.Contents.OfType<FunctionResultContent>().Select(result => result.CallId));
                pendingCallIds.Clear();
            }
            else
            {
                FunctionCallContent[] calls = message.Contents.OfType<FunctionCallContent>().ToArray();
                if (calls.Length > 0)
                {
                    Assert.Equal(ChatRole.Assistant, message.Role);
                    pendingCallIds.AddRange(calls.Select(call => call.CallId));
                }
            }
        }
        Assert.Empty(pendingCallIds);
    }

    private static AgentRunner CreateRunner(FakeChatClient client, IReadOnlyList<AIFunction>? functions = null,
        int maxToolCalls = 4, TimeSpan? timeout = null, int maxModelSteps = 6) =>
        new(client, new ToolDispatcher(functions ?? []), "Use tools.",
            new AgentRunOptions(MaxModelSteps: maxModelSteps, MaxToolCalls: maxToolCalls, MaxDuration: timeout));

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