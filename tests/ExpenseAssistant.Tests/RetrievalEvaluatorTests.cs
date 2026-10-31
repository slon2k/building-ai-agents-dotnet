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
            Assert.Equal("RETRIEVAL_FAILED", r.ErrorCode);
            Assert.Null(r.Metrics.Precision);
        });
        fake.Failure = new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RetrievalEvaluator(fake).RunAsync(Catalog(), [Source("A", "monitor")], default));
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
