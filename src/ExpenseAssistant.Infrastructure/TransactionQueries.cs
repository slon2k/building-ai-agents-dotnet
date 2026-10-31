using ExpenseAssistant.Core;
using Microsoft.EntityFrameworkCore;

namespace ExpenseAssistant.Infrastructure;

public sealed class TransactionQueries : ITransactionQueries
{
    public const int MaxSearchLimit = 100;

    private readonly ExpenseDbContext context;
    private readonly string currency;

    public TransactionQueries(ExpenseDbContext context, string currency = "EUR")
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        this.context = context;
        this.currency = currency;
    }

    public async Task<TransactionSearchResult> SearchTransactionsAsync(
        DateOnly startDate,
        DateOnly endDate,
        string? categoryId = null,
        string? merchantText = null,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaxSearchLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), $"Limit must be between 1 and {MaxSearchLimit}.");
        }

        if (merchantText is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(merchantText);
        }

        IQueryable<Transaction> query = await CreateQueryAsync(startDate, endDate, categoryId, cancellationToken);

        if (merchantText is not null)
        {
            string pattern = "%" + merchantText.Trim()
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_") + "%";
            query = query.Where(transaction => EF.Functions.Like(transaction.Merchant, pattern, "\\"));
        }

        List<TransactionDto> transactions = await query
            .OrderBy(transaction => transaction.Date)
            .ThenBy(transaction => transaction.Id)
            .Take(limit + 1)
            .Select(transaction => new TransactionDto(
                transaction.Id,
                transaction.Date,
                transaction.Merchant,
                transaction.Description,
                transaction.CategoryId,
                transaction.Category.Name,
                transaction.Amount,
                transaction.Currency))
            .ToListAsync(cancellationToken);

        bool hasMore = transactions.Count > limit;
        if (hasMore)
        {
            transactions.RemoveAt(limit);
        }

        return new TransactionSearchResult(transactions.AsReadOnly(), hasMore);
    }

    public async Task<SpendingSummary> GetSpendingSummaryAsync(
        DateOnly startDate,
        DateOnly endDate,
        string? categoryId = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Transaction> query = await CreateQueryAsync(startDate, endDate, categoryId, cancellationToken);
        var transactions = await query
            .Select(transaction => new
            {
                transaction.CategoryId,
                CategoryName = transaction.Category.Name,
                transaction.Amount,
                transaction.Currency
            })
            .ToListAsync(cancellationToken);

        if (transactions.Any(transaction => transaction.Currency != currency))
        {
            throw new InvalidOperationException("Spending summaries require all matching transactions to use the configured currency.");
        }

        CategorySpendingTotal[] categoryTotals = transactions
            .GroupBy(transaction => new { transaction.CategoryId, transaction.CategoryName })
            .OrderBy(group => group.Key.CategoryId, StringComparer.Ordinal)
            .Select(group => new CategorySpendingTotal(
                group.Key.CategoryId,
                group.Key.CategoryName,
                group.Sum(transaction => transaction.Amount),
                group.Count()))
            .ToArray();

        return new SpendingSummary(
            transactions.Sum(transaction => transaction.Amount),
            transactions.Count,
            currency,
            Array.AsReadOnly(categoryTotals));
    }

    private Task<IQueryable<Transaction>> CreateQueryAsync(
        DateOnly startDate, DateOnly endDate, string? categoryId, CancellationToken cancellationToken)
        => TransactionFilters.CreateQueryAsync(context, startDate, endDate, categoryId, cancellationToken);
}