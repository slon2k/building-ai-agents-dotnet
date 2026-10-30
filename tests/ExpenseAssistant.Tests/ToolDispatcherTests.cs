using System.Text.Json;
using ExpenseAssistant.App;
using ExpenseAssistant.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Tests;

public class ToolDispatcherTests
{
    [Fact]
    public void Constructor_SnapshotsFunctionsAndRejectsDuplicateNames()
    {
        AIFunction function = AIFunctionFactory.Create(OptionalMessage);
        List<AIFunction> functions = [function];

        var dispatcher = new ToolDispatcher(functions);
        functions.Clear();

        Assert.Same(function, Assert.Single(dispatcher.Functions));
        Assert.Throws<ArgumentNullException>(() => new ToolDispatcher(null!));
        Assert.Throws<ArgumentException>(() => new ToolDispatcher([function, function]));
    }

    [Fact]
    public async Task InvokeAsync_WithExpenseFunctions_ReturnsBoundSearchAndSummaryResults()
    {
        await using var context = await CreateContextAsync();
        var dispatcher = new ToolDispatcher(new ExpenseTools(new TransactionQueries(context)).CreateFunctions());
        Assert.Equal(new[] { "SearchTransactions", "GetSpendingSummary" }, dispatcher.Functions.Select(function => function.Name));

        JsonElement search = Envelope(await dispatcher.InvokeAsync(Call("SearchTransactions", """
            { "startDate": "2026-03-01", "endDate": "2026-04-01", "categoryId": "groceries", "limit": 1 }
            """)));
        JsonElement summary = Envelope(await dispatcher.InvokeAsync(Call("GetSpendingSummary", ValidArguments)));

        Assert.True(search.GetProperty("isSuccess").GetBoolean());
        Assert.True(search.GetProperty("result").GetProperty("hasMore").GetBoolean());
        Assert.Single(search.GetProperty("result").GetProperty("transactions").EnumerateArray());
        Assert.True(summary.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(21.25m, summary.GetProperty("result").GetProperty("totalSpending").GetDecimal());
        Assert.Equal(2, summary.GetProperty("result").GetProperty("transactionCount").GetInt32());
    }

    [Theory]
    [InlineData("DeleteTransaction", "{}", "UNKNOWN_TOOL")]
    [InlineData("getspendingsummary", "{}", "UNKNOWN_TOOL")]
    [InlineData("GetSpendingSummary", "null", "INVALID_ARGUMENTS")]
    [InlineData("GetSpendingSummary", "{}", "INVALID_ARGUMENTS")]
    [InlineData("GetSpendingSummary", "{\"startDate\":\"2026-03-01\"}", "INVALID_ARGUMENTS")]
    [InlineData("GetSpendingSummary", "{\"startDate\":null,\"endDate\":\"2026-04-01\"}", "INVALID_ARGUMENTS")]
    [InlineData("GetSpendingSummary", "{\"startDate\":\"2026-02-30\",\"endDate\":\"2026-04-01\"}", "INVALID_ARGUMENTS")]
    [InlineData("GetSpendingSummary", "{\"startDate\":123,\"endDate\":\"2026-04-01\"}", "INVALID_ARGUMENTS")]
    [InlineData("GetSpendingSummary", "{\"startDate\":\"2026-03-01\",\"endDate\":\"2026-04-01\",\"categoryId\":123}", "INVALID_ARGUMENTS")]
    [InlineData("SearchTransactions", "{\"startDate\":\"2026-03-01\",\"endDate\":\"2026-04-01\",\"limit\":\"abc\"}", "INVALID_ARGUMENTS")]
    [InlineData("SearchTransactions", "{\"startDate\":\"2026-03-01\",\"endDate\":\"2026-04-01\",\"limit\":2147483648}", "INVALID_ARGUMENTS")]
    [InlineData("GetSpendingSummary", "{\"startDate\":\"2026-03-01\",\"endDate\":\"2026-04-01\",\"cancellationToken\":null}", "INVALID_ARGUMENTS")]
    [InlineData("GetSpendingSummary", "{\"startDate\":\"2026-04-01\",\"endDate\":\"2026-03-01\"}", "INVALID_DATE_RANGE")]
    [InlineData("GetSpendingSummary", "{\"startDate\":\"2026-03-01\",\"endDate\":\"2026-04-01\",\"categoryId\":\"unknown\"}", "INVALID_CATEGORY")]
    [InlineData("SearchTransactions", "{\"startDate\":\"2026-03-01\",\"endDate\":\"2026-04-01\",\"limit\":0}", "INVALID_LIMIT")]
    public async Task InvokeAsync_WithInvalidRequest_ReturnsSafeError(string name, string json, string expectedCode)
    {
        await using var context = await CreateContextAsync();
        var dispatcher = new ToolDispatcher(new ExpenseTools(new TransactionQueries(context)).CreateFunctions());

        object? result = await dispatcher.InvokeAsync(Call(name, json));

        AssertFailure(result, expectedCode);
    }

    [Fact]
    public async Task InvokeAsync_WithOptionalOnlyFunction_UsesDefaultWithoutRequiredSchemaProperties()
    {
        AIFunction function = AIFunctionFactory.Create(OptionalMessage);
        Assert.False(function.JsonSchema.TryGetProperty("required", out _));
        var dispatcher = new ToolDispatcher([function]);

        object? result = await dispatcher.InvokeAsync(Call(function.Name, "{}"));

        Assert.Equal("default", Envelope(result).GetString());
        Assert.Equal("custom", Envelope(await dispatcher.InvokeAsync(Call(function.Name, """
            { "message": "custom" }
            """))).GetString());
    }

    [Fact]
    public async Task InvokeAsync_WithParameterlessFunction_RejectsUnknownArgumentsBeforeExecution()
    {
        int invocationCount = 0;
        AIFunction function = AIFunctionFactory.Create(() => ++invocationCount, "Count");
        var dispatcher = new ToolDispatcher([function]);

        AssertFailure(await dispatcher.InvokeAsync(Call("Count", "{\"unexpected\":true}")), "INVALID_ARGUMENTS");
        Assert.Equal(0, invocationCount);
        Assert.Equal(1, Envelope(await dispatcher.InvokeAsync(Call("Count", "{}"))).GetInt32());
    }

    [Fact]
    public async Task InvokeAsync_WithNullableOptionalArgument_AcceptsNull()
    {
        AIFunction function = AIFunctionFactory.Create(OptionalMessage);
        var dispatcher = new ToolDispatcher([function]);

        object? result = await dispatcher.InvokeAsync(Call(function.Name, "{\"message\":null}"));

        Assert.Equal(JsonValueKind.Null, Envelope(result).ValueKind);
    }

    [Fact]
    public async Task InvokeAsync_WhenQueryFails_PreservesSafeDomainError()
    {
        await using var context = await CreateContextAsync();
        await context.Database.ExecuteSqlRawAsync("DROP TABLE Transactions");
        var dispatcher = new ToolDispatcher(new ExpenseTools(new TransactionQueries(context)).CreateFunctions());

        object? result = await dispatcher.InvokeAsync(Call("GetSpendingSummary", ValidArguments));

        AssertFailure(result, "QUERY_FAILED");
        Assert.DoesNotContain("SQLite", Envelope(result).GetRawText());
    }

    [Fact]
    public async Task InvokeAsync_WhenFunctionThrows_ReturnsSafeFailure()
    {
        AIFunction function = AIFunctionFactory.Create(ThrowFailure);
        var dispatcher = new ToolDispatcher([function]);

        object? result = await dispatcher.InvokeAsync(Call(function.Name, "{}"));

        AssertFailure(result, "TOOL_FAILED");
        Assert.DoesNotContain("PRIVATE", Envelope(result).GetRawText());
    }

    [Fact]
    public async Task InvokeAsync_WhenAlreadyCancelled_DoesNotInvokeFunction()
    {
        int invocationCount = 0;
        AIFunction function = AIFunctionFactory.Create(() => ++invocationCount, "Count");
        var dispatcher = new ToolDispatcher([function]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.InvokeAsync(Call("Count", "{}"), cancellation.Token));

        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public async Task InvokeAsync_PassesCancellationTokenAndPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken receivedToken = default;
        AIFunction function = AIFunctionFactory.Create((CancellationToken cancellationToken) =>
        {
            receivedToken = cancellationToken;
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return "unreachable";
        }, "Cancel");
        var dispatcher = new ToolDispatcher([function]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.InvokeAsync(Call("Cancel", "{}"), cancellation.Token));

        Assert.Equal(cancellation.Token, receivedToken);
    }

    private const string ValidArguments = """
        { "startDate": "2026-03-01", "endDate": "2026-04-01", "categoryId": "groceries" }
        """;

    private static string? OptionalMessage(string? message = "default") => message;

    private static string ThrowFailure() => throw new InvalidOperationException("PRIVATE function details");

    private static FunctionCallContent Call(string name, string json) => new("call-1", name,
        JsonSerializer.Deserialize<Dictionary<string, object?>>(json));

    private static JsonElement Envelope(object? result) => JsonSerializer.SerializeToElement(result, JsonSerializerOptions.Web);

    private static void AssertFailure(object? result, string expectedCode)
    {
        JsonElement envelope = Envelope(result);
        Assert.False(envelope.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(expectedCode, envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.True(!envelope.TryGetProperty("result", out JsonElement value) || value.ValueKind == JsonValueKind.Null);
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