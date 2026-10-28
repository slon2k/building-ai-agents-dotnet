using System.Text.Json;
using ExpenseAssistant.App;
using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ExpenseAssistant.Tests;

public class ToolChatRunnerTests
{
    [Fact]
    public async Task RunAsync_WithToolCall_PreservesExchangeAndDisablesToolsForFinalRequest()
    {
        await using var context = await CreateContextAsync();
        var call = new FunctionCallContent("call-1", "GetSpendingSummary", Arguments("""
            { "startDate": "2026-03-01", "endDate": "2026-04-01", "categoryId": "groceries" }
            """));
        var toolMessage = new ChatMessage(ChatRole.Assistant, [call]);
        Queue<ChatResponse> responses = new([
            new ChatResponse(toolMessage),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "You spent EUR 21.25."))
        ]);
        var client = new FakeChatClient((_, _) => Task.FromResult(responses.Dequeue()));
        var runner = CreateRunner(client, context);
        Guid sessionId = Guid.NewGuid();

        ChatRunResult result = await runner.RunAsync(sessionId, "March groceries?", CancellationToken.None);

        Assert.Equal(ChatRunStatus.Completed, result.Status);
        Assert.Equal(sessionId, result.SessionId);
        Assert.NotEqual(Guid.Empty, result.RunId);
        Assert.Equal("You spent EUR 21.25.", result.Response?.Text);
        Assert.Equal(2, client.ReceivedRequests.Count);
        Assert.Equal(new[] { "SearchTransactions", "GetSpendingSummary" }, client.ReceivedOptions[0]!.Tools!.Select(tool => tool.Name));
        Assert.Equal(ChatToolMode.None, client.ReceivedOptions[1]!.ToolMode);
        Assert.True(client.ReceivedOptions[1]!.Tools is null or { Count: 0 });
        IReadOnlyList<ChatMessage> finalMessages = client.ReceivedRequests[1];
        Assert.Equal(4, finalMessages.Count);
        Assert.Equal(ChatRole.System, finalMessages[0].Role);
        Assert.Equal(ChatRole.User, finalMessages[1].Role);
        Assert.Same(toolMessage, finalMessages[2]);
        Assert.Equal(ChatRole.Tool, finalMessages[3].Role);
        FunctionResultContent toolResult = Assert.IsType<FunctionResultContent>(Assert.Single(finalMessages[3].Contents));
        Assert.Equal("call-1", toolResult.CallId);
        JsonElement envelope = JsonSerializer.SerializeToElement(toolResult.Result, JsonSerializerOptions.Web);
        Assert.True(envelope.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(21.25m, envelope.GetProperty("result").GetProperty("totalSpending").GetDecimal());
        Assert.Equal(2, envelope.GetProperty("result").GetProperty("transactionCount").GetInt32());
    }

    [Fact]
    public async Task RunAsync_WithoutToolCall_ReturnsInitialResponse()
    {
        await using var context = await CreateContextAsync();
        var client = new FakeChatClient((_, _) => Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Which date range?"))));

        ChatRunResult result = await CreateRunner(client, context).RunAsync(Guid.NewGuid(), "Total spending?", CancellationToken.None);

        Assert.Equal(ChatRunStatus.Completed, result.Status);
        Assert.Equal("Which date range?", result.Response?.Text);
        Assert.Single(client.ReceivedRequests);
    }

    [Theory]
    [InlineData("DeleteTransaction", "{}", "UNKNOWN_TOOL")]
    [InlineData("GetSpendingSummary", "{}", "INVALID_ARGUMENTS")]
    [InlineData("GetSpendingSummary", "{\"endDate\":\"2026-04-01\"}", "INVALID_ARGUMENTS")]
    [InlineData("GetSpendingSummary", "{\"startDate\":\"2026-03-01\"}", "INVALID_ARGUMENTS")]
    [InlineData("GetSpendingSummary", "{\"startDate\":null,\"endDate\":\"2026-04-01\"}", "INVALID_ARGUMENTS")]
    [InlineData("GetSpendingSummary", "{\"startDate\":\"2026-02-30\",\"endDate\":\"2026-04-01\"}", "INVALID_ARGUMENTS")]
    [InlineData("SearchTransactions", "{\"startDate\":\"2026-03-01\",\"endDate\":\"2026-04-01\",\"limit\":\"abc\"}", "INVALID_ARGUMENTS")]
    [InlineData("SearchTransactions", "{\"startDate\":\"2026-03-01\",\"endDate\":\"2026-04-01\",\"limit\":2147483648}", "INVALID_ARGUMENTS")]
    [InlineData("GetSpendingSummary", "{\"startDate\":\"2026-03-01\",\"endDate\":\"2026-04-01\",\"categoryId\":123}", "INVALID_ARGUMENTS")]
    [InlineData("GetSpendingSummary", "{\"startDate\":\"2026-03-01\",\"endDate\":\"2026-04-01\",\"cancellationToken\":null}", "INVALID_ARGUMENTS")]
    [InlineData("GetSpendingSummary", "{\"startDate\":\"2026-04-01\",\"endDate\":\"2026-03-01\"}", "INVALID_DATE_RANGE")]
    [InlineData("GetSpendingSummary", "{\"startDate\":\"2026-03-01\",\"endDate\":\"2026-04-01\",\"categoryId\":\"unknown\"}", "INVALID_CATEGORY")]
    public async Task RunAsync_WithInvalidToolRequest_ReturnsSafeErrorWithOriginalCallId(
        string toolName, string json, string expectedCode)
    {
        await using var context = await CreateContextAsync();
        var call = new FunctionCallContent("invalid-call", toolName, Arguments(json));
        var client = ReplyingToCall(call);

        ChatRunResult result = await CreateRunner(client, context).RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(ChatRunStatus.Completed, result.Status);
        FunctionResultContent toolResult = GetToolResults(client).Single();
        Assert.Equal("invalid-call", toolResult.CallId);
        AssertFailure(toolResult, expectedCode);
    }

    [Fact]
    public async Task RunAsync_WhenCallCapIsExceeded_DoesNotExecuteExcessCallsButRespondsToEveryId()
    {
        var queries = new RecordingQueries();
        var first = new FunctionCallContent("first", "GetSpendingSummary", ValidArguments());
        var second = new FunctionCallContent("second", "GetSpendingSummary", ValidArguments());
        var third = new FunctionCallContent("third", "SearchTransactions", ValidArguments());
        var client = ReplyingToCall(first, second, third);
        var runner = new ToolChatRunner(client, new ExpenseTools(queries), TimeSpan.FromSeconds(30), "Use tools.", maxToolCalls: 1);

        ChatRunResult result = await runner.RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(ChatRunStatus.Completed, result.Status);
        Assert.Equal(1, queries.CallCount);
        FunctionResultContent[] toolResults = GetToolResults(client);
        Assert.Equal(new[] { "first", "second", "third" }, toolResults.Select(toolResult => toolResult.CallId));
        AssertFailure(toolResults[1], "TOOL_CALL_LIMIT");
        AssertFailure(toolResults[2], "TOOL_CALL_LIMIT");
        Assert.Equal(2, client.ReceivedRequests.Count);
    }

    [Fact]
    public async Task RunAsync_WithMultipleTools_ExecutesBothAndRetainsCallIds()
    {
        var queries = new RecordingQueries();
        var client = ReplyingToCall(
            new FunctionCallContent("summary", "GetSpendingSummary", ValidArguments()),
            new FunctionCallContent("search", "SearchTransactions", ValidArguments()));
        var runner = new ToolChatRunner(client, new ExpenseTools(queries), TimeSpan.FromSeconds(30), "Use tools.");

        ChatRunResult result = await runner.RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(ChatRunStatus.Completed, result.Status);
        Assert.Equal(2, queries.CallCount);
        Assert.Equal(new[] { "summary", "search" }, GetToolResults(client).Select(toolResult => toolResult.CallId));
    }

    [Fact]
    public async Task RunAsync_WhenFinalResponseRequestsAnotherTool_FailsWithoutExecutingIt()
    {
        var queries = new RecordingQueries();
        var initialCall = new FunctionCallContent("initial", "GetSpendingSummary", ValidArguments());
        var extraCall = new FunctionCallContent("extra", "SearchTransactions", ValidArguments());
        Queue<ChatResponse> responses = new([
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [initialCall])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [extraCall]))
        ]);
        var client = new FakeChatClient((_, _) => Task.FromResult(responses.Dequeue()));
        var runner = new ToolChatRunner(client, new ExpenseTools(queries), TimeSpan.FromSeconds(30), "Use tools.");

        ChatRunResult result = await runner.RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(ChatRunStatus.Failed, result.Status);
        Assert.Null(result.Response);
        Assert.Equal(1, queries.CallCount);
        Assert.Equal(2, client.ReceivedRequests.Count);
    }

    [Fact]
    public async Task RunAsync_WhenDatabaseFails_SendsFailureInsteadOfInventedTotal()
    {
        await using var context = await CreateContextAsync();
        await context.Database.ExecuteSqlRawAsync("DROP TABLE Transactions");
        var client = ReplyingToCall(new FunctionCallContent("failed-query", "GetSpendingSummary", ValidArguments()));

        ChatRunResult result = await CreateRunner(client, context).RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(ChatRunStatus.Completed, result.Status);
        AssertFailure(GetToolResults(client).Single(), "QUERY_FAILED");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RunAsync_WhenModelRequestIsCancelled_ReturnsCancelled(int requestIndex)
    {
        await using var context = await CreateContextAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        var client = new FakeChatClient(async (_, cancellationToken) =>
        {
            if (requests++ == requestIndex)
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("summary", "GetSpendingSummary", ValidArguments())]));
        });
        using var cancellation = new CancellationTokenSource();

        Task<ChatRunResult> run = CreateRunner(client, context).RunAsync(Guid.NewGuid(), "Expenses?", cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        ChatRunResult result = await run;

        Assert.Equal(ChatRunStatus.Cancelled, result.Status);
        Assert.Null(result.Response);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RunAsync_WhenModelRequestTimesOut_ReturnsTimedOut(int requestIndex)
    {
        await using var context = await CreateContextAsync();
        int requests = 0;
        var client = new FakeChatClient(async (_, cancellationToken) =>
        {
            if (requests++ == requestIndex)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("summary", "GetSpendingSummary", ValidArguments())]));
        });
        var runner = new ToolChatRunner(client, new ExpenseTools(new TransactionQueries(context)),
            TimeSpan.FromMilliseconds(100), "Use tools.");

        ChatRunResult result = await runner.RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(ChatRunStatus.TimedOut, result.Status);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task RunAsync_WhenModelFails_DoesNotExposeExceptionDetails()
    {
        await using var context = await CreateContextAsync();
        var client = new FakeChatClient((_, _) => throw new InvalidOperationException("private provider details"));

        ChatRunResult result = await CreateRunner(client, context).RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(ChatRunStatus.Failed, result.Status);
        Assert.Equal("The assistant request could not be completed.", result.Error);
    }

    [Fact]
    public async Task RunAsync_WithDuplicateCallIds_FailsBeforeExecutingTools()
    {
        var queries = new RecordingQueries();
        var client = ReplyingToCall(
            new FunctionCallContent("duplicate", "GetSpendingSummary", ValidArguments()),
            new FunctionCallContent("duplicate", "SearchTransactions", ValidArguments()));
        var runner = new ToolChatRunner(client, new ExpenseTools(queries), TimeSpan.FromSeconds(30), "Use tools.");

        ChatRunResult result = await runner.RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(ChatRunStatus.Failed, result.Status);
        Assert.Equal(0, queries.CallCount);
        Assert.Single(client.ReceivedRequests);
    }

    [Fact]
    public async Task ScopedRunners_UseDistinctContextsAndRunsButRetainConsoleSessionId()
    {
        var client = new FakeChatClient((_, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello."))));
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(client);
        services.AddDbContext<ExpenseDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        services.AddScoped<ITransactionQueries, TransactionQueries>();
        services.AddScoped<ExpenseTools>();
        services.AddScoped(provider => new ToolChatRunner(provider.GetRequiredService<IChatClient>(),
            provider.GetRequiredService<ExpenseTools>(), TimeSpan.FromSeconds(30), "Use tools."));
        services.AddSingleton<ConsoleChat>();
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        Guid sessionId = provider.GetRequiredService<ConsoleChat>().SessionId;
        ExpenseDbContext firstContext;
        ToolChatRunner firstRunner;
        ChatRunResult firstResult;

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            firstContext = scope.ServiceProvider.GetRequiredService<ExpenseDbContext>();
            firstRunner = scope.ServiceProvider.GetRequiredService<ToolChatRunner>();
            firstResult = await firstRunner.RunAsync(sessionId, "Hello", CancellationToken.None);
        }

        Assert.Throws<ObjectDisposedException>(() => firstContext.Categories.ToList());
        await using AsyncServiceScope secondScope = provider.CreateAsyncScope();
        ExpenseDbContext secondContext = secondScope.ServiceProvider.GetRequiredService<ExpenseDbContext>();
        ToolChatRunner secondRunner = secondScope.ServiceProvider.GetRequiredService<ToolChatRunner>();
        ChatRunResult secondResult = await secondRunner.RunAsync(sessionId, "Hello again", CancellationToken.None);

        Assert.NotSame(firstContext, secondContext);
        Assert.NotSame(firstRunner, secondRunner);
        Assert.Equal(ChatRunStatus.Completed, firstResult.Status);
        Assert.Equal(ChatRunStatus.Completed, secondResult.Status);
        Assert.Equal(sessionId, firstResult.SessionId);
        Assert.Equal(sessionId, secondResult.SessionId);
        Assert.NotEqual(firstResult.RunId, secondResult.RunId);
    }

    private static FakeChatClient ReplyingToCall(params FunctionCallContent[] calls)
    {
        Queue<ChatResponse> responses = new([
            new ChatResponse(new ChatMessage(ChatRole.Assistant, calls.Cast<AIContent>().ToList())),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Final answer based on tool results."))
        ]);
        return new FakeChatClient((_, _) => Task.FromResult(responses.Dequeue()));
    }

    private static FunctionResultContent[] GetToolResults(FakeChatClient client) =>
        client.ReceivedRequests[1].Last().Contents.OfType<FunctionResultContent>().ToArray();

    private static void AssertFailure(FunctionResultContent toolResult, string expectedCode)
    {
        JsonElement envelope = JsonSerializer.SerializeToElement(toolResult.Result, JsonSerializerOptions.Web);
        Assert.False(envelope.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(expectedCode, envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.True(!envelope.TryGetProperty("result", out JsonElement value) || value.ValueKind == JsonValueKind.Null);
    }

    private static Dictionary<string, object?> ValidArguments() => Arguments("""
        { "startDate": "2026-03-01", "endDate": "2026-04-01" }
        """);

    private sealed class RecordingQueries : ITransactionQueries
    {
        public int CallCount { get; private set; }

        public Task<TransactionSearchResult> SearchTransactionsAsync(
            DateOnly startDate, DateOnly endDate, string? categoryId = null, string? merchantText = null,
            int limit = 20, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new TransactionSearchResult([], false));
        }

        public Task<SpendingSummary> GetSpendingSummaryAsync(
            DateOnly startDate, DateOnly endDate, string? categoryId = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new SpendingSummary(0m, 0, "EUR", []));
        }
    }

    private static ToolChatRunner CreateRunner(FakeChatClient client, ExpenseDbContext context, int maxToolCalls = 4) =>
        new(client, new ExpenseTools(new TransactionQueries(context)), TimeSpan.FromSeconds(30),
            "Answer expense questions using tool results. Ask for clarification when dates are missing.", maxToolCalls);

    private static Dictionary<string, object?> Arguments(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;

    private static async Task<ExpenseDbContext> CreateContextAsync()
    {
        var options = new DbContextOptionsBuilder<ExpenseDbContext>()
            .UseSqlite("Data Source=:memory:;Foreign Keys=True")
            .Options;
        var context = new ExpenseDbContext(options);
        await context.Database.OpenConnectionAsync();
        await ExpenseDatabaseInitializer.InitializeAsync(context);
        context.ChangeTracker.Clear();
        return context;
    }
}