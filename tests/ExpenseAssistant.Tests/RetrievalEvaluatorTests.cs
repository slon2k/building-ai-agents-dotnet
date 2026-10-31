using ExpenseAssistant.Core;
using ExpenseAssistant.Evals;
using ExpenseAssistant.Infrastructure;

namespace ExpenseAssistant.Tests;

public class RetrievalEvaluatorTests
{
    [Fact]
    public void Metrics_GateDraftLabelsAndHandleUndefinedDenominators()
    {
        Assert.Equal(new RetrievalMetrics(null, null, null),
            RetrievalEvaluator.Measure(new RetrievalScenario { RelevantIds = ["A"] }, [Candidate("A")]));
        var reviewed = new RetrievalScenario
        {
            ReviewStatus = "reviewed", RelevantSetComplete = true, RelevantIds = ["A", "B"]
        };
        Assert.Equal(new RetrievalMetrics(0.5, 0.5, 1),
            RetrievalEvaluator.Measure(reviewed, [Candidate("A"), Candidate("C")]));
        Assert.Equal(new RetrievalMetrics(null, 0, 0), RetrievalEvaluator.Measure(reviewed, []));
        Assert.Equal(new RetrievalMetrics(null, null, null),
            RetrievalEvaluator.Measure(new RetrievalScenario { ReviewStatus = "reviewed" }, [Candidate("C")]));
        Assert.Null(RetrievalEvaluator.Measure(new RetrievalScenario
        {
            ReviewStatus = "reviewed", RelevantIds = ["A"]
        }, [Candidate("A")]).Recall);
    }

    [Fact]
    public void Keywords_UseExactTokensFiltersAndStableTies()
    {
        Transaction[] transactions = [Source("B", "monitor"), Source("A", "monitor"), Source("C", "monitoring")];
        var scenario = Scenario();
        var results = RetrievalEvaluator.KeywordSearch(scenario, transactions, 10);
        Assert.Equal(new[] { "A", "B" }, results.Select(c => c.Transaction.Id));
        Assert.Empty(RetrievalEvaluator.KeywordSearch(new RetrievalScenario
        {
            Query = "monitor", EndDate = new DateOnly(2025, 1, 1)
        }, transactions, 10));
    }

    [Fact]
    public async Task Runner_EmbedsOncePerScenarioAndProducesAllKRows()
    {
        var fake = new Search();
        var catalog = Catalog();
        var rows = await new RetrievalEvaluator(fake).RunAsync(catalog, [Source("A", "monitor")], default);
        Assert.Equal(1, fake.Calls);
        Assert.Equal(7, rows.Count);
        Assert.Equal(new[] { 3, 5, 10, 100 }, rows.Where(r => r.Method.StartsWith("semantic")).Select(r => r.K));
        Assert.All(rows, r => Assert.Equal("Completed", r.Status));
        Assert.All(rows, r => Assert.Null(r.Metrics.Precision));
    }

    [Fact]
    public async Task Runner_FailuresAreNotSuccessShapedAndCancellationPropagates()
    {
        var fake = new Search { Failure = new InvalidOperationException() };
        var rows = await new RetrievalEvaluator(fake).RunAsync(Catalog(), [Source("A", "monitor")], default);
        Assert.All(rows.Where(r => r.Method.StartsWith("semantic")), r =>
        {
            Assert.Equal("Failed", r.Status);
            Assert.Equal("INDEX_OR_CONFIGURATION_FAILED", r.ErrorCode);
            Assert.Null(r.Metrics.Precision);
        });
        fake.Failure = new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RetrievalEvaluator(fake).RunAsync(Catalog(), [Source("A", "monitor")], default));
    }

    [Theory]
    [InlineData("network", "PROVIDER_CONNECTION_FAILED")]
    [InlineData("database", "DATABASE_FAILED")]
    [InlineData("json", "INVALID_VECTOR_JSON")]
    [InlineData("arguments", "INVALID_SEARCH_ARGUMENTS")]
    [InlineData("unknown", "RETRIEVAL_FAILED")]
    public async Task Runner_ClassifiesFailuresWithoutExposingExceptionMessages(string kind, string expected)
    {
        const string message = "PRIVATE_PROVIDER_OR_DATABASE_DETAILS";
        Exception failure = kind switch
        {
            "network" => new HttpRequestException(message),
            "database" => new Microsoft.Data.Sqlite.SqliteException(message, 1),
            "json" => new System.Text.Json.JsonException(message),
            "arguments" => new ArgumentException(message),
            _ => new Exception(message)
        };
        var rows = await new RetrievalEvaluator(new Search { Failure = failure })
            .RunAsync(Catalog(), [Source("A", "monitor")], default);

        Assert.All(rows.Where(row => row.Method.StartsWith("semantic")), row =>
        {
            Assert.Equal("Failed", row.Status);
            Assert.Equal(expected, row.ErrorCode);
            Assert.Null(row.Metrics.Precision);
        });
        Assert.DoesNotContain(message, System.Text.Json.JsonSerializer.Serialize(rows));
        Assert.All(rows.Where(row => row.Method == "keyword"), row => Assert.Equal("Completed", row.Status));
    }

    [Fact]
    public async Task Runner_ClassifiesProviderHttpFailureWithoutExposingResponse()
    {
        using var handler = new ProviderFailureHandler();
        using var http = new HttpClient(handler);
        var client = new OpenAI.Embeddings.EmbeddingClient("fake-model",
            new System.ClientModel.ApiKeyCredential("test-only-key"),
            new OpenAI.OpenAIClientOptions
            {
                Endpoint = new Uri("https://expense-assistant.invalid"),
                Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(http)
            });
        var failure = await Assert.ThrowsAsync<System.ClientModel.ClientResultException>(
            async () => await client.GenerateEmbeddingAsync("monitor"));
        var rows = await new RetrievalEvaluator(new Search { Failure = failure })
            .RunAsync(Catalog(), [Source("A", "monitor")], default);

        Assert.All(rows.Where(row => row.Method.StartsWith("semantic")),
            row => Assert.Equal("PROVIDER_HTTP_401", row.ErrorCode));
        Assert.DoesNotContain("PRIVATE_PROVIDER_DETAILS", System.Text.Json.JsonSerializer.Serialize(rows));
    }

    [Fact]
    public async Task CatalogSnapshot_HashIdentifiesLoadedBytesDespiteLaterFileEdit()
    {
        string path = Path.Combine(Path.GetTempPath(), $"retrieval-snapshot-{Guid.NewGuid():N}.json");
        byte[] original = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Catalog());
        try
        {
            await File.WriteAllBytesAsync(path, original);
            var snapshot = await RetrievalCatalogSnapshot.LoadAsync(path, new System.Text.Json.JsonSerializerOptions());
            await File.WriteAllTextAsync(path, "{}");

            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)), snapshot.Hash);
            Assert.Equal("monitor", Assert.Single(snapshot.Catalog.Scenarios).Id);
            Assert.NotEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(path))), snapshot.Hash);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Catalog_RejectsMissingLabelIdsAndDuplicateScenarios()
    {
        Assert.Throws<InvalidDataException>(() => Catalog().Validate([]));
        var scenario = Scenario();
        Assert.Throws<InvalidDataException>(() => new RetrievalCatalog
        {
            DatasetVersion = "v2", LabelVersion = "draft-v1", Scenarios = [scenario, scenario]
        }.Validate([Source("A", "monitor")]));
    }

    private static RetrievalScenario Scenario() => new()
    {
        Id = "monitor", Query = "monitor", RelevantIds = ["A"], Notes = "Draft candidate relevance."
    };

    private static RetrievalCatalog Catalog() => new()
    {
        DatasetVersion = ExpenseDatabaseInitializer.DatasetVersion, LabelVersion = "draft-v1", Scenarios = [Scenario()]
    };

    private static Transaction Source(string id, string description) => new()
    {
        Id = id, Date = new DateOnly(2026, 3, 1), Merchant = "Store", Description = description,
        CategoryId = "shopping", Category = new Category { Id = "shopping", Name = "Shopping" },
        Amount = 10m, Currency = "EUR"
    };
    private static SemanticSearchCandidate Candidate(string id) => new(
        new TransactionDto(id, new DateOnly(2026, 3, 1), "Store", "monitor", "shopping", "Shopping", 10m, "EUR"), 1);

    private sealed class ProviderFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"error":{"message":"PRIVATE_PROVIDER_DETAILS","type":"invalid_request_error","code":"invalid_api_key"}}""",
                    System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class Search : ITransactionSemanticSearch
    {
        public int Calls { get; private set; }
        public Exception? Failure { get; set; }
        public Task<SemanticSearchResult> SearchAsync(string query, DateOnly? startDate = null, DateOnly? endDate = null,
            string? categoryId = null, int limit = 5, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(new SemanticSearchResult([Candidate("A")], startDate, endDate, categoryId, 1, limit));
        }
    }
}
