using System.ComponentModel;
using ExpenseAssistant.Core;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.App;

public class ExpenseTools(ITransactionQueries queries, Func<ITransactionSemanticSearch>? semanticSearchFactory = null)
{
    public IReadOnlyList<AIFunction> CreateFunctions()
    {
        List<AIFunction> functions =
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
        if (semanticSearchFactory is not null)
        {
            functions.Add(AIFunctionFactory.Create(SearchTransactionsByMeaningAsync, new AIFunctionFactoryOptions
            {
                Name = "SearchTransactionsByMeaning"
            }));
        }
        return functions.AsReadOnly();
    }

    [Description("""
        Find candidate transactions by meaning, such as purchases possibly
        related to remote work or streaming entertainment. Optional exact
        date and category filters are applied before similarity ranking.
        Returns transaction facts and stable IDs with cosine similarity scores,
        applied filters and eligible record count. Scores indicate ranking
        relevance, not probabilities or proof of purchase purpose.
        Present IDs and explain uncertainty; do not invent facts or infer
        purpose as certain. Top-k candidates are not exhaustive; HasMore means
        eligible records exceeded the limit, not that more relevant matches exist.
        Use SearchTransactions for exact merchant/category retrieval and
        GetSpendingSummary for authoritative totals. Never sum semantic
        candidates as an authoritative concept-based spending total.
        This tool is read-only and does not generate or repair the stored index.
        """)]
    public async Task<ToolResult<SemanticSearchResult>> SearchTransactionsByMeaningAsync(
        [Description("Concept or activity to search for, such as 'remote-work expenses'. Required, nonblank, at most 2000 characters. Keep exact date/category constraints in their separate arguments.")]
        string query,
        [Description("Optional inclusive start date in YYYY-MM-DD format. Omit for no lower date bound.")]
        DateOnly? startDate = null,
        [Description("Optional exclusive end date in YYYY-MM-DD format. Must be after startDate when both are supplied. Omit for no upper date bound.")]
        DateOnly? endDate = null,
        [Description("Optional exact category ID, such as 'software' or 'business-travel', not an inferred concept or display name. Omit to include all categories.")]
        string? categoryId = null,
        [Description("Maximum candidate count, from 1 to 100. Defaults to 5. Results are ranked by similarity, then transaction ID for ties.")]
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000)
        {
            return new Error("INVALID_QUERY", "The semantic query must be nonblank and at most 2000 characters.");
        }
        Error? validationError = ValidateQueryArguments(startDate, endDate, categoryId);
        if (validationError is not null)
        {
            return validationError;
        }
        if (limit is < 1 or > 100)
        {
            return new Error("INVALID_LIMIT", "The result limit must be between 1 and 100.");
        }
        if (semanticSearchFactory is null)
        {
            return new Error("SEMANTIC_SEARCH_UNAVAILABLE", "Semantic search is not configured.");
        }

        return await ExecuteAsync(
            () => semanticSearchFactory().SearchAsync(query, startDate, endDate, categoryId, limit, cancellationToken),
            new Error("SEMANTIC_SEARCH_FAILED",
                "Semantic search could not be completed. No candidates are available. Check embedding configuration and ensure the index is current using --index-only."));
    }

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

    private static Error? ValidateQueryArguments(DateOnly? startDate, DateOnly? endDate, string? categoryId)
    {
        if (startDate.HasValue && endDate.HasValue && endDate.Value <= startDate.Value)
        {
            return new Error("INVALID_DATE_RANGE", "The end date must be after the start date.");
        }

        if (categoryId is not null && string.IsNullOrWhiteSpace(categoryId))
        {
            return new Error("INVALID_CATEGORY", "Category ID must not be blank. Omit it to include all categories.");
        }

        return null;
    }

    private static async Task<ToolResult<TResult>> ExecuteAsync<TResult>(
        Func<Task<TResult>> query, Error? failure = null) where TResult : class
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
            return failure ?? new Error("QUERY_FAILED", "The transaction query could not be completed. No result is available.");
        }
    }
}