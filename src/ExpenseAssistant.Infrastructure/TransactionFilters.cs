using ExpenseAssistant.Core;
using Microsoft.EntityFrameworkCore;

namespace ExpenseAssistant.Infrastructure;

internal static class TransactionFilters
{
    public static async Task<IQueryable<Transaction>> CreateQueryAsync(
        ExpenseDbContext context, DateOnly? startDate, DateOnly? endDate,
        string? categoryId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (startDate.HasValue && endDate.HasValue && endDate.Value <= startDate.Value)
        {
            throw new ArgumentException("The end date must be after the start date.", nameof(endDate));
        }

        IQueryable<Transaction> query = context.Transactions.AsNoTracking();
        if (startDate.HasValue)
        {
            query = query.Where(transaction => transaction.Date >= startDate.Value);
        }
        if (endDate.HasValue)
        {
            query = query.Where(transaction => transaction.Date < endDate.Value);
        }
        if (categoryId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(categoryId);
            if (!await context.Categories.AnyAsync(category => category.Id == categoryId, cancellationToken))
            {
                throw new ArgumentException("The category ID is not recognized.", nameof(categoryId));
            }
            query = query.Where(transaction => transaction.CategoryId == categoryId);
        }
        return query;
    }
}
