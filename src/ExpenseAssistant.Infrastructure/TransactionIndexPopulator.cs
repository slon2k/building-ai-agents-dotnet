using System.Diagnostics;
using System.Text.Json;
using ExpenseAssistant.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExpenseAssistant.Infrastructure;

public sealed record IndexPopulationResult(string IndexId, int GeneratedCount, int ReusedCount, int RemovedCount, int Dimensions);

public sealed class TransactionIndexPopulator(
    ExpenseDbContext context,
    IEmbeddings embeddings,
    ILogger<TransactionIndexPopulator> logger)
{
    public async Task<IndexPopulationResult> PopulateAsync(
        string indexId,
        string modelConfigurationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelConfigurationId);
        long started = Stopwatch.GetTimestamp();
        try
        {
            // Keep a stable source snapshot and publish all changes in one transaction.
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            List<Transaction> sources = await context.Transactions.AsNoTracking()
                .OrderBy(entity => entity.Id).ToListAsync(cancellationToken);
            EmbeddingIndexMetadata? metadata = await context.EmbeddingIndexMetadata.AsNoTracking()
                .SingleOrDefaultAsync(entity => entity.IndexId == indexId, cancellationToken);
            Dictionary<string, TransactionEmbeddings> stored = await context.TransactionEmbeddings.AsNoTracking()
                .Where(entity => entity.IndexId == indexId)
                .ToDictionaryAsync(entity => entity.TransactionId, cancellationToken);
            bool compatible = metadata is not null
                && metadata.ModelConfigurationId == modelConfigurationId
                && metadata.TextConstructionVersion == TransactionExtensions.TextConstructionVersion;
            if (compatible && metadata!.Dimensions <= 0)
            {
                throw new InvalidOperationException("The existing index has invalid dimensions.");
            }

            int? dimensions = compatible ? metadata!.Dimensions : null;
            int generated = 0;
            int reused = 0;
            List<TransactionEmbeddings> entries = [];
            foreach (Transaction source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string text = source.BuildSearchableText;
                string hash = source.SearchableTextHash;
                string json;
                if (compatible && stored.TryGetValue(source.Id, out TransactionEmbeddings? existing)
                    && existing.SearchableTextHash == hash)
                {
                    float[] vector = JsonSerializer.Deserialize<float[]>(existing.VectorJson)
                        ?? throw new InvalidOperationException("The stored embedding vector is null.");
                    EmbeddingVector.Validate(vector, dimensions);
                    json = existing.VectorJson;
                    reused++;
                }
                else
                {
                    ReadOnlyMemory<float> vector = await embeddings.GenerateAsync(text, cancellationToken);
                    EmbeddingVector.Validate(vector.Span, dimensions);
                    dimensions ??= vector.Length;
                    json = JsonSerializer.Serialize(vector.ToArray());
                    generated++;
                }
                entries.Add(new TransactionEmbeddings
                {
                    IndexId = indexId,
                    TransactionId = source.Id,
                    SearchableTextHash = hash,
                    VectorJson = json
                });
            }

            if (dimensions is null)
            {
                throw new InvalidOperationException("Cannot determine embedding dimensions for an empty dataset.");
            }

            await context.TransactionEmbeddings.Where(entity => entity.IndexId == indexId)
                .ExecuteDeleteAsync(cancellationToken);
            await context.EmbeddingIndexMetadata.Where(entity => entity.IndexId == indexId)
                .ExecuteDeleteAsync(cancellationToken);
            context.EmbeddingIndexMetadata.Add(new EmbeddingIndexMetadata
            {
                IndexId = indexId,
                ModelConfigurationId = modelConfigurationId,
                Dimensions = dimensions.Value,
                TextConstructionVersion = TransactionExtensions.TextConstructionVersion,
                DatasetVersion = ExpenseDatabaseInitializer.DatasetVersion
            });
            context.TransactionEmbeddings.AddRange(entries);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            int removed = stored.Keys.Except(sources.Select(source => source.Id), StringComparer.Ordinal).Count();
            logger.LogInformation(
                "Index population completed IndexId={IndexId} Generated={Generated} Reused={Reused} Removed={Removed} Dimensions={Dimensions} DurationMs={DurationMs}",
                indexId, generated, reused, removed, dimensions.Value, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new IndexPopulationResult(indexId, generated, reused, removed, dimensions.Value);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Index population cancelled IndexId={IndexId}", indexId);
            throw;
        }
        catch (Exception)
        {
            logger.LogError("Index population failed IndexId={IndexId} ErrorCode={ErrorCode}", indexId, "INDEX_POPULATION_FAILED");
            throw;
        }
        finally
        {
            // This operation owns its short-lived context; do not retain rolled-back entities.
            context.ChangeTracker.Clear();
        }
    }
}
