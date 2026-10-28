using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ExpenseAssistant.Tests;

public class TransactionQueriesTests
{
    private static readonly DateOnly MarchStart = new(2026, 3, 1);
    private static readonly DateOnly AprilStart = new(2026, 4, 1);

    [Fact]
    public async Task SearchAndSummary_WithLimitedSearch_StillSummarizesAllMatchingTransactions()
    {
        await using var context = await CreateContextAsync();
        ITransactionQueries queries = new TransactionQueries(context);

        TransactionSearchResult search = await queries.SearchTransactionsAsync(MarchStart, AprilStart, "groceries", limit: 1);
        SpendingSummary summary = await queries.GetSpendingSummaryAsync(MarchStart, AprilStart, "groceries");

        Assert.Equal("TX-G1", Assert.Single(search.Transactions).Id);
        Assert.Equal(1, search.ReturnedCount);
        Assert.True(search.HasMore);
        Assert.Equal(21.25m, summary.TotalSpending);
        Assert.Equal(2, summary.TransactionCount);
        Assert.Equal("EUR", summary.Currency);
        Assert.Equal(new CategorySpendingTotal("groceries", "Groceries", 21.25m, 2), Assert.Single(summary.CategoryTotals));
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task SearchTransactionsAsync_IncludesStartAndExcludesEnd()
    {
        await using var context = await CreateContextAsync();
        var queries = new TransactionQueries(context);

        TransactionSearchResult result = await queries.SearchTransactionsAsync(
            new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 14), "groceries");

        Assert.Equal("TX-G1", Assert.Single(result.Transactions).Id);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task Queries_WithNoMatches_ReturnSuccessfulEmptyResults()
    {
        await using var context = await CreateContextAsync();
        var queries = new TransactionQueries(context);
        var startDate = new DateOnly(2025, 1, 1);
        var endDate = new DateOnly(2025, 2, 1);

        TransactionSearchResult search = await queries.SearchTransactionsAsync(startDate, endDate);
        SpendingSummary summary = await queries.GetSpendingSummaryAsync(startDate, endDate);

        Assert.Empty(search.Transactions);
        Assert.Equal(0, search.ReturnedCount);
        Assert.False(search.HasMore);
        Assert.Equal(0m, summary.TotalSpending);
        Assert.Equal(0, summary.TransactionCount);
        Assert.Equal("EUR", summary.Currency);
        Assert.Empty(summary.CategoryTotals);
    }

    [Theory]
    [InlineData("green market", 2)]
    [InlineData("  MaRkEt  ", 2)]
    [InlineData("%", 0)]
    [InlineData("_", 0)]
    [InlineData("\\", 0)]
    public async Task SearchTransactionsAsync_AppliesLiteralMerchantSubstringFilter(string merchantText, int expectedCount)
    {
        await using var context = await CreateContextAsync();
        var queries = new TransactionQueries(context);

        TransactionSearchResult result = await queries.SearchTransactionsAsync(
            MarchStart, AprilStart, merchantText: merchantText);

        Assert.Equal(expectedCount, result.ReturnedCount);
        Assert.False(result.HasMore);
        Assert.All(result.Transactions, transaction => Assert.Equal("Green Market", transaction.Merchant));
    }

    [Fact]
    public async Task SearchTransactionsAsync_CombinesCategoryAndMerchantFilters()
    {
        await using var context = await CreateContextAsync();
        var queries = new TransactionQueries(context);

        TransactionSearchResult result = await queries.SearchTransactionsAsync(
            MarchStart, AprilStart, "dining", "Green Market");

        Assert.Empty(result.Transactions);
        Assert.False(result.HasMore);
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(2, 2, false)]
    [InlineData(3, 2, false)]
    public async Task SearchTransactionsAsync_SetsHasMoreOnlyWhenMatchesAreOmitted(int limit, int expectedCount, bool expectedHasMore)
    {
        await using var context = await CreateContextAsync();
        var queries = new TransactionQueries(context);

        TransactionSearchResult result = await queries.SearchTransactionsAsync(MarchStart, AprilStart, "groceries", limit: limit);

        Assert.Equal(expectedCount, result.ReturnedCount);
        Assert.Equal(expectedHasMore, result.HasMore);
    }

    [Fact]
    public async Task SearchTransactionsAsync_OrdersByDateThenId()
    {
        await using var context = await CreateContextAsync();
        context.Transactions.Add(new Transaction
        {
            Id = "TX-G0",
            Date = new DateOnly(2026, 3, 2),
            Merchant = "Green Market",
            Description = "Additional purchase",
            CategoryId = "groceries",
            Amount = 1m,
            Currency = "EUR"
        });
        await context.SaveChangesAsync();
        var queries = new TransactionQueries(context);

        TransactionSearchResult result = await queries.SearchTransactionsAsync(MarchStart, AprilStart, "groceries");

        Assert.Equal(new[] { "TX-G0", "TX-G1", "TX-G2" }, result.Transactions.Select(transaction => transaction.Id));
    }

    [Fact]
    public async Task GetSpendingSummaryAsync_CalculatesExactTotalsForEachCategory()
    {
        await using var context = await CreateContextAsync();
        var date = new DateOnly(2024, 1, 1);
        context.Transactions.AddRange(
            new Transaction
            {
                Id = "EXACT-1", Date = date, Merchant = "Market", Description = "First purchase",
                CategoryId = "groceries", Amount = 0.10m, Currency = "EUR"
            },
            new Transaction
            {
                Id = "EXACT-2", Date = date, Merchant = "Market", Description = "Second purchase",
                CategoryId = "groceries", Amount = 0.20m, Currency = "EUR"
            },
            new Transaction
            {
                Id = "EXACT-3", Date = date, Merchant = "Cafe", Description = "Coffee",
                CategoryId = "dining", Amount = 1.15m, Currency = "EUR"
            });
        await context.SaveChangesAsync();
        var queries = new TransactionQueries(context);

        SpendingSummary summary = await queries.GetSpendingSummaryAsync(date, date.AddDays(1));

        Assert.Equal(1.45m, summary.TotalSpending);
        Assert.Equal(3, summary.TransactionCount);
        Assert.Equal("EUR", summary.Currency);
        Assert.Equal(new[]
        {
            new CategorySpendingTotal("dining", "Dining", 1.15m, 1),
            new CategorySpendingTotal("groceries", "Groceries", 0.30m, 2)
        }, summary.CategoryTotals);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Queries_RejectInvalidDateRanges(int daysAfterStart)
    {
        await using var context = await CreateContextAsync();
        var queries = new TransactionQueries(context);
        DateOnly endDate = MarchStart.AddDays(daysAfterStart);

        await Assert.ThrowsAsync<ArgumentException>(() => queries.SearchTransactionsAsync(MarchStart, endDate));
        await Assert.ThrowsAsync<ArgumentException>(() => queries.GetSpendingSummaryAsync(MarchStart, endDate));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public async Task SearchTransactionsAsync_RejectsInvalidLimits(int limit)
    {
        await using var context = await CreateContextAsync();
        var queries = new TransactionQueries(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            queries.SearchTransactionsAsync(MarchStart, AprilStart, limit: limit));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Queries_RejectInvalidCategories(string categoryId)
    {
        await using var context = await CreateContextAsync();
        var queries = new TransactionQueries(context);

        await Assert.ThrowsAsync<ArgumentException>(() => queries.SearchTransactionsAsync(MarchStart, AprilStart, categoryId));
        await Assert.ThrowsAsync<ArgumentException>(() => queries.GetSpendingSummaryAsync(MarchStart, AprilStart, categoryId));
    }

    [Fact]
    public async Task SearchTransactionsAsync_RejectsBlankMerchantFilter()
    {
        await using var context = await CreateContextAsync();
        var queries = new TransactionQueries(context);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            queries.SearchTransactionsAsync(MarchStart, AprilStart, merchantText: " "));
    }

    [Fact]
    public async Task GetSpendingSummaryAsync_RejectsUnexpectedCurrency()
    {
        await using var context = await CreateContextAsync();
        Transaction transaction = await context.Transactions.SingleAsync(entity => entity.Id == "TX-G1");
        transaction.Currency = "USD";
        await context.SaveChangesAsync();
        var queries = new TransactionQueries(context);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queries.GetSpendingSummaryAsync(MarchStart, AprilStart, "groceries"));
    }

    [Fact]
    public async Task Queries_PropagateCancellation()
    {
        await using var context = await CreateContextAsync();
        var queries = new TransactionQueries(context);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            queries.SearchTransactionsAsync(MarchStart, AprilStart, cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            queries.GetSpendingSummaryAsync(MarchStart, AprilStart, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Queries_PropagateDatabaseFailureInsteadOfReturningEmptyResults()
    {
        await using var context = await CreateContextAsync();
        await context.Database.ExecuteSqlRawAsync("DROP TABLE Transactions");
        var queries = new TransactionQueries(context);

        await Assert.ThrowsAsync<SqliteException>(() => queries.SearchTransactionsAsync(MarchStart, AprilStart));
        await Assert.ThrowsAsync<SqliteException>(() => queries.GetSpendingSummaryAsync(MarchStart, AprilStart));
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