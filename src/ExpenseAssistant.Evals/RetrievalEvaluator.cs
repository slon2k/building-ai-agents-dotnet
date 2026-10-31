using System.Text.RegularExpressions;
using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;

namespace ExpenseAssistant.Evals;

public sealed record RetrievalMetrics(double? Precision, double? Recall, double? HitRate);
public sealed record RetrievalRow(
    string QueryId, string Split, string ReviewStatus, string Method, int K, int EligibleCount,
    IReadOnlyList<SemanticSearchCandidate> Candidates, IReadOnlyList<string> RelevantReturned,
    IReadOnlyList<string> AmbiguousReturned, RetrievalMetrics Metrics, string Status, string? ErrorCode);

public sealed class RetrievalEvaluator(ITransactionSemanticSearch search)
{
    public static IReadOnlyList<SemanticSearchCandidate> KeywordSearch(
        RetrievalScenario scenario, IReadOnlyList<Transaction> transactions, int limit)
    {
        string[] words = Regex.Matches(scenario.Query.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
        return transactions.Where(scenario.IsEligible)
            .Select(transaction =>
            {
                string text = $"{transaction.Merchant.Trim()} {transaction.Description.Trim()}";
                HashSet<string> tokens = Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+")
                    .Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
                return new SemanticSearchCandidate(new TransactionDto(transaction.Id, transaction.Date,
                    transaction.Merchant, transaction.Description, transaction.CategoryId, transaction.Category.Name,
                    transaction.Amount, transaction.Currency), words.Count(tokens.Contains));
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Transaction.Id, StringComparer.Ordinal)
            .Take(limit).ToArray();
    }

    public static RetrievalMetrics Measure(RetrievalScenario scenario, IReadOnlyList<SemanticSearchCandidate> candidates)
    {
        if (scenario.ReviewStatus != "reviewed" || scenario.RelevantIds.Count == 0)
        {
            return new(null, null, null);
        }
        int relevant = candidates.Count(candidate => scenario.RelevantIds.Contains(candidate.Transaction.Id));
        return new(candidates.Count == 0 ? null : (double)relevant / candidates.Count,
            scenario.RelevantSetComplete ? (double)relevant / scenario.RelevantIds.Count : null,
            relevant > 0 ? 1 : 0);
    }

    public async Task<IReadOnlyList<RetrievalRow>> RunAsync(
        RetrievalCatalog catalog, IReadOnlyList<Transaction> transactions, CancellationToken cancellationToken)
    {
        catalog.Validate(transactions);
        List<RetrievalRow> rows = [];
        foreach (RetrievalScenario scenario in catalog.Scenarios)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<SemanticSearchCandidate> semantic = [];
            string? error = null;
            try
            {
                SemanticSearchResult result = await search.SearchAsync(scenario.Query, scenario.StartDate,
                    scenario.EndDate, scenario.CategoryId, 100, cancellationToken);
                semantic = result.Candidates;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { error = RetrievalFailure.Classify(exception); }
            int eligible = transactions.Count(scenario.IsEligible);
            foreach (int k in new[] { 3, 5, 10 })
            {
                Add("keyword", KeywordSearch(scenario, transactions, k), k, null);
                Add("semantic", semantic.Take(k).ToArray(), k, error);
            }
            // Retain a wider ranking to inspect missed candidates without another embedding request.
            Add("semantic-diagnostic", semantic, 100, error);

            void Add(string method, IReadOnlyList<SemanticSearchCandidate> candidates, int k, string? failure)
            {
                rows.Add(new RetrievalRow(scenario.Id, scenario.Split, scenario.ReviewStatus, method, k, eligible,
                    candidates, candidates.Select(c => c.Transaction.Id).Intersect(scenario.RelevantIds).ToArray(),
                    candidates.Select(c => c.Transaction.Id).Intersect(scenario.AmbiguousIds).ToArray(),
                    failure is null ? Measure(scenario, candidates) : new(null, null, null),
                    failure is null ? "Completed" : "Failed", failure));
            }
        }
        return rows;
    }
}
