using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;

namespace ExpenseAssistant.Evals;

public sealed class RetrievalCatalog
{
    public string DatasetVersion { get; init; } = "";
    public string LabelVersion { get; init; } = "";
    public List<RetrievalScenario> Scenarios { get; init; } = [];

    public void Validate(IReadOnlyList<Transaction> transactions)
    {
        if (DatasetVersion != ExpenseDatabaseInitializer.DatasetVersion || string.IsNullOrWhiteSpace(LabelVersion)
            || Scenarios is null || Scenarios.Count == 0 || Scenarios.Any(s => s is null)
            || Scenarios.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != Scenarios.Count)
        {
            throw new InvalidDataException("Retrieval catalog requires the current dataset, a label version and unique scenario IDs.");
        }
        foreach (RetrievalScenario scenario in Scenarios)
        {
            if (string.IsNullOrWhiteSpace(scenario.Id) || string.IsNullOrWhiteSpace(scenario.Query)
                || scenario.Query.Length > TransactionSemanticSearch.MaxQueryLength
                || scenario.Split is not ("tuning" or "held-out")
                || scenario.ReviewStatus is not ("draft" or "reviewed")
                || scenario.RelevantIds is null || scenario.AmbiguousIds is null
                || string.IsNullOrWhiteSpace(scenario.Notes)
                || (scenario.StartDate.HasValue && scenario.EndDate.HasValue && scenario.EndDate <= scenario.StartDate)
                || (scenario.CategoryId is not null && !transactions.Any(t => t.CategoryId == scenario.CategoryId))
                || scenario.RelevantIds.Distinct().Count() != scenario.RelevantIds.Count
                || scenario.AmbiguousIds.Distinct().Count() != scenario.AmbiguousIds.Count
                || scenario.RelevantIds.Intersect(scenario.AmbiguousIds).Any())
            {
                throw new InvalidDataException($"Invalid retrieval scenario '{scenario.Id}'.");
            }
            HashSet<string> eligible = transactions.Where(scenario.IsEligible).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            if (scenario.RelevantIds.Concat(scenario.AmbiguousIds).Any(id => !eligible.Contains(id)))
            {
                throw new InvalidDataException($"Scenario '{scenario.Id}' labels reference missing or ineligible records.");
            }
        }
    }
}

public sealed class RetrievalScenario
{
    public string Id { get; init; } = "";
    public string Query { get; init; } = "";
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public string? CategoryId { get; init; }
    public string Split { get; init; } = "tuning";
    public string ReviewStatus { get; init; } = "draft";
    public bool RelevantSetComplete { get; init; }
    public List<string> RelevantIds { get; init; } = [];
    public List<string> AmbiguousIds { get; init; } = [];
    public string Notes { get; init; } = "";

    public bool IsEligible(Transaction transaction) =>
        (!StartDate.HasValue || transaction.Date >= StartDate.Value)
        && (!EndDate.HasValue || transaction.Date < EndDate.Value)
        && (CategoryId is null || transaction.CategoryId == CategoryId);
}
