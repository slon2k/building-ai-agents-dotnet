namespace ExpenseAssistant.Core;

public interface ITransactionQueries
{
    Task<TransactionSearchResult> SearchTransactionsAsync(
        DateOnly startDate,
        DateOnly endDate,
        string? categoryId = null,
        string? merchantText = null,
        int limit = 20,
        CancellationToken cancellationToken = default);

    Task<SpendingSummary> GetSpendingSummaryAsync(
        DateOnly startDate,
        DateOnly endDate,
        string? categoryId = null,
        CancellationToken cancellationToken = default);
}