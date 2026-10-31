using System.Text.Json;
using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExpenseAssistant.Tests;

public class TransactionSemanticSearchTests
{
    [Fact]
    public async Task SearchAsync_FiltersBeforeRankingAndReturnsScoredFacts()
    {
        await using var context = await CreateContextAsync();
        var embeddings = new FakeEmbeddings([1, 0]);

        SemanticSearchResult result = await Search(context, embeddings).SearchAsync(
            "  remote work  ", new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1), "shopping", 1);

        SemanticSearchCandidate candidate = Assert.Single(result.Candidates);
        Assert.Equal("B", candidate.Transaction.Id);
        Assert.Equal(0.6, candidate.Score, 6);
        Assert.Equal("Shopping", candidate.Transaction.CategoryName);
        Assert.Equal(10m, candidate.Transaction.Amount);
        Assert.Equal("EUR", candidate.Transaction.Currency);
        Assert.Equal(2, result.EligibleRecordCount);
        Assert.Equal(1, result.ReturnedCount);
        Assert.Equal(1, result.RequestedLimit);
        Assert.True(result.HasMore);
        Assert.Equal(new DateOnly(2026, 3, 1), result.StartDate);
        Assert.Equal(new DateOnly(2026, 4, 1), result.EndDate);
        Assert.Equal("shopping", result.CategoryId);
        Assert.Equal("remote work", Assert.Single(embeddings.Inputs));
    }

    [Fact]
    public async Task SearchAsync_NoFiltersUsesAllRecordsAndBreaksTiesById()
    {
        await using var context = await CreateContextAsync();

        SemanticSearchResult result = await Search(context, new FakeEmbeddings([1, 0])).SearchAsync("desk", limit: 4);

        Assert.Equal(new[] { "A", "D", "B", "C" }, result.Candidates.Select(candidate => candidate.Transaction.Id));
        double[] expectedScores = [1, 1, 0.6, 0];
        for (int index = 0; index < expectedScores.Length; index++)
        {
            Assert.Equal(expectedScores[index], result.Candidates[index].Score, 6);
        }
        Assert.False(result.HasMore);
        Assert.Null(result.StartDate);
        Assert.Null(result.EndDate);
        Assert.Null(result.CategoryId);
    }

    [Fact]
    public async Task SearchAsync_ExactCategoryExcludesHigherScoringRecords()
    {
        await using var context = await CreateContextAsync();
        context.Categories.Add(new Category { Id = "groceries", Name = "Groceries" });
        Transaction source = Source("OTHER-CATEGORY", new DateOnly(2026, 3, 14));
        source.CategoryId = "groceries";
        context.Transactions.Add(source);
        context.TransactionEmbeddings.Add(Entry(source, [1, 0]));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        SemanticSearchResult result = await Search(context, new FakeEmbeddings([1, 0])).SearchAsync(
            "desk", new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1), "shopping");

        Assert.Equal(new[] { "B", "C" }, result.Candidates.Select(candidate => candidate.Transaction.Id));
        Assert.Equal(2, result.EligibleRecordCount);
        Assert.Equal(5, result.RequestedLimit);
    }

    [Fact]
    public async Task SearchAsync_DatabaseFailurePropagatesWithoutEmbeddingCall()
    {
        await using var context = await CreateContextAsync();
        await context.Database.ExecuteSqlRawAsync("DROP TABLE TransactionEmbeddings;");
        var embeddings = new FakeEmbeddings([1, 0]);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            Search(context, embeddings).SearchAsync("desk"));

        Assert.Empty(embeddings.Inputs);
    }

    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(0, 1, 0)]
    [InlineData(-1, 0, -1)]
    [InlineData(3, 4, 0.6)]
    public async Task SearchAsync_CalculatesCosineForNonNormalizedVectors(float x, float y, double expected)
    {
        await using var context = await CreateContextAsync();

        SemanticSearchResult result = await Search(context, new FakeEmbeddings([x, y]))
            .SearchAsync("desk", endDate: new DateOnly(2026, 3, 1));

        Assert.Equal("A", Assert.Single(result.Candidates).Transaction.Id);
        Assert.Equal(expected, result.Candidates[0].Score, 6);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SearchAsync_SupportsOneSidedDateFilters(bool startOnly)
    {
        await using var context = await CreateContextAsync();
        var search = Search(context, new FakeEmbeddings([1, 0]));
        var boundary = new DateOnly(2026, 3, 31);

        SemanticSearchResult result = startOnly
            ? await search.SearchAsync("desk", startDate: boundary)
            : await search.SearchAsync("desk", endDate: boundary);

        Assert.Equal(startOnly ? new[] { "D", "C" } : new[] { "A", "B" },
            result.Candidates.Select(candidate => candidate.Transaction.Id));
    }

    [Fact]
    public async Task SearchAsync_DoesNotTruncateBeforeSimilarityScoring()
    {
        await using var context = await CreateContextAsync();
        for (int index = 0; index < 120; index++)
        {
            Transaction source = Source($"EXTRA-{index:000}", new DateOnly(2026, 3, 2));
            context.Transactions.Add(source);
            context.TransactionEmbeddings.Add(Entry(source, [0, 1]));
        }
        Transaction best = Source("Z-BEST", new DateOnly(2026, 3, 30));
        context.Transactions.Add(best);
        context.TransactionEmbeddings.Add(Entry(best, [1, 0]));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        SemanticSearchResult result = await Search(context, new FakeEmbeddings([1, 0]))
            .SearchAsync("desk", new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1), limit: 1);

        Assert.Equal("Z-BEST", Assert.Single(result.Candidates).Transaction.Id);
        Assert.Equal(123, result.EligibleRecordCount);
    }

    [Fact]
    public async Task SearchAsync_EmptyEligibleSetSkipsProvider()
    {
        await using var context = await CreateContextAsync();
        var embeddings = new FakeEmbeddings([1, 0]);

        SemanticSearchResult result = await Search(context, embeddings).SearchAsync(
            "desk", endDate: new DateOnly(2025, 1, 1));

        Assert.Empty(result.Candidates);
        Assert.Equal(0, result.EligibleRecordCount);
        Assert.False(result.HasMore);
        Assert.Empty(embeddings.Inputs);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task SearchAsync_RejectsBlankQuery(string? query)
    {
        await using var context = await CreateContextAsync();
        var embeddings = new FakeEmbeddings([1, 0]);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Search(context, embeddings).SearchAsync(query!));

        Assert.Empty(embeddings.Inputs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task SearchAsync_RejectsInvalidLimit(int limit)
    {
        await using var context = await CreateContextAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Search(context, new FakeEmbeddings([1, 0])).SearchAsync("desk", limit: limit));
    }

    [Fact]
    public async Task SearchAsync_ValidatesQueryLengthBoundary()
    {
        await using var context = await CreateContextAsync();
        var search = Search(context, new FakeEmbeddings([1, 0]));

        await search.SearchAsync(new string('a', TransactionSemanticSearch.MaxQueryLength));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            search.SearchAsync(new string('a', TransactionSemanticSearch.MaxQueryLength + 1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown")]
    public async Task SearchAsync_RejectsInvalidCategory(string category)
    {
        await using var context = await CreateContextAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            Search(context, new FakeEmbeddings([1, 0])).SearchAsync("desk", categoryId: category));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SearchAsync_RejectsInvalidDateRange(int startDay)
    {
        await using var context = await CreateContextAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => Search(context, new FakeEmbeddings([1, 0]))
            .SearchAsync("desk", new DateOnly(2026, 3, startDay), new DateOnly(2026, 3, 1)));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("model")]
    [InlineData("text")]
    [InlineData("dimensions")]
    public async Task SearchAsync_RejectsMissingOrIncompatibleMetadataBeforeProvider(string kind)
    {
        await using var context = await CreateContextAsync();
        EmbeddingIndexMetadata metadata = await context.EmbeddingIndexMetadata.SingleAsync();
        switch (kind)
        {
            case "missing": context.EmbeddingIndexMetadata.Remove(metadata); break;
            case "model": metadata.ModelConfigurationId = "other-model"; break;
            case "text": metadata.TextConstructionVersion = "other-format"; break;
            default: metadata.Dimensions = 0; break;
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var embeddings = new FakeEmbeddings([1, 0]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Search(context, embeddings).SearchAsync("desk"));

        Assert.Empty(embeddings.Inputs);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("json")]
    [InlineData("null")]
    [InlineData("zero")]
    [InlineData("empty")]
    [InlineData("dimensions")]
    [InlineData("infinite")]
    public async Task SearchAsync_RejectsInvalidEligibleEmbeddingBeforeProvider(string kind)
    {
        await using var context = await CreateContextAsync();
        TransactionEmbeddings vector = await context.TransactionEmbeddings.SingleAsync(entry => entry.TransactionId == "B");
        if (kind == "missing")
        {
            context.TransactionEmbeddings.Remove(vector);
        }
        else if (kind == "stale")
        {
            vector.SearchableTextHash = "old";
        }
        else
        {
            vector.VectorJson = kind switch
            {
                "json" => "{",
                "null" => "null",
                "zero" => "[0,0]",
                "empty" => "[]",
                "dimensions" => "[1,0,0]",
                _ => "[1e100,1]"
            };
        }
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var embeddings = new FakeEmbeddings([1, 0]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Search(context, embeddings).SearchAsync("desk"));

        Assert.Empty(embeddings.Inputs);
    }

    [Fact]
    public async Task SearchAsync_OnlyValidatesEligibleVectorsAndIgnoresFixtureVersion()
    {
        await using var context = await CreateContextAsync();
        await context.TransactionEmbeddings.Where(entry => entry.TransactionId == "A").ExecuteDeleteAsync();
        await context.EmbeddingIndexMetadata.ExecuteUpdateAsync(setters => setters.SetProperty(index => index.DatasetVersion, "v1"));

        SemanticSearchResult result = await Search(context, new FakeEmbeddings([1, 0]))
            .SearchAsync("desk", startDate: new DateOnly(2026, 3, 1));

        Assert.Equal(3, result.EligibleRecordCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SearchAsync_RejectsInvalidQueryVector(int kind)
    {
        await using var context = await CreateContextAsync();
        float[] vector = kind switch { 0 => [], 1 => [0, 0], 2 => [1, 0, 0], _ => [float.NaN, 1] };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Search(context, new FakeEmbeddings(vector)).SearchAsync("desk"));
    }

    [Fact]
    public async Task SearchAsync_DoesNotMutateDatabaseOnSuccessOrProviderFailure()
    {
        await using var context = await CreateContextAsync();
        string before = await FingerprintAsync(context);
        var embeddings = new FakeEmbeddings([1, 0]);
        var search = Search(context, embeddings);
        await search.SearchAsync("desk");
        embeddings.Failure = new InvalidOperationException("Provider unavailable");

        await Assert.ThrowsAsync<InvalidOperationException>(() => search.SearchAsync("desk"));

        Assert.Equal(before, await FingerprintAsync(context));
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task SearchAsync_PropagatesCancellationBeforeAndDuringEmbedding()
    {
        await using var context = await CreateContextAsync();
        using var cancellation = new CancellationTokenSource();
        var embeddings = new FakeEmbeddings([1, 0]) { Cancel = cancellation };
        var search = Search(context, embeddings);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.SearchAsync("desk", cancellationToken: cancellation.Token));
        Assert.Single(embeddings.Inputs);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.SearchAsync("desk", cancellationToken: cancellation.Token));
        Assert.Single(embeddings.Inputs);
    }

    [Fact]
    public void EmbeddingIndexConfiguration_MatchesExistingIdentityFormat()
    {
        var options = new AzureOpenAIOptions
        {
            Endpoint = "https://test.invalid", ApiKey = "test-only", EmbeddingDeployment = "embed-small"
        };
        EmbeddingIndexConfiguration config = EmbeddingIndexConfiguration.Create(options, "expenses-v1", "v1");

        Assert.Equal("expenses-v1", config.IndexId);
        Assert.Equal("""{"Provider":"azure-openai","Endpoint":"https://test.invalid/","Deployment":"embed-small","Version":"v1"}""",
            config.ModelConfigurationId);
        Assert.DoesNotContain("test-only", config.ModelConfigurationId);
        Assert.NotEqual(config.ModelConfigurationId, EmbeddingIndexConfiguration.Create(options, "expenses-v1", "v2").ModelConfigurationId);
    }

    private static TransactionSemanticSearch Search(ExpenseDbContext context, IEmbeddings embeddings) =>
        new(context, embeddings, new EmbeddingIndexConfiguration("test-index", "model-v1"),
            NullLogger<TransactionSemanticSearch>.Instance);

    private static Transaction Source(string id, DateOnly date) => new()
    {
        Id = id, Date = date, Merchant = "DeskSupply", Description = $"Purchase {id}", CategoryId = "shopping",
        Amount = 10m, Currency = "EUR"
    };

    private static TransactionEmbeddings Entry(Transaction source, float[] vector) => new()
    {
        IndexId = "test-index", TransactionId = source.Id, SearchableTextHash = source.SearchableTextHash,
        VectorJson = JsonSerializer.Serialize(vector)
    };

    private static async Task<ExpenseDbContext> CreateContextAsync()
    {
        var context = new ExpenseDbContext(new DbContextOptionsBuilder<ExpenseDbContext>()
            .UseSqlite("Data Source=:memory:;Foreign Keys=True").Options);
        await context.Database.OpenConnectionAsync();
        await context.Database.MigrateAsync();
        context.Categories.Add(new Category { Id = "shopping", Name = "Shopping" });
        context.EmbeddingIndexMetadata.Add(new EmbeddingIndexMetadata
        {
            IndexId = "test-index", ModelConfigurationId = "model-v1", Dimensions = 2,
            TextConstructionVersion = TransactionExtensions.TextConstructionVersion, DatasetVersion = "v2"
        });
        Transaction[] sources =
        [
            Source("A", new DateOnly(2026, 2, 28)),
            Source("B", new DateOnly(2026, 3, 1)),
            Source("C", new DateOnly(2026, 3, 31)),
            Source("D", new DateOnly(2026, 4, 1))
        ];
        context.Transactions.AddRange(sources);
        context.TransactionEmbeddings.AddRange(Entry(sources[0], [2, 0]), Entry(sources[1], [3, 4]),
            Entry(sources[2], [0, 1]), Entry(sources[3], [1, 0]));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return context;
    }

    private static async Task<string> FingerprintAsync(ExpenseDbContext context) =>
        JsonSerializer.Serialize(new
        {
            Transactions = await context.Transactions.AsNoTracking().OrderBy(source => source.Id).ToListAsync(),
            Entries = await context.TransactionEmbeddings.AsNoTracking().OrderBy(entry => entry.TransactionId).ToListAsync(),
            Metadata = await context.EmbeddingIndexMetadata.AsNoTracking().ToListAsync()
        });

    private sealed class FakeEmbeddings(float[] vector) : IEmbeddings
    {
        public List<string> Inputs { get; } = [];
        public Exception? Failure { get; set; }
        public CancellationTokenSource? Cancel { get; set; }

        public Task<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inputs.Add(text);
            Cancel?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            return Task.FromResult<ReadOnlyMemory<float>>(vector);
        }
    }

}
