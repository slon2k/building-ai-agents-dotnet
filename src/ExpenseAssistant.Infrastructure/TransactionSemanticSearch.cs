namespace ExpenseAssistant.Infrastructure;

using System.Diagnostics;
using System.Text.Json;
using ExpenseAssistant.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public sealed class TransactionSemanticSearch(
    ExpenseDbContext dbContext,
    IEmbeddings embeddings,
    EmbeddingIndexConfiguration configuration,
    ILogger<TransactionSemanticSearch> logger
    ) : ITransactionSemanticSearch
{
    public const int MaxQueryLength = 2000;
    public const int MaxResultLimit = 100;

    public async Task<SemanticSearchResult> SearchAsync(
        string query,
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        string? categoryId = null,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(query);
            if (query.Length > MaxQueryLength)
            {
                throw new ArgumentOutOfRangeException(nameof(query), $"Query must not exceed {MaxQueryLength} characters.");
            }
            if (limit is < 1 or > MaxResultLimit)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), $"Limit must be between 1 and {MaxResultLimit}.");
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(configuration.IndexId);
            ArgumentException.ThrowIfNullOrWhiteSpace(configuration.ModelConfigurationId);

            List<(TransactionDto Transaction, float[] Vector)> eligible = [];
            int dimensions;
            // Read metadata and eligible vectors from the same database snapshot.
            await using (var snapshot = await dbContext.Database.BeginTransactionAsync(cancellationToken))
            {
                IQueryable<Transaction> sources = await TransactionFilters.CreateQueryAsync(
                    dbContext, startDate, endDate, categoryId, cancellationToken);
                EmbeddingIndexMetadata? metadata = await dbContext.EmbeddingIndexMetadata.AsNoTracking()
                    .SingleOrDefaultAsync(index => index.IndexId == configuration.IndexId, cancellationToken);
                if (metadata is null
                    || metadata.ModelConfigurationId != configuration.ModelConfigurationId
                    || metadata.TextConstructionVersion != TransactionExtensions.TextConstructionVersion
                    || metadata.Dimensions <= 0)
                {
                    throw new InvalidOperationException("The semantic index is missing or incompatible. Run --index-only.");
                }
                dimensions = metadata.Dimensions;
                var rows = await (
                    from source in sources
                    join vector in dbContext.TransactionEmbeddings.AsNoTracking()
                        .Where(vector => vector.IndexId == configuration.IndexId)
                        on source.Id equals vector.TransactionId into vectors
                    from vector in vectors.DefaultIfEmpty()
                    select new
                    {
                        Source = source,
                        CategoryName = source.Category.Name,
                        Hash = vector == null ? null : vector.SearchableTextHash,
                        Json = vector == null ? null : vector.VectorJson
                    }).ToListAsync(cancellationToken);

                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (row.Json is null || row.Hash != row.Source.SearchableTextHash)
                    {
                        throw new InvalidOperationException("An eligible transaction has a missing or stale embedding. Run --index-only.");
                    }
                    float[] vector;
                    try
                    {
                        vector = JsonSerializer.Deserialize<float[]>(row.Json)
                            ?? throw new InvalidOperationException("A stored embedding vector is null.");
                    }
                    catch (JsonException exception)
                    {
                        throw new InvalidOperationException("A stored embedding vector contains invalid JSON.", exception);
                    }
                    EmbeddingVector.Validate(vector, dimensions);
                    Transaction source = row.Source;
                    eligible.Add((new TransactionDto(source.Id, source.Date, source.Merchant, source.Description,
                        source.CategoryId, row.CategoryName, source.Amount, source.Currency), vector));
                }
                await snapshot.CommitAsync(cancellationToken);
            }

            List<SemanticSearchCandidate> scored = [];
            double embeddingDurationMs = 0;
            if (eligible.Count > 0)
            {
                long embeddingStarted = Stopwatch.GetTimestamp();
                ReadOnlyMemory<float> queryVector = await embeddings.GenerateAsync(query.Trim(), cancellationToken);
                embeddingDurationMs = Stopwatch.GetElapsedTime(embeddingStarted).TotalMilliseconds;
                EmbeddingVector.Validate(queryVector.Span, dimensions);
                foreach (var candidate in eligible)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scored.Add(new SemanticSearchCandidate(candidate.Transaction,
                        EmbeddingVector.CosineSimilarity(queryVector.Span, candidate.Vector)));
                }
            }
            SemanticSearchCandidate[] candidates = scored.OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Transaction.Id, StringComparer.Ordinal).Take(limit).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogInformation(
                "Semantic search completed IndexId={IndexId} ModelConfigurationId={ModelConfigurationId} TextConstructionVersion={TextConstructionVersion} Dimensions={Dimensions} EligibleCount={EligibleCount} RequestedLimit={RequestedLimit} ReturnedCount={ReturnedCount} EmbeddingDurationMs={EmbeddingDurationMs} DurationMs={DurationMs}",
                configuration.IndexId, configuration.ModelConfigurationId, TransactionExtensions.TextConstructionVersion,
                dimensions, eligible.Count, limit, candidates.Length, embeddingDurationMs,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new SemanticSearchResult(Array.AsReadOnly(candidates), startDate, endDate, categoryId, eligible.Count, limit);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Semantic search cancelled IndexId={IndexId}", configuration.IndexId);
            throw;
        }
        catch (Exception)
        {
            logger.LogError("Semantic search failed IndexId={IndexId} ErrorCode={ErrorCode}",
                configuration.IndexId, "SEMANTIC_SEARCH_FAILED");
            throw;
        }
    }
}