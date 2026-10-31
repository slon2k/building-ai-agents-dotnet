using System.Text.Json;
using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExpenseAssistant.Tests;

public class TransactionIndexPopulatorTests
{
    [Fact]
    public async Task PopulateAsync_PersistsFullFixtureAndReusesUnchangedVectors()
    {
        await using var context = await CreateContextAsync();
        var embeddings = new FakeEmbeddings();
        var populator = CreatePopulator(context, embeddings);

        IndexPopulationResult first = await populator.PopulateAsync("test-index", "model-v1");
        IndexPopulationResult second = await populator.PopulateAsync("test-index", "model-v1");

        Assert.Equal(200, first.GeneratedCount);
        Assert.Equal(0, first.ReusedCount);
        Assert.Equal(0, second.GeneratedCount);
        Assert.Equal(200, second.ReusedCount);
        Assert.Equal(200, embeddings.Calls);
        Assert.Equal(2, second.Dimensions);
        Assert.Equal(200, await context.TransactionEmbeddings.CountAsync());
        EmbeddingIndexMetadata metadata = await context.EmbeddingIndexMetadata.SingleAsync();
        Assert.Equal("model-v1", metadata.ModelConfigurationId);
        Assert.Equal(TransactionExtensions.TextConstructionVersion, metadata.TextConstructionVersion);
        Assert.Equal(ExpenseDatabaseInitializer.DatasetVersion, metadata.DatasetVersion);
        TransactionEmbeddings entry = await context.TransactionEmbeddings.FirstAsync();
        Assert.Equal(64, entry.SearchableTextHash.Length);
        Assert.Equal(new float[] { 1, 0 }, JsonSerializer.Deserialize<float[]>(entry.VectorJson));
        Assert.Contains("Merchant: CloudDesk; Description: Shared document editing subscription", embeddings.Inputs);
    }

    [Fact]
    public async Task PopulateAsync_OnlyReembedsChangedSearchableText()
    {
        await using var context = await CreateContextAsync();
        var embeddings = new FakeEmbeddings();
        var populator = CreatePopulator(context, embeddings);
        await populator.PopulateAsync("test-index", "model-v1");
        Transaction source = await context.Transactions.SingleAsync(entity => entity.Id == "TX-G1");
        source.Description = "Changed description";
        Transaction other = await context.Transactions.SingleAsync(entity => entity.Id == "TX-G2");
        other.Amount = 10m;
        other.Date = new DateOnly(2026, 3, 15);
        other.CategoryId = "shopping";
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        IndexPopulationResult result = await populator.PopulateAsync("test-index", "model-v1");

        Assert.Equal(1, result.GeneratedCount);
        Assert.Equal(199, result.ReusedCount);
        Assert.Equal(201, embeddings.Calls);
        Assert.Equal(10m, (await context.Transactions.SingleAsync(entity => entity.Id == "TX-G2")).Amount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PopulateAsync_IncompatibleConfigurationRebuildsWholeIndex(bool changeModel)
    {
        await using var context = await CreateContextAsync();
        var embeddings = new FakeEmbeddings();
        var populator = CreatePopulator(context, embeddings);
        await populator.PopulateAsync("test-index", "model-v1");
        if (!changeModel)
        {
            await context.EmbeddingIndexMetadata.ExecuteUpdateAsync(setters =>
                setters.SetProperty(entity => entity.TextConstructionVersion, "old-text-format"));
        }
        embeddings.Vector = [1, 0, 0];

        IndexPopulationResult result = await populator.PopulateAsync("test-index", changeModel ? "model-v2" : "model-v1");

        Assert.Equal(200, result.GeneratedCount);
        Assert.Equal(0, result.ReusedCount);
        Assert.Equal(3, result.Dimensions);
        Assert.Equal(3, (await context.EmbeddingIndexMetadata.SingleAsync()).Dimensions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PopulateAsync_FailureOrCancellationPreservesPreviousIndex(bool cancel)
    {
        await using var context = await CreateContextAsync();
        var embeddings = new FakeEmbeddings();
        var populator = CreatePopulator(context, embeddings);
        await populator.PopulateAsync("test-index", "model-v1");
        List<string> before = await context.TransactionEmbeddings.OrderBy(entity => entity.TransactionId)
            .Select(entity => entity.VectorJson).ToListAsync();
        embeddings.Failure = cancel ? new OperationCanceledException() : new InvalidOperationException("Test failure");
        embeddings.FailOnCall = 202;

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => populator.PopulateAsync("test-index", "model-v2"));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => populator.PopulateAsync("test-index", "model-v2"));
        }

        Assert.Equal("model-v1", (await context.EmbeddingIndexMetadata.SingleAsync()).ModelConfigurationId);
        Assert.Equal(before, await context.TransactionEmbeddings.OrderBy(entity => entity.TransactionId)
            .Select(entity => entity.VectorJson).ToListAsync());
        Assert.Equal(200, await context.Transactions.CountAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PopulateAsync_InvalidGeneratedVectorDoesNotPublishIndex(int kind)
    {
        await using var context = await CreateContextAsync();
        var embeddings = new FakeEmbeddings
        {
            Vector = kind switch { 0 => [], 1 => [0, 0], _ => [float.NaN, 1] }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreatePopulator(context, embeddings).PopulateAsync("test-index", "model-v1"));

        Assert.Empty(await context.EmbeddingIndexMetadata.ToListAsync());
        Assert.Empty(await context.TransactionEmbeddings.ToListAsync());
    }

    [Fact]
    public async Task PopulateAsync_DimensionMismatchDoesNotPublishIndex()
    {
        await using var context = await CreateContextAsync();
        var embeddings = new FakeEmbeddings { ChangeDimensionsOnCall = 2 };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreatePopulator(context, embeddings).PopulateAsync("test-index", "model-v1"));

        Assert.Empty(await context.EmbeddingIndexMetadata.ToListAsync());
        Assert.Empty(await context.TransactionEmbeddings.ToListAsync());
    }

    [Fact]
    public async Task PopulateAsync_CorruptStoredVectorFailsExplicitly()
    {
        await using var context = await CreateContextAsync();
        var embeddings = new FakeEmbeddings();
        var populator = CreatePopulator(context, embeddings);
        await populator.PopulateAsync("test-index", "model-v1");
        await context.TransactionEmbeddings.Where(entity => entity.TransactionId == "TX-G1")
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.VectorJson, "[0,0]"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => populator.PopulateAsync("test-index", "model-v1"));

        Assert.Equal(200, embeddings.Calls);
    }

    [Fact]
    public async Task PopulateAsync_AddsMissingEmbeddingAndLeavesOtherIndexesUntouched()
    {
        await using var context = await CreateContextAsync();
        var embeddings = new FakeEmbeddings();
        var populator = CreatePopulator(context, embeddings);
        await populator.PopulateAsync("first", "model-v1");
        await populator.PopulateAsync("second", "model-v2");
        await context.TransactionEmbeddings.Where(entity => entity.IndexId == "first" && entity.TransactionId == "TX-G1")
            .ExecuteDeleteAsync();

        IndexPopulationResult result = await populator.PopulateAsync("first", "model-v1");

        Assert.Equal(1, result.GeneratedCount);
        Assert.Equal(199, result.ReusedCount);
        Assert.Equal(400, await context.TransactionEmbeddings.CountAsync());
        Assert.Equal("model-v2", (await context.EmbeddingIndexMetadata.SingleAsync(entity => entity.IndexId == "second")).ModelConfigurationId);
    }

    [Fact]
    public async Task PopulateAsync_DeletedTransactionDoesNotLeaveOrphanEmbedding()
    {
        await using var context = await CreateContextAsync();
        var embeddings = new FakeEmbeddings();
        var populator = CreatePopulator(context, embeddings);
        await populator.PopulateAsync("test-index", "model-v1");
        await context.Transactions.Where(entity => entity.Id == "TX-G1").ExecuteDeleteAsync();

        IndexPopulationResult result = await populator.PopulateAsync("test-index", "model-v1");

        Assert.Equal(199, result.ReusedCount);
        Assert.Equal(0, result.GeneratedCount);
        Assert.Equal(199, await context.TransactionEmbeddings.CountAsync());
    }

    [Fact]
    public async Task PopulateAsync_EmptyDatasetCannotCreateUnknownDimensionIndex()
    {
        await using var context = await CreateContextAsync();
        await context.Transactions.ExecuteDeleteAsync();
        var embeddings = new FakeEmbeddings();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreatePopulator(context, embeddings).PopulateAsync("test-index", "model-v1"));

        Assert.Equal(0, embeddings.Calls);
    }

    private static TransactionIndexPopulator CreatePopulator(ExpenseDbContext context, IEmbeddings embeddings) =>
        new(context, embeddings, NullLogger<TransactionIndexPopulator>.Instance);

    private static async Task<ExpenseDbContext> CreateContextAsync()
    {
        var context = new ExpenseDbContext(new DbContextOptionsBuilder<ExpenseDbContext>()
            .UseSqlite("Data Source=:memory:;Foreign Keys=True").Options);
        await context.Database.OpenConnectionAsync();
        await ExpenseDatabaseInitializer.InitializeAsync(context);
        context.ChangeTracker.Clear();
        return context;
    }

    private sealed class FakeEmbeddings : IEmbeddings
    {
        public int Calls { get; private set; }
        public List<string> Inputs { get; } = [];
        public float[] Vector { get; set; } = [1, 0];
        public Exception? Failure { get; set; }
        public int FailOnCall { get; set; }
        public int ChangeDimensionsOnCall { get; set; }

        public Task<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Inputs.Add(text);
            if (Failure is not null && Calls == FailOnCall)
            {
                throw Failure;
            }
            return Task.FromResult<ReadOnlyMemory<float>>(Calls == ChangeDimensionsOnCall ? new float[] { 1, 0, 0 } : Vector);
        }
    }
}
