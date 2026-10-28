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

    public async Task<ToolResult<TransactionSearchResult>> SearchTransactionsAsync(
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
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Error? validationError = ValidateQueryArguments(startDate, endDate, categoryId);
        if (validationError is not null)
        {
            return validationError;
        }

        if (limit is < 1 or > 100)
        {
            return new Error("INVALID_LIMIT", "The result limit must be between 1 and 100.");
        }

        if (merchantText is not null && string.IsNullOrWhiteSpace(merchantText))
        {
            return new Error("INVALID_MERCHANT", "Merchant text must not be blank. Omit it to include all merchants.");
        }

        return await ExecuteAsync(() => queries.SearchTransactionsAsync(
            startDate,
            endDate,
            categoryId,
            merchantText,
            limit,
            cancellationToken));
    }

    [Description("""
        Calculate complete spending totals for a date range.
        Use this operation for totals instead of adding a limited
        transaction search. The end date is exclusive.
        """)]
    public async Task<ToolResult<SpendingSummary>> GetSpendingSummaryAsync(
        [Description("Inclusive start date in YYYY-MM-DD format.")]
        DateOnly startDate,
        [Description("Exclusive end date in YYYY-MM-DD format. Must be after the start date.")]
        DateOnly endDate,
        [Description("Optional exact category ID, such as 'groceries' or 'business-travel', not a display name. Omit to include all categories.")]
        string? categoryId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Error? validationError = ValidateQueryArguments(startDate, endDate, categoryId);
        if (validationError is not null)
        {
            return validationError;
        }

        return await ExecuteAsync(() => queries.GetSpendingSummaryAsync(
            startDate,
            endDate,
            categoryId,
            cancellationToken));
    }

    private static Error? ValidateQueryArguments(DateOnly startDate, DateOnly endDate, string? categoryId)
    {
        if (endDate <= startDate)
        {
            return new Error("INVALID_DATE_RANGE", "The end date must be after the start date.");
        }

        if (categoryId is not null && string.IsNullOrWhiteSpace(categoryId))
        {
            return new Error("INVALID_CATEGORY", "Category ID must not be blank. Omit it to include all categories.");
        }

        return null;
    }

    private static async Task<ToolResult<TResult>> ExecuteAsync<TResult>(Func<Task<TResult>> query) where TResult : class
    {
        try
        {
            return await query();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException exception) when (exception.ParamName == "categoryId")
        {
            return new Error("INVALID_CATEGORY", "The category ID is not recognized. Use an existing category ID.");
        }
        catch (Exception)
        {
            return new Error("QUERY_FAILED", "The transaction query could not be completed. No result is available.");
        }
    }
}