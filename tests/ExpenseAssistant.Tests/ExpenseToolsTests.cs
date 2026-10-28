using System.Text.Json;
using ExpenseAssistant.App;
using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Tests;

public class ExpenseToolsTests
{
    [Fact]
    public async Task CreateFunctions_ExposesNamedDescribedToolsWithExpectedSchemas()
    {
        await using var context = await CreateContextAsync();
        IReadOnlyList<AIFunction> functions = new ExpenseTools(new TransactionQueries(context)).CreateFunctions();

        Assert.Equal(["SearchTransactions", "GetSpendingSummary"], functions.Select(function => function.Name));

        foreach (AIFunction function in functions)
        {
            Assert.False(string.IsNullOrWhiteSpace(function.Description));
            JsonElement schema = function.JsonSchema;
            Assert.Equal("object", schema.GetProperty("type").GetString());

            JsonElement properties = schema.GetProperty("properties");
            string[] expectedParameters = function.Name == "SearchTransactions"
                ? ["categoryId", "endDate", "limit", "merchantText", "startDate"]
                : ["categoryId", "endDate", "startDate"];
            Assert.Equal(expectedParameters, properties.EnumerateObject()
                .Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal(new[] { "endDate", "startDate" }, schema.GetProperty("required")
                .EnumerateArray().Select(property => property.GetString()).Order(StringComparer.Ordinal));

            foreach (JsonProperty property in properties.EnumerateObject())
            {
                Assert.False(string.IsNullOrWhiteSpace(property.Value.GetProperty("description").GetString()));
            }

            Assert.Equal("string", properties.GetProperty("startDate").GetProperty("type").GetString());
            Assert.Equal("date", properties.GetProperty("startDate").GetProperty("format").GetString());
            Assert.Equal("string", properties.GetProperty("endDate").GetProperty("type").GetString());
            Assert.Equal("date", properties.GetProperty("endDate").GetProperty("format").GetString());
            Assert.False(properties.TryGetProperty("cancellationToken", out _));
        }

        Assert.Equal(20, functions[0].JsonSchema.GetProperty("properties")
            .GetProperty("limit").GetProperty("default").GetInt32());
    }

    [Fact]
    public async Task InvokeAsync_WithJsonArguments_PreservesLimitedSearchAndCompleteSummary()
    {
        await using var context = await CreateContextAsync();
        IReadOnlyList<AIFunction> functions = new ExpenseTools(new TransactionQueries(context)).CreateFunctions();

        TransactionSearchResult search = await InvokeAsync<TransactionSearchResult>(functions[0], """
            {
                "startDate": "2026-03-01",
                "endDate": "2026-04-01",
                "categoryId": "groceries",
                "merchantText": "green market",
                "limit": 1
            }
            """);
        SpendingSummary summary = await InvokeAsync<SpendingSummary>(functions[1], """
            {
                "startDate": "2026-03-01",
                "endDate": "2026-04-01",
                "categoryId": "groceries"
            }
            """);

        TransactionDto transaction = Assert.Single(search.Transactions);
        Assert.Equal("TX-G1", transaction.Id);
        Assert.Equal(new DateOnly(2026, 3, 2), transaction.Date);
        Assert.Equal(12.50m, transaction.Amount);
        Assert.Equal("groceries", transaction.CategoryId);
        Assert.Equal(1, search.ReturnedCount);
        Assert.True(search.HasMore);
        Assert.Equal(21.25m, summary.TotalSpending);
        Assert.Equal(2, summary.TransactionCount);
        Assert.Equal("EUR", summary.Currency);
        Assert.Equal(new CategorySpendingTotal("groceries", "Groceries", 21.25m, 2), Assert.Single(summary.CategoryTotals));
    }

    [Fact]
    public async Task InvokeAsync_WithOptionalArgumentsOmitted_UsesDefaults()
    {
        await using var context = await CreateContextAsync();
        IReadOnlyList<AIFunction> functions = new ExpenseTools(new TransactionQueries(context)).CreateFunctions();

        TransactionSearchResult search = await InvokeAsync<TransactionSearchResult>(functions[0], """
            { "startDate": "2026-03-01", "endDate": "2026-04-01" }
            """);
        SpendingSummary summary = await InvokeAsync<SpendingSummary>(functions[1], """
            { "startDate": "2026-03-01", "endDate": "2026-04-01" }
            """);

        Assert.Equal(20, search.ReturnedCount);
        Assert.True(search.HasMore);
        Assert.All(search.Transactions, transaction => Assert.Equal(3, transaction.Date.Month));
        Assert.Equal(22, summary.TransactionCount);
        Assert.Equal("EUR", summary.Currency);
        Assert.True(summary.CategoryTotals.Count > 1);
    }

    [Theory]
    [InlineData("SearchTransactions", "endDate", "2026-03-01", "INVALID_DATE_RANGE")]
    [InlineData("GetSpendingSummary", "endDate", "2026-02-01", "INVALID_DATE_RANGE")]
    [InlineData("SearchTransactions", "categoryId", " ", "INVALID_CATEGORY")]
    [InlineData("GetSpendingSummary", "categoryId", "", "INVALID_CATEGORY")]
    [InlineData("SearchTransactions", "categoryId", "unknown", "INVALID_CATEGORY")]
    [InlineData("GetSpendingSummary", "categoryId", "unknown", "INVALID_CATEGORY")]
    [InlineData("SearchTransactions", "merchantText", " ", "INVALID_MERCHANT")]
    [InlineData("SearchTransactions", "limit", 0, "INVALID_LIMIT")]
    [InlineData("SearchTransactions", "limit", -1, "INVALID_LIMIT")]
    [InlineData("SearchTransactions", "limit", 101, "INVALID_LIMIT")]
    public async Task InvokeAsync_WithInvalidArguments_ReturnsStructuredFailure(
        string toolName, string parameter, object value, string expectedCode)
    {
        await using var context = await CreateContextAsync();
        AIFunction function = new ExpenseTools(new TransactionQueries(context)).CreateFunctions()
            .Single(candidate => candidate.Name == toolName);
        var arguments = new Dictionary<string, object?>
        {
            ["startDate"] = "2026-03-01",
            ["endDate"] = "2026-04-01",
            [parameter] = value
        };

        JsonElement envelope = await InvokeResultAsync(function, JsonSerializer.Serialize(arguments));

        AssertFailure(envelope, expectedCode);
    }

    [Theory]
    [InlineData("SearchTransactions")]
    [InlineData("GetSpendingSummary")]
    public async Task InvokeAsync_WithInvalidRange_DoesNotAccessDatabase(string toolName)
    {
        await using var context = await CreateContextAsync();
        await context.Database.ExecuteSqlRawAsync("DROP TABLE Transactions");
        AIFunction function = new ExpenseTools(new TransactionQueries(context)).CreateFunctions()
            .Single(candidate => candidate.Name == toolName);

        JsonElement envelope = await InvokeResultAsync(function, """
            { "startDate": "2026-03-01", "endDate": "2026-03-01" }
            """);

        AssertFailure(envelope, "INVALID_DATE_RANGE");
    }

    [Theory]
    [InlineData("SearchTransactions")]
    [InlineData("GetSpendingSummary")]
    public async Task InvokeAsync_WhenDatabaseFails_ReturnsSafeFailure(string toolName)
    {
        await using var context = await CreateContextAsync();
        await context.Database.ExecuteSqlRawAsync("DROP TABLE Transactions");
        AIFunction function = new ExpenseTools(new TransactionQueries(context)).CreateFunctions()
            .Single(candidate => candidate.Name == toolName);

        JsonElement envelope = await InvokeResultAsync(function, """
            { "startDate": "2026-03-01", "endDate": "2026-04-01" }
            """);

        AssertFailure(envelope, "QUERY_FAILED");
        Assert.Equal("The transaction query could not be completed. No result is available.",
            envelope.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain("SQLite", envelope.GetRawText());
        Assert.DoesNotContain("no such table", envelope.GetRawText());
    }

    [Fact]
    public async Task InvokeAsync_WithNoMatches_ReturnsSuccessfulEmptyResults()
    {
        await using var context = await CreateContextAsync();
        IReadOnlyList<AIFunction> functions = new ExpenseTools(new TransactionQueries(context)).CreateFunctions();
        const string arguments = """
            { "startDate": "2025-01-01", "endDate": "2025-02-01" }
            """;

        TransactionSearchResult search = await InvokeAsync<TransactionSearchResult>(functions[0], arguments);
        SpendingSummary summary = await InvokeAsync<SpendingSummary>(functions[1], arguments);

        Assert.Empty(search.Transactions);
        Assert.False(search.HasMore);
        Assert.Equal(0m, summary.TotalSpending);
        Assert.Equal(0, summary.TransactionCount);
        Assert.Equal("EUR", summary.Currency);
    }

    [Fact]
    public async Task Tools_WhenAlreadyCancelled_PropagateCancellation()
    {
        await using var context = await CreateContextAsync();
        var tools = new ExpenseTools(new TransactionQueries(context));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.SearchTransactionsAsync(
            new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1), cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.GetSpendingSummaryAsync(
            new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1), cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Tools_WhenQueryThrowsCancellation_DoNotConvertItToFailure()
    {
        var tools = new ExpenseTools(new CancellingQueries());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.SearchTransactionsAsync(
            new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.GetSpendingSummaryAsync(
            new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1)));
    }

    private static void AssertFailure(JsonElement envelope, string expectedCode)
    {
        Assert.False(envelope.GetProperty("isSuccess").GetBoolean());
        Assert.True(envelope.GetProperty("isFailure").GetBoolean());
        Assert.True(!envelope.TryGetProperty("result", out JsonElement result) || result.ValueKind == JsonValueKind.Null);
        JsonElement error = envelope.GetProperty("error");
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
    }

    private sealed class CancellingQueries : ITransactionQueries
    {
        public Task<TransactionSearchResult> SearchTransactionsAsync(
            DateOnly startDate, DateOnly endDate, string? categoryId = null, string? merchantText = null,
            int limit = 20, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException();

        public Task<SpendingSummary> GetSpendingSummaryAsync(
            DateOnly startDate, DateOnly endDate, string? categoryId = null,
            CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException();
    }

    private static async Task<TResult> InvokeAsync<TResult>(AIFunction function, string json)
    {
        JsonElement envelope = await InvokeResultAsync(function, json);
        Assert.True(envelope.GetProperty("isSuccess").GetBoolean());
        Assert.False(envelope.GetProperty("isFailure").GetBoolean());
        Assert.True(!envelope.TryGetProperty("error", out JsonElement error) || error.ValueKind == JsonValueKind.Null);
        TResult? value = envelope.GetProperty("result").Deserialize<TResult>(JsonSerializerOptions.Web);
        Assert.NotNull(value);
        return value;
    }

    private static async Task<JsonElement> InvokeResultAsync(
        AIFunction function, string json, CancellationToken cancellationToken = default)
    {
        var arguments = new AIFunctionArguments(JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!);
        object? result = await function.InvokeAsync(arguments, cancellationToken);
        return JsonSerializer.SerializeToElement(result, JsonSerializerOptions.Web);
    }

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