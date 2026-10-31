using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ExpenseAssistant.Tests;

public class ExpenseDatabaseMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeAsync_AdoptsEnsureCreatedDatabaseAndPreservesRecords(bool hasEmbeddings)
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        context.Categories.Add(new Category { Id = "shopping", Name = "Edited category" });
        context.Transactions.Add(new Transaction
        {
            Id = "CUSTOM-1", Date = new DateOnly(2026, 1, 1), Merchant = "Test store",
            Description = "Preserved purchase", CategoryId = "shopping", Amount = 10m, Currency = "EUR"
        });
        if (hasEmbeddings)
        {
            context.EmbeddingIndexMetadata.Add(new EmbeddingIndexMetadata
            {
                IndexId = "old-index", ModelConfigurationId = "old-model", Dimensions = 2,
                TextConstructionVersion = "old-text", DatasetVersion = "v1"
            });
            context.TransactionEmbeddings.Add(new TransactionEmbeddings
            {
                IndexId = "old-index", TransactionId = "CUSTOM-1", SearchableTextHash = "old-hash", VectorJson = "[1,0]"
            });
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        if (!hasEmbeddings)
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE TransactionEmbeddings; DROP TABLE EmbeddingIndexMetadata;");
        }

        await ExpenseDatabaseInitializer.InitializeAsync(context);
        await ExpenseDatabaseInitializer.InitializeAsync(context);

        Assert.Single(await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(201, await context.Transactions.CountAsync());
        Assert.Equal("Preserved purchase", (await context.Transactions.SingleAsync(entity => entity.Id == "CUSTOM-1")).Description);
        Assert.Equal("Edited category", (await context.Categories.SingleAsync(entity => entity.Id == "shopping")).Name);
        Assert.Equal(hasEmbeddings ? 1 : 0, await context.TransactionEmbeddings.CountAsync());
        Assert.Equal(hasEmbeddings ? 1 : 0, await context.EmbeddingIndexMetadata.CountAsync());
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task InitializeAsync_MigratesFreshDatabase()
    {
        await using var context = CreateContext();

        await ExpenseDatabaseInitializer.InitializeAsync(context);

        Assert.Single(await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(200, await context.Transactions.CountAsync());
        Assert.Empty(await context.TransactionEmbeddings.ToListAsync());
        Assert.Empty(await context.EmbeddingIndexMetadata.ToListAsync());
    }

    private static ExpenseDbContext CreateContext()
    {
        var context = new ExpenseDbContext(new DbContextOptionsBuilder<ExpenseDbContext>()
            .UseSqlite("Data Source=:memory:;Foreign Keys=True").Options);
        context.Database.OpenConnection();
        return context;
    }
}
