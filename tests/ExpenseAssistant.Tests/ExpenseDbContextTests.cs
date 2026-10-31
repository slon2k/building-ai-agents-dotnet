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
        Assert.Equal(200, transactions.Count);
        Assert.Equal("v2", ExpenseDatabaseInitializer.DatasetVersion);
        Assert.Equal(200, transactions.Select(transaction => transaction.Id).Distinct().Count());
        Assert.Equal(83, transactions.Select(transaction => transaction.Description).Distinct().Count());
        Assert.All(transactions, transaction =>
        {
            Assert.Equal("EUR", transaction.Currency);
            Assert.True(transaction.Amount > 0);
            Assert.Equal(transaction.Amount, decimal.Round(transaction.Amount, 2));
            Assert.InRange(transaction.Date, new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));
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
        transaction.Description = "Edited description";
        Category category = await context.Categories.SingleAsync(entity => entity.Id == "groceries");
        category.Name = "Food shopping";
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await ExpenseDatabaseInitializer.InitializeAsync(context);

        Assert.Equal(200, await context.Transactions.CountAsync());
        Assert.Equal(8, await context.Categories.CountAsync());
        Assert.Equal("shopping", (await context.Transactions.SingleAsync(entity => entity.Id == "TX-G1")).CategoryId);
        Assert.Equal("Edited description", (await context.Transactions.SingleAsync(entity => entity.Id == "TX-G1")).Description);
        Assert.Equal("Food shopping", (await context.Categories.SingleAsync(entity => entity.Id == "groceries")).Name);
    }

    [Fact]
    public async Task InitializeAsync_PreservesLegacyRecordsWithoutReusingTheirIds()
    {
        await using var context = CreateContext();
        context.Categories.Add(new Category { Id = "groceries", Name = "Groceries" });
        Transaction legacy = CreateTransaction();
        legacy.Description = "Preserved legacy purchase";
        context.Transactions.Add(legacy);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await ExpenseDatabaseInitializer.InitializeAsync(context);
        await ExpenseDatabaseInitializer.InitializeAsync(context);

        Assert.Equal(201, await context.Transactions.CountAsync());
        Assert.Equal("Preserved legacy purchase",
            (await context.Transactions.SingleAsync(transaction => transaction.Id == "TX-001")).Description);
    }

    [Fact]
    public async Task InitializeAsync_ContainsVariedSemanticExamplesAndDateBoundaries()
    {
        await using var context = CreateContext();
        await ExpenseDatabaseInitializer.InitializeAsync(context);
        List<Transaction> transactions = await context.Transactions.AsNoTracking().ToListAsync();

        Assert.Equal(97, transactions.Count(transaction => transaction.Id.StartsWith("TX-E", StringComparison.Ordinal)));
        Assert.Equal(50, transactions.Count(transaction => transaction.Id.StartsWith("TX-C", StringComparison.Ordinal)));
        Assert.Equal(25, transactions.Count(transaction => transaction.Id.StartsWith("TX-A", StringComparison.Ordinal)));
        Assert.Equal(25, transactions.Count(transaction => transaction.Id.StartsWith("TX-D", StringComparison.Ordinal)));
        Assert.Equal(new DateOnly(2026, 3, 1), transactions.Single(transaction => transaction.Id == "TX-C001").Date);
        Assert.Equal(new DateOnly(2026, 3, 31), transactions.Single(transaction => transaction.Id == "TX-C002").Date);
        Assert.Equal(new DateOnly(2026, 4, 1), transactions.Single(transaction => transaction.Id == "TX-C003").Date);
        Assert.Contains(transactions, transaction => transaction.Description == "Remote-control racing car");
        Assert.Contains(transactions, transaction => transaction.Description == "Monthly subscription");
        Assert.True(transactions.Where(transaction => transaction.Merchant == "Central Store")
            .Select(transaction => transaction.Description).Distinct().Count() >= 5);
        Assert.True(transactions.Count(transaction =>
            transaction.Description == "Shared document editing subscription") >= 2);
    }

    [Fact]
    public async Task InitializeAsync_IsReproducibleAcrossFreshDatabases()
    {
        await using var first = CreateContext();
        await using var second = CreateContext();
        await ExpenseDatabaseInitializer.InitializeAsync(first);
        await ExpenseDatabaseInitializer.InitializeAsync(second);

        var firstRecords = await first.Transactions.OrderBy(transaction => transaction.Id)
            .Select(transaction => new { transaction.Id, transaction.Date, transaction.CategoryId,
                transaction.Merchant, transaction.Description, transaction.Amount, transaction.Currency })
            .ToListAsync();
        var secondRecords = await second.Transactions.OrderBy(transaction => transaction.Id)
            .Select(transaction => new { transaction.Id, transaction.Date, transaction.CategoryId,
                transaction.Merchant, transaction.Description, transaction.Amount, transaction.Currency })
            .ToListAsync();

        Assert.Equal(firstRecords, secondRecords);
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

    [Fact]
    public async Task SaveChangesAsync_PersistsEmbeddingsForMultipleIndexes()
    {
        await using var context = CreateContext();
        context.Categories.Add(new Category { Id = "groceries", Name = "Groceries" });
        context.Transactions.Add(CreateTransaction());
        context.EmbeddingIndexMetadata.AddRange(CreateIndex("index-1"), CreateIndex("index-2"));
        context.TransactionEmbeddings.AddRange(CreateEmbedding("index-1"), CreateEmbedding("index-2"));

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        EmbeddingIndexMetadata index = await context.EmbeddingIndexMetadata.SingleAsync(entity => entity.IndexId == "index-1");
        Assert.Equal("test-model", index.ModelConfigurationId);
        Assert.Equal(2, index.Dimensions);
        Assert.Equal("merchant-description-v1", index.TextConstructionVersion);
        Assert.Equal(ExpenseDatabaseInitializer.DatasetVersion, index.DatasetVersion);
        Assert.Equal(2, await context.TransactionEmbeddings.CountAsync());
        TransactionEmbeddings embedding = await context.TransactionEmbeddings.SingleAsync(entity => entity.IndexId == "index-1");
        Assert.Equal("TX-001", embedding.TransactionId);
        Assert.Equal("text-hash", embedding.SearchableTextHash);
        Assert.Equal("[1,0]", embedding.VectorJson);

        context.ChangeTracker.Clear();
        context.TransactionEmbeddings.Add(CreateEmbedding("index-1"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveChangesAsync_RejectsEmbeddingWithMissingParent(bool missingIndex)
    {
        await using var context = CreateContext();
        if (missingIndex)
        {
            context.Categories.Add(new Category { Id = "groceries", Name = "Groceries" });
            context.Transactions.Add(CreateTransaction());
        }
        else
        {
            context.EmbeddingIndexMetadata.Add(CreateIndex("index-1"));
        }
        await context.SaveChangesAsync();
        context.TransactionEmbeddings.Add(CreateEmbedding("index-1"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveChangesAsync_DeletingParentCascadesOnlyToEmbeddings(bool deleteIndex)
    {
        await using var context = CreateContext();
        context.Categories.Add(new Category { Id = "groceries", Name = "Groceries" });
        context.Transactions.Add(CreateTransaction());
        context.EmbeddingIndexMetadata.Add(CreateIndex("index-1"));
        context.TransactionEmbeddings.Add(CreateEmbedding("index-1"));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        if (deleteIndex)
        {
            context.EmbeddingIndexMetadata.Remove(await context.EmbeddingIndexMetadata.SingleAsync());
        }
        else
        {
            context.Transactions.Remove(await context.Transactions.SingleAsync());
        }
        await context.SaveChangesAsync();

        Assert.Empty(await context.TransactionEmbeddings.ToListAsync());
        Assert.Equal(deleteIndex ? 1 : 0, await context.Transactions.CountAsync());
        Assert.Equal(deleteIndex ? 0 : 1, await context.EmbeddingIndexMetadata.CountAsync());
        Assert.Equal(1, await context.Categories.CountAsync());
    }

    private static EmbeddingIndexMetadata CreateIndex(string indexId) => new()
    {
        IndexId = indexId,
        ModelConfigurationId = "test-model",
        Dimensions = 2,
        TextConstructionVersion = "merchant-description-v1",
        DatasetVersion = ExpenseDatabaseInitializer.DatasetVersion
    };

    private static TransactionEmbeddings CreateEmbedding(string indexId) => new()
    {
        IndexId = indexId,
        TransactionId = "TX-001",
        SearchableTextHash = "text-hash",
        VectorJson = "[1,0]"
    };

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