using System.Text.Json;
using ExpenseAssistant.App;
using ExpenseAssistant.Core;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Tests;

public class SemanticExpenseToolsTests
{
    [Fact]
    public void CreateFunctions_AdvertisesSemanticSchemaWithoutResolvingSearch()
    {
        var tools = new ExpenseTools(new UnusedQueries(), () => throw new InvalidOperationException("Must remain lazy"));

        IReadOnlyList<AIFunction> functions = tools.CreateFunctions();

        Assert.Equal(new[] { "SearchTransactions", "GetSpendingSummary", "SearchTransactionsByMeaning" },
            functions.Select(function => function.Name));
        AIFunction function = functions[2];
        JsonElement properties = function.JsonSchema.GetProperty("properties");
        Assert.Equal(new[] { "categoryId", "endDate", "limit", "query", "startDate" },
            properties.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "query" }, function.JsonSchema.GetProperty("required")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(5, properties.GetProperty("limit").GetProperty("default").GetInt32());
        foreach (string name in new[] { "startDate", "endDate" })
        {
            Assert.Equal("date", properties.GetProperty(name).GetProperty("format").GetString());
        }
        Assert.All(properties.EnumerateObject(), property =>
            Assert.False(string.IsNullOrWhiteSpace(property.Value.GetProperty("description").GetString())));
        Assert.Contains("not probabilities", function.Description);
        Assert.Contains("not exhaustive", function.Description);
        Assert.Contains("authoritative", function.Description);
        Assert.False(properties.TryGetProperty("cancellationToken", out _));
    }

    [Fact]
    public async Task InvokeAsync_BindsFiltersAndPreservesScoredResults()
    {
        var search = new FakeSearch();
        using var cancellation = new CancellationTokenSource();
        var tools = new ExpenseTools(new UnusedQueries(), () => search);
        AIFunction function = tools.CreateFunctions().Single(function => function.Name == "SearchTransactionsByMeaning");

        object? value = await function.InvokeAsync(new AIFunctionArguments(
            JsonSerializer.Deserialize<Dictionary<string, object?>>("""
                {"query":"remote work","startDate":"2026-03-01","endDate":"2026-04-01","categoryId":"shopping","limit":2}
                """)!), cancellation.Token);

        JsonElement envelope = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        Assert.True(envelope.GetProperty("isSuccess").GetBoolean());
        JsonElement result = envelope.GetProperty("result");
        Assert.Equal("TX-TEST", result.GetProperty("candidates")[0].GetProperty("transaction").GetProperty("id").GetString());
        Assert.Equal(0.75, result.GetProperty("candidates")[0].GetProperty("score").GetDouble());
        Assert.Equal("2026-03-01", result.GetProperty("startDate").GetString());
        Assert.Equal("2026-04-01", result.GetProperty("endDate").GetString());
        Assert.Equal("shopping", result.GetProperty("categoryId").GetString());
        Assert.Equal(3, result.GetProperty("eligibleRecordCount").GetInt32());
        Assert.True(result.GetProperty("hasMore").GetBoolean());
        Assert.Equal(1, result.GetProperty("returnedCount").GetInt32());
        Assert.Equal(("remote work", new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1), "shopping", 2),
            (search.Query, search.StartDate, search.EndDate, search.CategoryId, search.Limit));
        Assert.Equal(cancellation.Token, search.Token);
    }

    [Fact]
    public async Task InvokeAsync_OmittedOptionsUseSemanticDefaults()
    {
        var search = new FakeSearch();
        var dispatcher = new ToolDispatcher(new ExpenseTools(new UnusedQueries(), () => search).CreateFunctions());

        object? value = await dispatcher.InvokeAsync(Call("""{"query":"subscriptions"}"""));

        Assert.True(JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web)
            .GetProperty("isSuccess").GetBoolean());
        Assert.Null(search.StartDate);
        Assert.Null(search.EndDate);
        Assert.Null(search.CategoryId);
        Assert.Equal(5, search.Limit);
    }

    [Theory]
    [InlineData("", "INVALID_QUERY")]
    [InlineData(" ", "INVALID_QUERY")]
    public async Task SearchAsync_InvalidQueryDoesNotResolveSearch(string query, string code)
    {
        var tools = new ExpenseTools(new UnusedQueries(), () => throw new InvalidOperationException("Must remain lazy"));

        Assert.Equal(code, (await tools.SearchTransactionsByMeaningAsync(query)).Error?.Code);
    }

    [Fact]
    public async Task SearchAsync_EnforcesQueryLengthBoundary()
    {
        var search = new FakeSearch();
        var tools = new ExpenseTools(new UnusedQueries(), () => search);

        Assert.True((await tools.SearchTransactionsByMeaningAsync(new string('a', 2000))).IsSuccess);
        Assert.Equal("INVALID_QUERY", (await tools.SearchTransactionsByMeaningAsync(new string('a', 2001))).Error?.Code);
        Assert.Equal(1, search.Calls);
    }

    [Theory]
    [InlineData("""{"query":"desk","limit":0}""", "INVALID_LIMIT")]
    [InlineData("""{"query":"desk","limit":101}""", "INVALID_LIMIT")]
    [InlineData("""{"query":"desk","categoryId":" "}""", "INVALID_CATEGORY")]
    [InlineData("""{"query":"desk","startDate":"2026-04-01","endDate":"2026-03-01"}""", "INVALID_DATE_RANGE")]
    [InlineData("""{"query":"desk","startDate":"2026-03-01","endDate":"2026-03-01"}""", "INVALID_DATE_RANGE")]
    [InlineData("""{}""", "INVALID_ARGUMENTS")]
    [InlineData("""{"query":null}""", "INVALID_ARGUMENTS")]
    [InlineData("""{"query":"desk","merchantText":"store"}""", "INVALID_ARGUMENTS")]
    [InlineData("""{"query":"desk","limit":"bad"}""", "INVALID_ARGUMENTS")]
    [InlineData("""{"query":"desk","startDate":"invalid"}""", "INVALID_ARGUMENTS")]
    public async Task Dispatcher_InvalidArgumentsDoNotResolveSearch(string arguments, string expectedCode)
    {
        var dispatcher = new ToolDispatcher(new ExpenseTools(new UnusedQueries(),
            () => throw new InvalidOperationException("Must remain lazy")).CreateFunctions());

        object? value = await dispatcher.InvokeAsync(Call(arguments));

        JsonElement envelope = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        Assert.False(envelope.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(expectedCode, envelope.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SearchAsync_UnknownCategoryReturnsValidationFailure()
    {
        var search = new FakeSearch { Failure = new ArgumentException("PRIVATE_DETAILS", "categoryId") };
        var tools = new ExpenseTools(new UnusedQueries(), () => search);

        ToolResult<SemanticSearchResult> result = await tools.SearchTransactionsByMeaningAsync("desk", categoryId: "unknown");

        Assert.Equal("INVALID_CATEGORY", result.Error?.Code);
        Assert.DoesNotContain("PRIVATE", result.Error!.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchAsync_ProviderIndexOrResolutionFailureReturnsSafeError(bool resolutionFailure)
    {
        var search = new FakeSearch { Failure = new InvalidOperationException("PRIVATE_DETAILS") };
        var tools = new ExpenseTools(new UnusedQueries(), () =>
            resolutionFailure ? throw new InvalidOperationException("PRIVATE_CONFIG") : search);

        ToolResult<SemanticSearchResult> result = await tools.SearchTransactionsByMeaningAsync("desk");

        Assert.True(result.IsFailure);
        Assert.Null(result.Result);
        Assert.Equal("SEMANTIC_SEARCH_FAILED", result.Error?.Code);
        Assert.DoesNotContain("PRIVATE", result.Error!.Message);
    }

    [Fact]
    public async Task SearchAsync_EmptyCandidatesRemainSuccessful()
    {
        var tools = new ExpenseTools(new UnusedQueries(), () => new FakeSearch { Empty = true });

        ToolResult<SemanticSearchResult> result = await tools.SearchTransactionsByMeaningAsync("absent concept");

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Result!.Candidates);
        Assert.Equal(0, result.Result.EligibleRecordCount);
        Assert.False(result.Result.HasMore);
    }

    [Fact]
    public async Task SearchAsync_CancellationPropagates()
    {
        var search = new FakeSearch { Failure = new OperationCanceledException() };
        var tools = new ExpenseTools(new UnusedQueries(), () => search);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.SearchTransactionsByMeaningAsync("desk"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tools.SearchTransactionsByMeaningAsync("desk", cancellationToken: new CancellationToken(true)));
        Assert.Equal(1, search.Calls);
    }

    [Fact]
    public async Task ExactOnlyBaselineOmitsSemanticToolAndReportsUnavailableForDirectCall()
    {
        var tools = new ExpenseTools(new UnusedQueries());

        Assert.Equal(new[] { "SearchTransactions", "GetSpendingSummary" },
            tools.CreateFunctions().Select(function => function.Name));
        Assert.Equal("SEMANTIC_SEARCH_UNAVAILABLE", (await tools.SearchTransactionsByMeaningAsync("desk")).Error?.Code);
    }

    [Fact]
    public async Task AgentRunner_AdvertisesAndExecutesSemanticToolWithOriginalCallId()
    {
        var search = new FakeSearch();
        var tools = new ExpenseTools(new UnusedQueries(), () => search);
        Queue<ChatResponse> responses = new([
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call("""{"query":"remote work"}""")])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "TX-TEST may be related to remote work."))
        ]);
        var client = new FakeChatClient((_, _) => Task.FromResult(responses.Dequeue()));
        var runner = new AgentRunner(client, new ToolDispatcher(tools.CreateFunctions()), "Use tools.", new AgentRunOptions());

        AgentRunResult result = await runner.RunAsync(Guid.NewGuid(), "Find possible remote-work expenses.", CancellationToken.None);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Equal(1, search.Calls);
        Assert.Contains(client.ReceivedOptions[0]!.Tools!, tool => tool.Name == "SearchTransactionsByMeaning");
        FunctionResultContent content = Assert.Single(client.ReceivedRequests[1]
            .SelectMany(message => message.Contents).OfType<FunctionResultContent>());
        Assert.Equal("semantic-call", content.CallId);
        JsonElement envelope = JsonSerializer.SerializeToElement(content.Result, JsonSerializerOptions.Web);
        Assert.True(envelope.GetProperty("isSuccess").GetBoolean());
        Assert.Equal("TX-TEST", envelope.GetProperty("result").GetProperty("candidates")[0]
            .GetProperty("transaction").GetProperty("id").GetString());
    }

    private static FunctionCallContent Call(string json) => new("semantic-call", "SearchTransactionsByMeaning",
        JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!);

    private sealed class FakeSearch : ITransactionSemanticSearch
    {
        public int Calls { get; private set; }
        public string? Query { get; private set; }
        public DateOnly? StartDate { get; private set; }
        public DateOnly? EndDate { get; private set; }
        public string? CategoryId { get; private set; }
        public int Limit { get; private set; }
        public CancellationToken Token { get; private set; }
        public Exception? Failure { get; init; }
        public bool Empty { get; init; }

        public Task<SemanticSearchResult> SearchAsync(string query, DateOnly? startDate = null,
            DateOnly? endDate = null, string? categoryId = null, int limit = 5, CancellationToken cancellationToken = default)
        {
            Calls++;
            Query = query;
            StartDate = startDate;
            EndDate = endDate;
            CategoryId = categoryId;
            Limit = limit;
            Token = cancellationToken;
            if (Failure is not null) throw Failure;
            SemanticSearchCandidate[] candidates = Empty ? [] :
            [
                new(new TransactionDto("TX-TEST", new DateOnly(2026, 3, 1), "DeskSupply", "Laptop stand",
                    "shopping", "Shopping", 20m, "EUR"), 0.75)
            ];
            return Task.FromResult(new SemanticSearchResult(candidates, startDate, endDate, categoryId, Empty ? 0 : 3, limit));
        }
    }

    private sealed class UnusedQueries : ITransactionQueries
    {
        public Task<TransactionSearchResult> SearchTransactionsAsync(DateOnly startDate, DateOnly endDate,
            string? categoryId = null, string? merchantText = null, int limit = 20, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Exact queries must not run during semantic search.");

        public Task<SpendingSummary> GetSpendingSummaryAsync(DateOnly startDate, DateOnly endDate,
            string? categoryId = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Exact queries must not run during semantic search.");
    }
}
