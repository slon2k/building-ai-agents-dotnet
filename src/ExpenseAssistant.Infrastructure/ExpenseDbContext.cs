using ExpenseAssistant.Core;
using Microsoft.EntityFrameworkCore;

namespace ExpenseAssistant.Infrastructure;

public sealed class ExpenseDbContext(DbContextOptions<ExpenseDbContext> options) : DbContext(options)
{
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<TransactionEmbeddings> TransactionEmbeddings => Set<TransactionEmbeddings>();
    public DbSet<EmbeddingIndexMetadata> EmbeddingIndexMetadata => Set<EmbeddingIndexMetadata>();

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

        modelBuilder.Entity<EmbeddingIndexMetadata>(index =>
        {
            index.HasKey(entity => entity.IndexId);
            index.Property(entity => entity.IndexId).ValueGeneratedNever();
            index.Property(entity => entity.ModelConfigurationId).IsRequired();
            index.Property(entity => entity.TextConstructionVersion).IsRequired();
            index.Property(entity => entity.DatasetVersion).IsRequired();
        });

        modelBuilder.Entity<TransactionEmbeddings>(embedding =>
        {
            embedding.HasKey(entity => new { entity.IndexId, entity.TransactionId });
            embedding.Property(entity => entity.SearchableTextHash).IsRequired();
            embedding.Property(entity => entity.VectorJson).IsRequired();

            embedding.HasOne<EmbeddingIndexMetadata>()
                .WithMany()
                .HasForeignKey(entity => entity.IndexId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            embedding.HasOne<Transaction>()
                .WithMany()
                .HasForeignKey(entity => entity.TransactionId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);
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