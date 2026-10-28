namespace ExpenseAssistant.Core;

public sealed record TransactionDto(
    string Id,
    DateOnly Date,
    string Merchant,
    string Description,
    string CategoryId,
    string CategoryName,
    decimal Amount,
    string Currency);

public sealed record TransactionSearchResult(
    IReadOnlyList<TransactionDto> Transactions,
    bool HasMore)
{
    public int ReturnedCount => Transactions.Count;
}