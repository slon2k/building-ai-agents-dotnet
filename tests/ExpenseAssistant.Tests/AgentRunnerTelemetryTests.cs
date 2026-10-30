using System.Collections.Concurrent;
using System.Diagnostics;
using ExpenseAssistant.App;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ExpenseAssistant.Tests;

public class AgentRunnerTelemetryTests
{
    [Theory]
    [InlineData("success", null)]
    [InlineData("unknown", "UNKNOWN_TOOL")]
    [InlineData("failure", "QUERY_FAILED")]
    public async Task RunAsync_RecordsCorrelatedSpansAndSafeLogs(string scenario, string? errorCode)
    {
        Guid sessionId = Guid.NewGuid();
        var spans = new ConcurrentQueue<Activity>();
        using ActivityListener listener = Listen(sessionId, spans);
        var logger = new RecordingLogger();
        AIFunction function = AIFunctionFactory.Create((string value) => scenario == "failure"
            ? ToolResult<string>.Failure(new Error("QUERY_FAILED", "PRIVATE_ERROR_MESSAGE"))
            : ToolResult<string>.Success("PRIVATE_TOOL_RESULT"), "Summary");
        string name = scenario == "unknown" ? "PRIVATE_TOOL_NAME" : "Summary";
        List<AIContent> calls = [new FunctionCallContent("call-1", name,
            new Dictionary<string, object?> { ["value"] = "PRIVATE_ARGUMENT" })];
        Queue<ChatResponse> responses = new([
            new ChatResponse(new ChatMessage(ChatRole.Assistant, calls))
            {
                Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 3, TotalTokenCount = 13 }
            },
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "PRIVATE_FINAL_TEXT"))
            {
                Usage = new UsageDetails { InputTokenCount = 20, OutputTokenCount = 5, TotalTokenCount = 25 }
            }
        ]);
        var client = new FakeChatClient((_, _) => Task.FromResult(responses.Dequeue()));
        var runner = new AgentRunner(client, new ToolDispatcher([function]),
            "PRIVATE_SYSTEM_PROMPT", new AgentRunOptions(MaxToolCalls: 3), logger);

        AgentRunResult result = await runner.RunAsync(sessionId, "PRIVATE_USER_TEXT", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(4, spans.Count);
        Activity root = Assert.Single(spans, span => span.OperationName == "assistant.run");
        Assert.Equal(ActivityStatusCode.Ok, root.Status);
        Assert.Equal("Completed", root.GetTagItem("outcome"));
        Assert.Equal(2, root.GetTagItem("model.steps"));
        Assert.Equal(1, root.GetTagItem("tool.calls"));
        Assert.Equal(38L, root.GetTagItem("gen_ai.usage.total_tokens"));
        Assert.All(spans, span =>
        {
            Assert.Equal(sessionId.ToString(), span.GetTagItem("session.id"));
            Assert.Equal(result.RunId.ToString(), span.GetTagItem("run.id"));
            Assert.IsType<double>(span.GetTagItem("duration.ms"));
            if (span != root)
            {
                Assert.Equal(root.SpanId, span.ParentSpanId);
                Assert.Equal(root.TraceId, span.TraceId);
            }
        });
        Activity[] models = spans.Where(span => span.OperationName == "model.step").ToArray();
        Assert.Equal(2, models.Length);
        Assert.All(models, span => Assert.Equal(ActivityKind.Client, span.Kind));
        Assert.Equal(new[] { 1, 2 }, models.Select(span => (int)span.GetTagItem("model.step")!));
        Activity[] tools = spans.Where(span => span.OperationName == "tool.execute").ToArray();
        Assert.All(tools, span => Assert.Equal(scenario == "unknown" ? "unrecognized" : "Summary", span.GetTagItem("tool.name")));
        if (errorCode is not null)
        {
            Activity failed = Assert.Single(tools, span => Equals(span.GetTagItem("error.code"), errorCode));
            Assert.Equal(ActivityStatusCode.Error, failed.Status);
            Assert.Contains(logger.Properties, property => property.Key == "ErrorCode" && Equals(property.Value, errorCode));
        }
        else
        {
            Assert.All(tools, span => Assert.Equal(ActivityStatusCode.Ok, span.Status));
        }
        Assert.Contains(logger.Scopes, scope => scope.Contains(sessionId.ToString()) && scope.Contains(result.RunId.ToString()));
        Assert.Contains(logger.Scopes, scope => scope.Contains("call-1"));
        Assert.Contains(logger.Properties, property => property.Key == "TotalTokens" && Equals(property.Value, 38L));
        AssertPrivatePayloadsAbsent(logger, spans);
    }

    [Theory]
    [InlineData("provider", AgentRunStatus.Failed)]
    [InlineData("cancel", AgentRunStatus.Cancelled)]
    [InlineData("timeout", AgentRunStatus.BudgetExceeded)]
    public async Task RunAsync_RecordsInterruptedModelWithoutExceptionDetails(string scenario, AgentRunStatus expectedStatus)
    {
        Guid sessionId = Guid.NewGuid();
        var spans = new ConcurrentQueue<Activity>();
        using ActivityListener listener = Listen(sessionId, spans);
        var logger = new RecordingLogger();
        using var cancellation = new CancellationTokenSource();
        var client = new FakeChatClient(async (_, token) =>
        {
            if (scenario == "cancel")
            {
                cancellation.Cancel();
            }
            if (scenario != "provider")
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            throw new InvalidOperationException("PRIVATE_PROVIDER_EXCEPTION");
        });
        var runner = new AgentRunner(client, new ToolDispatcher([]), "PRIVATE_SYSTEM_PROMPT",
            new AgentRunOptions(MaxDuration: scenario == "timeout" ? TimeSpan.FromMilliseconds(100) : null), logger);

        AgentRunResult result = await runner.RunAsync(sessionId, "PRIVATE_USER_TEXT", cancellation.Token);

        Assert.Equal(expectedStatus, result.Status);
        Activity root = Assert.Single(spans, span => span.OperationName == "assistant.run");
        Assert.Equal(scenario == "timeout" ? "DurationLimit" : null, root.GetTagItem("stop.reason"));
        Assert.Equal(2, spans.Count);
        Assert.All(spans, span =>
        {
            Assert.Equal(ActivityStatusCode.Error, span.Status);
            Assert.Equal(expectedStatus.ToString(), span.GetTagItem("error.code"));
            Assert.IsType<double>(span.GetTagItem("duration.ms"));
        });
        Assert.Contains(logger.Properties, property => property.Key == "ErrorCode" && Equals(property.Value, expectedStatus.ToString()));
        AssertPrivatePayloadsAbsent(logger, spans);
    }

    [Theory]
    [InlineData("batch", 2, 0)]
    [InlineData("steps", 1, 1)]
    [InlineData("tools", 1, 1)]
    public async Task RunAsync_WhenBudgetStopsRun_RecordsReasonWithoutUnexecutedToolSpans(
        string scenario, int attemptedCalls, int executedCalls)
    {
        Guid sessionId = Guid.NewGuid();
        var spans = new ConcurrentQueue<Activity>();
        using ActivityListener listener = Listen(sessionId, spans);
        var logger = new RecordingLogger();
        int executions = 0;
        AIFunction function = AIFunctionFactory.Create(() =>
        {
            executions++;
            return "PRIVATE_TOOL_RESULT";
        }, "Summary");
        List<AIContent> calls = [new FunctionCallContent("call-1", "Summary", new Dictionary<string, object?>())];
        if (scenario == "batch")
        {
            calls.Add(new FunctionCallContent("call-2", "PRIVATE_TOOL_NAME",
                new Dictionary<string, object?> { ["value"] = "PRIVATE_ARGUMENT" }));
        }
        var client = new FakeChatClient((_, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, calls))
        {
            Usage = new UsageDetails { TotalTokenCount = 13 }
        }));
        var runner = new AgentRunner(client, new ToolDispatcher([function]), "PRIVATE_SYSTEM_PROMPT",
            new AgentRunOptions(MaxModelSteps: scenario == "steps" ? 1 : 6, MaxToolCalls: scenario == "steps" ? 3 : 1), logger);

        AgentRunResult result = await runner.RunAsync(sessionId, "PRIVATE_USER_TEXT", CancellationToken.None);

        Assert.Equal(AgentRunStatus.BudgetExceeded, result.Status);
        Assert.Equal(scenario == "steps" ? AgentStopReason.ModelStepLimit : AgentStopReason.ToolCallLimit, result.StopReason);
        Assert.Null(result.Response);
        Assert.Single(client.ReceivedRequests);
        Assert.Equal(executedCalls, executions);
        Assert.Equal(attemptedCalls, result.ToolCallCount);
        Assert.Equal(executedCalls, spans.Count(span => span.OperationName == "tool.execute"));
        if (scenario == "batch")
        {
            Assert.Contains(logger.Properties, property => property.Key == "ToolCallId" && Equals(property.Value, "call-1"));
            Assert.Contains(logger.Properties, property => property.Key == "ToolCallId" && Equals(property.Value, "call-2"));
            Assert.Contains(logger.Properties, property => property.Key == "ToolName" && Equals(property.Value, "unrecognized"));
        }
        Activity root = Assert.Single(spans, span => span.OperationName == "assistant.run");
        Assert.Equal(ActivityStatusCode.Error, root.Status);
        Assert.Equal("BudgetExceeded", root.GetTagItem("error.code"));
        Assert.Equal(result.StopReason.ToString(), root.GetTagItem("stop.reason"));
        Assert.Equal(attemptedCalls, root.GetTagItem("tool.calls"));
        Assert.Equal(13L, root.GetTagItem("gen_ai.usage.total_tokens"));
        AssertPrivatePayloadsAbsent(logger, spans);
    }

    private static ActivityListener Listen(Guid sessionId, ConcurrentQueue<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentRunner.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (Equals(activity.GetTagItem("session.id"), sessionId.ToString()))
                {
                    spans.Enqueue(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static void AssertPrivatePayloadsAbsent(RecordingLogger logger, IEnumerable<Activity> spans)
    {
        string telemetry = string.Join('\n', logger.Messages.Concat(logger.Scopes)
            .Concat(logger.Properties.Select(property => $"{property.Key}={property.Value}"))
            .Concat(spans.SelectMany(span => span.TagObjects).Select(tag => $"{tag.Key}={tag.Value}")));
        Assert.DoesNotContain("PRIVATE_", telemetry);
    }

    private sealed class RecordingLogger : ILogger<AgentRunner>
    {
        public List<string> Messages { get; } = [];
        public List<string> Scopes { get; } = [];
        public List<KeyValuePair<string, object?>> Properties { get; } = [];

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Scopes.Add(state.ToString()!);
            return null;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Messages.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                Properties.AddRange(properties);
            }
        }
    }
}