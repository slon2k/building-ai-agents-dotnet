namespace ExpenseAssistant.Core;

public sealed record SemanticSearchCandidate(TransactionDto Transaction, double Score);

public sealed record SemanticSearchResult(
    IReadOnlyList<SemanticSearchCandidate> Candidates,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string? CategoryId,
    int EligibleRecordCount,
    int RequestedLimit)
{
    public int ReturnedCount => Candidates.Count;
    public bool HasMore => EligibleRecordCount > RequestedLimit;
}
