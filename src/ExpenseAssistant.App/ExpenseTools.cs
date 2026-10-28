using System.ComponentModel;
using ExpenseAssistant.Core;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.App;

public class ExpenseTools(ITransactionQueries queries)
{
    public IReadOnlyList<AIFunction> CreateFunctions() =>
    [
        AIFunctionFactory.Create(SearchTransactionsAsync, new AIFunctionFactoryOptions
        {
            Name = "SearchTransactions"
        }),
        AIFunctionFactory.Create(GetSpendingSummaryAsync, new AIFunctionFactoryOptions
        {
            Name = "GetSpendingSummary"
        })
    ];

    [Description("""
        Search transactions within a date range, optionally filtered by
        category ID and merchant text. The start date is inclusive and
        the end date is exclusive. Merchant text is a literal substring.
        Returns up to the requested limit (1-100, default 20), ordered by
        date then ID. HasMore indicates additional matches were omitted.
        Use GetSpendingSummary for complete spending totals; do not treat
        this limited search result as the complete matching dataset.
        """)]

    public Task<TransactionSearchResult> SearchTransactionsAsync(
        [Description("Inclusive start date in YYYY-MM-DD format.")]
        DateOnly startDate,
        [Description("Exclusive end date in YYYY-MM-DD format. Must be after the start date.")]
        DateOnly endDate,
        [Description("Optional exact category ID, such as 'groceries' or 'business-travel', not a display name. Omit to include all categories.")]
        string? categoryId = null,
        [Description("Optional literal merchant-name substring. Matching is ASCII case-insensitive; '%' and '_' are literal characters. Must not be blank. Omit to include all merchants.")]
        string? merchantText = null,
        [Description("Maximum number of transactions to return, from 1 to 100. Defaults to 20. HasMore indicates additional matches were omitted.")]
        int limit = 20,
        CancellationToken cancellationToken = default) => queries.SearchTransactionsAsync(
            startDate,
            endDate,
            categoryId,
            merchantText,
            limit,
            cancellationToken);

    [Description("""
        Calculate complete spending totals for a date range.
        Use this operation for totals instead of adding a limited
        transaction search. The end date is exclusive.
        """)]
    public Task<SpendingSummary> GetSpendingSummaryAsync(
        [Description("Inclusive start date in YYYY-MM-DD format.")]
        DateOnly startDate,
        [Description("Exclusive end date in YYYY-MM-DD format. Must be after the start date.")]
        DateOnly endDate,
        [Description("Optional exact category ID, such as 'groceries' or 'business-travel', not a display name. Omit to include all categories.")]
        string? categoryId = null,
        CancellationToken cancellationToken = default) => queries.GetSpendingSummaryAsync(
            startDate,
            endDate,
            categoryId,
            cancellationToken);
}