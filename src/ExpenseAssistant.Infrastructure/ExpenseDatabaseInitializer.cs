using ExpenseAssistant.Core;
using Microsoft.EntityFrameworkCore;

namespace ExpenseAssistant.Infrastructure;

public static class ExpenseDatabaseInitializer
{
    public const string DatasetVersion = "v1";
    public const string Currency = "EUR";

    public static async Task InitializeAsync(ExpenseDbContext context, CancellationToken cancellationToken = default)
    {
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await using var databaseTransaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var existingCategoryIds = (await context.Categories
            .Select(category => category.Id)
            .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var existingTransactionIds = (await context.Transactions
            .Select(transaction => transaction.Id)
            .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

        (string Id, string Name, string Merchant, string Description)[] templates =
        [
            ("groceries", "Groceries", "Green Market", "Weekly groceries"),
            ("dining", "Dining", "Corner Cafe", "Lunch and coffee"),
            ("transport", "Transport", "City Transit", "Monthly travel pass"),
            ("utilities", "Utilities", "City Energy", "Household energy bill"),
            ("software", "Software", "CloudDesk", "Monthly online collaboration subscription"),
            ("travel", "Travel", "Example Hotel", "Stay near the station"),
            ("shopping", "Shopping", "Central Store", "Accessories and supplies"),
            ("business-travel", "Business Travel", "Rail Connect", "Train ticket for client meeting")
        ];

        foreach (var template in templates)
        {
            if (!existingCategoryIds.Contains(template.Id))
            {
                context.Categories.Add(new Category { Id = template.Id, Name = template.Name });
            }
        }

        List<Transaction> transactions =
        [
            CreateTransaction("TX-G1", new DateOnly(2026, 3, 2), "groceries", "Green Market", "Weekly groceries", 1250),
            CreateTransaction("TX-G2", new DateOnly(2026, 3, 14), "groceries", "Green Market", "Fresh produce", 875),
            CreateTransaction("TX-G3", new DateOnly(2026, 4, 1), "groceries", "Green Market", "Pantry supplies", 500)
        ];

        for (int index = 0; index < 120; index++)
        {
            int month = index / 20 + 1;
            int templateIndex = index % templates.Length;
            if (month == 3 && templateIndex == 0)
            {
                templateIndex = 1;
            }

            var template = templates[templateIndex];
            transactions.Add(CreateTransaction(
                FormattableString.Invariant($"TX-{index + 1:000}"),
                new DateOnly(2026, month, index % 20 + 1),
                template.Id,
                template.Merchant,
                template.Description,
                350 + index * 137 % 15000));
        }

        context.Transactions.AddRange(transactions.Where(transaction => !existingTransactionIds.Contains(transaction.Id)));
        await context.SaveChangesAsync(cancellationToken);
        await databaseTransaction.CommitAsync(cancellationToken);
    }

    private static Transaction CreateTransaction(
        string id, DateOnly date, string categoryId, string merchant, string description, long minorUnits) => new()
    {
        Id = id,
        Date = date,
        CategoryId = categoryId,
        Merchant = merchant,
        Description = description,
        Amount = minorUnits / 100m,
        Currency = Currency
    };
}