using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ExpenseAssistant.Infrastructure;

public sealed class ExpenseDbContextFactory : IDesignTimeDbContextFactory<ExpenseDbContext>
{
    public ExpenseDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Expenses")
            ?? "Data Source=expense-assistant.db;Foreign Keys=True";
        return new ExpenseDbContext(new DbContextOptionsBuilder<ExpenseDbContext>()
            .UseSqlite(connectionString).Options);
    }
}
