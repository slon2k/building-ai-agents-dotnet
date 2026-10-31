namespace ExpenseAssistant.Core;

public interface ITransactionSemanticSearch
{
    Task<SemanticSearchResult> SearchAsync(
        string query,
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        string? categoryId = null,
        int limit = 5,
        CancellationToken cancellationToken = default);
}
