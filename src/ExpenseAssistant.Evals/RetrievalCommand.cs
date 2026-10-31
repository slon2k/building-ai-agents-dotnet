using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExpenseAssistant.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExpenseAssistant.Evals;

public static class RetrievalCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(arg => arg.StartsWith("--") && arg != "--validate")
            || args.Count(arg => !arg.StartsWith("--")) > 1)
        {
            Console.Error.WriteLine("Usage: --retrieval [--validate] [path-to-retrieval.json]");
            return 2;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        string path = Path.GetFullPath(args.FirstOrDefault(arg => !arg.StartsWith("--"))
            ?? Path.Combine("evals", "retrieval.json"));
        string directory = Path.Combine(Path.GetDirectoryName(path)!, "results");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        try
        {
            RetrievalCatalog catalog = JsonSerializer.Deserialize<RetrievalCatalog>(
                await File.ReadAllTextAsync(path, cancellation.Token), json)
                ?? throw new InvalidDataException("Retrieval catalog is null.");
            await using var pristine = new ExpenseDbContext(new DbContextOptionsBuilder<ExpenseDbContext>()
                .UseSqlite("Data Source=:memory:;Foreign Keys=True").Options);
            await pristine.Database.OpenConnectionAsync(cancellation.Token);
            await ExpenseDatabaseInitializer.InitializeAsync(pristine, cancellation.Token);
            var sources = await pristine.Transactions.AsNoTracking().Include(t => t.Category)
                .OrderBy(t => t.Id).ToListAsync(cancellation.Token);
            catalog.Validate(sources);
            string fingerprint = Fingerprint(sources);
            if (args.Contains("--validate"))
            {
                Console.WriteLine($"Validated {catalog.Scenarios.Count} retrieval scenarios against {sources.Count} fixture records. Draft labels: {catalog.Scenarios.Count(s => s.ReviewStatus == "draft")}. No model calls.");
                return 0;
            }

            IConfiguration config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true).AddUserSecrets<RetrievalCatalog>(optional: true)
                .AddEnvironmentVariables().Build();
            var options = config.GetSection(AzureOpenAIOptions.SectionName).Get<AzureOpenAIOptions>()
                ?? new AzureOpenAIOptions();
            var index = EmbeddingIndexConfiguration.Create(options, "retrieval-v2",
                config["EmbeddingIndex:ModelConfigurationVersion"] ?? "v1");
            Directory.CreateDirectory(directory);
            await using var context = new ExpenseDbContext(new DbContextOptionsBuilder<ExpenseDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "retrieval-cache.db")};Foreign Keys=True").Options);
            await ExpenseDatabaseInitializer.InitializeAsync(context, cancellation.Token);
            var cachedSources = await context.Transactions.AsNoTracking().Include(t => t.Category)
                .OrderBy(t => t.Id).ToListAsync(cancellation.Token);
            if (Fingerprint(cachedSources) != fingerprint)
            {
                throw new InvalidDataException("Retrieval cache does not match the pristine fixture. Preserve it and choose a new catalog directory.");
            }
            context.ChangeTracker.Clear();
            Console.WriteLine("Running paid embedding evaluation only; draft-label metrics remain undefined.");
            using var embeddings = new AzureOpenAIEmbeddings(options, NullLogger<AzureOpenAIEmbeddings>.Instance);
            var population = await new TransactionIndexPopulator(context, embeddings,
                NullLogger<TransactionIndexPopulator>.Instance).PopulateAsync(index.IndexId, index.ModelConfigurationId, cancellation.Token);
            var search = new TransactionSemanticSearch(context, embeddings, index, NullLogger<TransactionSemanticSearch>.Instance);
            var rows = await new RetrievalEvaluator(search).RunAsync(catalog, sources, cancellation.Token);
            string reportPath = Path.Combine(directory, $"retrieval-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.json");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                RunId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, catalog.DatasetVersion, catalog.LabelVersion,
                DatasetFingerprint = fingerprint,
                CatalogHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, cancellation.Token))),
                EmbeddingDeployment = options.EmbeddingDeployment,
                ModelConfigurationVersion = config["EmbeddingIndex:ModelConfigurationVersion"] ?? "v1",
                index.IndexId, TransactionExtensions.TextConstructionVersion, population.Dimensions,
                Population = population, LabelPolicy = "Draft metrics undefined. Ambiguous IDs count as non-relevant until labels are reviewed. No-relevant queries scored separately; metrics undefined.",
                Scenarios = catalog.Scenarios, Rows = rows
            }, json), cancellation.Token);
            foreach (var row in rows.Where(row => row.K == 5))
                Console.WriteLine($"{row.QueryId}: {row.Method} {row.Status}; IDs={string.Join(",", row.Candidates.Select(c => c.Transaction.Id))}");
            Console.WriteLine($"Report: {reportPath}");
            return rows.All(row => row.Status == "Completed") ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Retrieval evaluation cancelled.");
            return 1;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine($"Retrieval input error: {exception.Message}");
            return 2;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Retrieval evaluation failed. Check embedding configuration, provider access and database availability.");
            return 1;
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static string Fingerprint(IReadOnlyList<ExpenseAssistant.Core.Transaction> transactions) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(transactions.Select(t =>
            new { t.Id, t.Date, t.Merchant, t.Description, t.CategoryId, CategoryName = t.Category.Name, t.Amount, t.Currency })))));
}
