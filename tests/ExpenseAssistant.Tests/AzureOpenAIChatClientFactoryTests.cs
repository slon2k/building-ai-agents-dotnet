using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.ClientModel.Primitives;
using ExpenseAssistant.App;
using ExpenseAssistant.Infrastructure;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Tests;

public class AzureOpenAIChatClientFactoryTests
{
    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task RunAsync_WithTransientProviderFailure_RetriesWithinOneModelStep(int status)
    {
        int responses = 0;
        using var handler = new StubHandler((_, _) => Task.FromResult(
            Reply(++responses == 1 ? status : 200)));
        using var httpClient = new HttpClient(handler);
        using IChatClient client = CreateClient(httpClient);
        var runner = CreateRunner(client);

        AgentRunResult result = await runner.RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("Done.", result.Response?.Text);
        Assert.Equal(2, handler.Attempts);
        Assert.Equal(1, result.ModelStepCount);
        Assert.Equal(0, result.ToolCallCount);
        Assert.Equal(5, result.Usage?.TotalTokenCount);
    }

    [Fact]
    public async Task RunAsync_WhenProviderRetriesAreExhausted_FailsWithoutAnApplicationRetry()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Reply(503)));
        using var httpClient = new HttpClient(handler);
        using IChatClient client = CreateClient(httpClient);

        AgentRunResult result = await CreateRunner(client).RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal(AzureOpenAIChatClientFactory.MaxProviderRetries + 1, handler.Attempts);
        Assert.Equal(1, result.ModelStepCount);
        Assert.Equal(0, result.ToolCallCount);
        Assert.Null(result.Response);
        Assert.Null(result.Usage);
        Assert.Equal("The assistant request could not be completed.", result.Error);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task RunAsync_WithNonRetryableProviderFailure_FailsAfterOneHttpAttempt(int status)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Reply(status)));
        using var httpClient = new HttpClient(handler);
        using IChatClient client = CreateClient(httpClient);

        AgentRunResult result = await CreateRunner(client).RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal(1, handler.Attempts);
        Assert.Equal(1, result.ModelStepCount);
        Assert.Null(result.Response);
        Assert.Null(result.Usage);
        Assert.DoesNotContain("PRIVATE_", result.Error!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_WhenInterruptedDuringProviderBackoff_DoesNotSendAnotherAttempt(bool timeout)
    {
        Guid sessionId = Guid.NewGuid();
        var spans = new ConcurrentQueue<Activity>();
        using ActivityListener listener = Listen(sessionId, spans);
        using var cancellation = new CancellationTokenSource();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler((_, _) =>
        {
            received.TrySetResult();
            return Task.FromResult(Reply(503, retryAfterSeconds: 60));
        });
        using var httpClient = new HttpClient(handler);
        using IChatClient client = CreateClient(httpClient);
        var runner = CreateRunner(client, duration: timeout ? TimeSpan.FromSeconds(1) : null);

        Task<AgentRunResult> run = runner.RunAsync(sessionId, "Expenses?", cancellation.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!timeout)
        {
            cancellation.Cancel();
        }
        AgentRunResult result = await run;

        Assert.Equal(timeout ? AgentRunStatus.BudgetExceeded : AgentRunStatus.Cancelled, result.Status);
        Assert.Equal(timeout ? AgentStopReason.DurationLimit : (AgentStopReason?)null, result.StopReason);
        Assert.Equal(1, handler.Attempts);
        Assert.Equal(1, result.ModelStepCount);
        Assert.Null(result.Response);
        Assert.Null(result.Usage);
        Activity model = Assert.Single(spans, span => span.OperationName == "model.step");
        Assert.Equal(0, model.GetTagItem("provider.retry.count"));
        Assert.Single(spans, span => span.OperationName == "provider.attempt");
    }

    [Fact]
    public async Task RunAsync_WhenProviderRetriesAfterTool_DoesNotReplayCompletedTool()
    {
        Guid sessionId = Guid.NewGuid();
        var spans = new ConcurrentQueue<Activity>();
        using ActivityListener listener = Listen(sessionId, spans);
        int responses = 0;
        List<string> requestBodies = [];
        using var handler = new StubHandler(async (request, token) =>
        {
            requestBodies.Add(await request.Content!.ReadAsStringAsync(token));
            return ++responses switch
            {
                1 => Reply(200, ToolResponse),
                2 => Reply(503),
                _ => Reply(200)
            };
        });
        using var httpClient = new HttpClient(handler);
        using IChatClient client = CreateClient(httpClient);
        int executions = 0;
        AIFunction function = AIFunctionFactory.Create(() =>
        {
            executions++;
            return ToolResult<string>.Success("PRIVATE_TOOL_RESULT");
        }, "Lookup");

        AgentRunResult result = await CreateRunner(client, [function])
            .RunAsync(sessionId, "Expenses?", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(3, handler.Attempts);
        Assert.Equal(2, result.ModelStepCount);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Equal(1, executions);
        Assert.Equal(10, result.Usage?.TotalTokenCount);
        Assert.Equal(requestBodies[1], requestBodies[2]);
        Activity[] models = spans.Where(span => span.OperationName == "model.step").ToArray();
        Assert.Equal(new[] { 0, 1 }, models.Select(span => (int)span.GetTagItem("provider.retry.count")!));
        Activity[] attempts = spans.Where(span => span.OperationName == "provider.attempt").ToArray();
        Assert.Equal(new[] { 1, 2, 2 }, attempts.Select(span => (int)span.GetTagItem("model.step")!));
        Assert.Equal(new[] { 0, 0, 1 }, attempts.Select(span => (int)span.GetTagItem("retry.count")!));
    }

    [Fact]
    public async Task RunAsync_WhenToolIsUnavailable_SendsSafeFailureAndContinues()
    {
        int responses = 0;
        string? followupBody = null;
        using var handler = new StubHandler(async (request, token) =>
        {
            if (++responses == 1)
            {
                return Reply(200, ToolResponse);
            }
            followupBody = await request.Content!.ReadAsStringAsync(token);
            return Reply(200);
        });
        using var httpClient = new HttpClient(handler);
        using IChatClient client = CreateClient(httpClient);
        AIFunction function = AIFunctionFactory.Create(UnavailableTool, "Lookup");

        AgentRunResult result = await CreateRunner(client, [function])
            .RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(2, handler.Attempts);
        Assert.Equal(2, result.ModelStepCount);
        Assert.Equal(1, result.ToolCallCount);
        using JsonDocument body = JsonDocument.Parse(followupBody!);
        JsonElement toolMessage = Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray(),
            message => message.GetProperty("role").GetString() == "tool");
        using JsonDocument envelope = JsonDocument.Parse(toolMessage.GetProperty("content").GetString()!);
        Assert.False(envelope.RootElement.GetProperty("isSuccess").GetBoolean());
        Assert.Equal("TOOL_FAILED", envelope.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(envelope.RootElement.TryGetProperty("result", out JsonElement value) && value.ValueKind != JsonValueKind.Null);
        Assert.DoesNotContain("PRIVATE_", followupBody!);
    }

    [Theory]
    [InlineData(200, true, 1, 0)]
    [InlineData(503, true, 2, 1)]
    [InlineData(503, false, 4, 3)]
    [InlineData(401, false, 1, 0)]
    public async Task RunAsync_RecordsActualProviderAttemptsWithoutPayloads(
        int status, bool recover, int expectedAttempts, int expectedRetries)
    {
        Guid sessionId = Guid.NewGuid();
        var spans = new ConcurrentQueue<Activity>();
        using ActivityListener listener = Listen(sessionId, spans);
        int responses = 0;
        using var handler = new StubHandler((_, _) => Task.FromResult(Reply(recover && ++responses > 1 ? 200 : status)));
        using var httpClient = new HttpClient(handler);
        using IChatClient client = CreateClient(httpClient);

        AgentRunResult result = await CreateRunner(client).RunAsync(sessionId, "PRIVATE_USER_TEXT", CancellationToken.None);

        Assert.Equal(recover ? AgentRunStatus.Completed : AgentRunStatus.Failed, result.Status);
        Assert.Equal(expectedAttempts, handler.Attempts);
        Activity model = Assert.Single(spans, span => span.OperationName == "model.step");
        Assert.Equal(expectedRetries, model.GetTagItem("provider.retry.count"));
        Activity[] attempts = spans.Where(span => span.OperationName == "provider.attempt").ToArray();
        Assert.Equal(expectedAttempts, attempts.Length);
        Assert.Equal(Enumerable.Range(0, expectedAttempts), attempts.Select(span => (int)span.GetTagItem("retry.count")!));
        Assert.All(attempts, span =>
        {
            Assert.Equal(model.SpanId, span.ParentSpanId);
            Assert.Equal(model.TraceId, span.TraceId);
            Assert.Equal(result.RunId.ToString(), span.GetTagItem("run.id"));
            Assert.Equal(1, span.GetTagItem("model.step"));
            Assert.IsType<double>(span.GetTagItem("duration.ms"));
            int httpStatus = (int)span.GetTagItem("http.response.status_code")!;
            Assert.Equal(httpStatus >= 400 ? ActivityStatusCode.Error : ActivityStatusCode.Ok, span.Status);
        });
        string telemetry = string.Join('\n', spans.SelectMany(span => span.TagObjects).Select(tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain("PRIVATE_", telemetry);
        Assert.DoesNotContain("expense-assistant.invalid", telemetry);
        Assert.DoesNotContain("test-only-key", telemetry);
    }

    private static ActivityListener Listen(Guid sessionId, ConcurrentQueue<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentRunner.ActivitySourceName
                || source.Name == AzureOpenAIChatClientFactory.ActivitySourceName,
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

    private static string UnavailableTool() => throw new InvalidOperationException("PRIVATE_TOOL_EXCEPTION");

    private static IChatClient CreateClient(HttpClient httpClient) => AzureOpenAIChatClientFactory.Create(
        new AzureOpenAIOptions
        {
            Endpoint = "https://expense-assistant.invalid",
            Deployment = "fake-model",
            ApiKey = "test-only-key"
        }, new HttpClientPipelineTransport(httpClient));

    private static AgentRunner CreateRunner(IChatClient client, IReadOnlyList<AIFunction>? functions = null, TimeSpan? duration = null) =>
        new(client, new ToolDispatcher(functions ?? []), "Use tools.", new AgentRunOptions(MaxDuration: duration));

    private static HttpResponseMessage Reply(int status, string? content = null, int retryAfterSeconds = 0)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(content ?? (status == 200 ? SuccessResponse : ErrorResponse), Encoding.UTF8, "application/json")
        };
        if (status != 200)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return response;
    }

    private const string SuccessResponse = """
        { "id": "chat-success", "object": "chat.completion", "created": 0, "model": "fake-model",
          "choices": [{ "index": 0, "message": { "role": "assistant", "content": "Done." }, "finish_reason": "stop" }],
          "usage": { "prompt_tokens": 3, "completion_tokens": 2, "total_tokens": 5 } }
        """;

    private const string ToolResponse = """
        { "id": "chat-tool", "object": "chat.completion", "created": 0, "model": "fake-model",
          "choices": [{ "index": 0, "message": { "role": "assistant", "content": null,
            "tool_calls": [{ "id": "lookup-call", "type": "function", "function": { "name": "Lookup", "arguments": "{}" } }] },
            "finish_reason": "tool_calls" }],
          "usage": { "prompt_tokens": 3, "completion_tokens": 2, "total_tokens": 5 } }
        """;

    private const string ErrorResponse = """
        { "error": { "message": "PRIVATE_PROVIDER_DETAILS", "type": "provider_error", "code": "provider_error" } }
        """;

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return handler(request, cancellationToken);
        }
    }
}