using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;

namespace ExpenseAssistant.Evals;

public sealed class FixtureAssertions
{
    public string DatasetVersion { get; init; } = string.Empty;
    public Dictionary<string, FixtureAssertion> Assertions { get; init; } = [];

    public void Validate(IEnumerable<EvaluationScenario> scenarios)
    {
        if (DatasetVersion != ExpenseDatabaseInitializer.DatasetVersion || Assertions is null || Assertions.Count == 0)
        {
            throw new InvalidDataException("Fixture assertions must use the current dataset version and contain assertions.");
        }

        foreach (var entry in Assertions)
        {
            FixtureAssertion assertion = entry.Value;
            if (assertion is null || string.IsNullOrWhiteSpace(entry.Key) || assertion.EndDate <= assertion.StartDate
                || assertion.TransactionCount is < 0 or > 100 || assertion.TotalSpending < 0
                || assertion.Currency != ExpenseDatabaseInitializer.Currency
                || assertion.RecordIds is null || assertion.RecordIds.Length != assertion.TransactionCount
                || assertion.RecordIds.Any(string.IsNullOrWhiteSpace)
                || assertion.RecordIds.Distinct(StringComparer.Ordinal).Count() != assertion.RecordIds.Length)
            {
                throw new InvalidDataException($"Fixture assertion '{entry.Key}' is invalid.");
            }
        }

        foreach (EvaluationScenario scenario in scenarios)
        {
            if (scenario.FixtureAssertionId is { } id && !Assertions.ContainsKey(id))
            {
                throw new InvalidDataException($"Scenario '{scenario.Id}' references unknown fixture assertion '{id}'.");
            }
        }
    }
}

public sealed class FixtureAssertion
{
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public string? CategoryId { get; init; }
    public decimal TotalSpending { get; init; }
    public int TransactionCount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string[] RecordIds { get; init; } = [];

    public async Task<bool> VerifyAsync(ITransactionQueries queries, CancellationToken cancellationToken)
    {
        SpendingSummary summary = await queries.GetSpendingSummaryAsync(StartDate, EndDate, CategoryId, cancellationToken);
        TransactionSearchResult search = await queries.SearchTransactionsAsync(
            StartDate, EndDate, CategoryId, limit: 100, cancellationToken: cancellationToken);
        return summary.TotalSpending == TotalSpending && summary.TransactionCount == TransactionCount
            && summary.Currency == Currency && !search.HasMore
            && search.Transactions.Select(transaction => transaction.Id).SequenceEqual(RecordIds);
    }
}