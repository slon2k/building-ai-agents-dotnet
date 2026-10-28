namespace ExpenseAssistant.Core;

public sealed record CategorySpendingTotal(
    string CategoryId,
    string CategoryName,
    decimal TotalSpending,
    int TransactionCount);

public sealed record SpendingSummary(
    decimal TotalSpending,
    int TransactionCount,
    string Currency,
    IReadOnlyList<CategorySpendingTotal> CategoryTotals);