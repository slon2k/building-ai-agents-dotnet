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

        Task<AgentRunResult> run = runner.RunAsync(Guid.NewGuid(), "Expenses?", cancellation.Token);
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
    }

    [Fact]
    public async Task RunAsync_WhenProviderRetriesAfterTool_DoesNotReplayCompletedTool()
    {
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
            .RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(3, handler.Attempts);
        Assert.Equal(2, result.ModelStepCount);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Equal(1, executions);
        Assert.Equal(10, result.Usage?.TotalTokenCount);
        Assert.Equal(requestBodies[1], requestBodies[2]);
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