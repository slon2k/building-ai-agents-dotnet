using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ExpenseAssistant.Tests;

public class ExpenseDbContextTests
{
    [Fact]
    public async Task SaveChangesAsync_PreservesTransactionAndStoresIntegerMinorUnits()
    {
        await using var context = CreateContext();
        context.Categories.Add(new Category { Id = "groceries", Name = "Groceries" });
        context.Transactions.Add(CreateTransaction());

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Transaction transaction = await context.Transactions.Include(entity => entity.Category).SingleAsync();

        Assert.Equal("TX-001", transaction.Id);
        Assert.Equal(new DateOnly(2026, 3, 2), transaction.Date);
        Assert.Equal(12.50m, transaction.Amount);
        Assert.Equal("groceries", transaction.CategoryId);
        Assert.Equal("Groceries", transaction.Category.Name);

        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT AmountMinorUnits, typeof(AmountMinorUnits) FROM Transactions";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1250L, reader.GetInt64(0));
        Assert.Equal("integer", reader.GetString(1));
    }

    [Fact]
    public async Task SaveChangesAsync_RejectsFractionalMinorUnits()
    {
        await using var context = CreateContext();
        context.Categories.Add(new Category { Id = "groceries", Name = "Groceries" });
        Transaction transaction = CreateTransaction();
        transaction.Amount = 12.501m;
        context.Transactions.Add(transaction);

        Exception exception = await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync());

        Assert.IsType<ArgumentOutOfRangeException>(exception.GetBaseException());
    }

    [Fact]
    public async Task SaveChangesAsync_RejectsUnknownCategory()
    {
        await using var context = CreateContext();
        context.Transactions.Add(CreateTransaction());

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_RejectsDeletingReferencedCategory()
    {
        await using var context = CreateContext();
        context.Categories.Add(new Category { Id = "groceries", Name = "Groceries" });
        context.Transactions.Add(CreateTransaction());
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Category category = await context.Categories.SingleAsync();
        context.Categories.Remove(category);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(1, await context.Transactions.CountAsync());
    }

    [Fact]
    public async Task InitializeAsync_CreatesDatabaseWithFixedSyntheticDataset()
    {
        await using var context = CreateContext(createSchema: false);

        await ExpenseDatabaseInitializer.InitializeAsync(context);

        Assert.Equal(8, await context.Categories.CountAsync());
        List<Transaction> transactions = await context.Transactions.AsNoTracking().ToListAsync();
        Assert.Equal(123, transactions.Count);
        Assert.All(transactions, transaction =>
        {
            Assert.Equal("EUR", transaction.Currency);
            Assert.True(transaction.Amount > 0);
        });
        Assert.Equal(6, transactions.Select(transaction => transaction.Date.Month).Distinct().Count());
    }

    [Fact]
    public async Task InitializeAsync_WhenRepeated_DoesNotDuplicateOrOverwriteRecords()
    {
        await using var context = CreateContext();
        await ExpenseDatabaseInitializer.InitializeAsync(context);
        Transaction transaction = await context.Transactions.SingleAsync(entity => entity.Id == "TX-G1");
        transaction.CategoryId = "shopping";
        Category category = await context.Categories.SingleAsync(entity => entity.Id == "groceries");
        category.Name = "Food shopping";
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await ExpenseDatabaseInitializer.InitializeAsync(context);

        Assert.Equal(123, await context.Transactions.CountAsync());
        Assert.Equal(8, await context.Categories.CountAsync());
        Assert.Equal("shopping", (await context.Transactions.SingleAsync(entity => entity.Id == "TX-G1")).CategoryId);
        Assert.Equal("Food shopping", (await context.Categories.SingleAsync(entity => entity.Id == "groceries")).Name);
    }

    [Fact]
    public async Task InitializeAsync_PreservesKnownMarchGroceriesFixture()
    {
        await using var context = CreateContext();
        await ExpenseDatabaseInitializer.InitializeAsync(context);

        List<Transaction> groceries = await context.Transactions.AsNoTracking()
            .Where(transaction => transaction.CategoryId == "groceries"
                && transaction.Date >= new DateOnly(2026, 3, 1)
                && transaction.Date < new DateOnly(2026, 4, 1))
            .OrderBy(transaction => transaction.Id)
            .ToListAsync();

        Assert.Equal(new[] { "TX-G1", "TX-G2" }, groceries.Select(transaction => transaction.Id));
        Assert.Equal(21.25m, groceries.Sum(transaction => transaction.Amount));
        Transaction aprilTransaction = await context.Transactions.SingleAsync(transaction => transaction.Id == "TX-G3");
        Assert.Equal(new DateOnly(2026, 4, 1), aprilTransaction.Date);
        Assert.Equal(5m, aprilTransaction.Amount);
    }

    private static ExpenseDbContext CreateContext(bool createSchema = true)
    {
        var options = new DbContextOptionsBuilder<ExpenseDbContext>()
            .UseSqlite("Data Source=:memory:;Foreign Keys=True")
            .Options;
        var context = new ExpenseDbContext(options);
        context.Database.OpenConnection();
        if (createSchema)
        {
            context.Database.EnsureCreated();
        }
        return context;
    }

    private static Transaction CreateTransaction() => new()
    {
        Id = "TX-001",
        Date = new DateOnly(2026, 3, 2),
        Merchant = "Green Market",
        Description = "Weekly groceries",
        CategoryId = "groceries",
        Amount = 12.50m,
        Currency = "EUR"
    };
}