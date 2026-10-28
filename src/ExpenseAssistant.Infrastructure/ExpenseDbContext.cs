using ExpenseAssistant.Core;
using Microsoft.EntityFrameworkCore;

namespace ExpenseAssistant.Infrastructure;

public sealed class ExpenseDbContext(DbContextOptions<ExpenseDbContext> options) : DbContext(options)
{
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Category> Categories => Set<Category>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Category>(category =>
        {
            category.HasKey(entity => entity.Id);
            category.Property(entity => entity.Id).ValueGeneratedNever();
            category.Property(entity => entity.Name).IsRequired();
        });

        modelBuilder.Entity<Transaction>(transaction =>
        {
            transaction.HasKey(entity => entity.Id);
            transaction.Property(entity => entity.Id).ValueGeneratedNever();
            transaction.Property(entity => entity.Merchant).IsRequired();
            transaction.Property(entity => entity.Description).IsRequired();
            transaction.Property(entity => entity.Currency).IsRequired();
            transaction.Property(entity => entity.Amount)
                .HasColumnName("AmountMinorUnits")
                .HasConversion(amount => ToMinorUnits(amount), minorUnits => minorUnits / 100m);

            transaction.HasOne(entity => entity.Category)
                .WithMany()
                .HasForeignKey(entity => entity.CategoryId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static long ToMinorUnits(decimal amount)
    {
        decimal minorUnits = amount * 100m;

        if (minorUnits != decimal.Truncate(minorUnits))
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amounts must have at most two decimal places.");
        }

        return checked((long)minorUnits);
    }
}