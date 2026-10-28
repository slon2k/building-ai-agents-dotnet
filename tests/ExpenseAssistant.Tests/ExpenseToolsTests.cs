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

    private static async Task<TResult> InvokeAsync<TResult>(AIFunction function, string json)
    {
        var arguments = new AIFunctionArguments(JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!);
        object? result = await function.InvokeAsync(arguments);
        JsonElement serializedResult = JsonSerializer.SerializeToElement(result);
        TResult? value = serializedResult.Deserialize<TResult>(JsonSerializerOptions.Web);
        Assert.NotNull(value);
        return value;
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