using ExpenseAssistant.Core;
using Microsoft.EntityFrameworkCore;

namespace ExpenseAssistant.Infrastructure;

public sealed class ExpenseDbContext(DbContextOptions<ExpenseDbContext> options) : DbContext(options)
{
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<TransactionEmbeddings> TransactionEmbeddings => Set<TransactionEmbeddings>();
    public DbSet<EmbeddingIndexMetadata> EmbeddingIndexMetadata => Set<EmbeddingIndexMetadata>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
    public DbSet<ConversationMessagePart> ConversationMessageParts => Set<ConversationMessagePart>();
    public DbSet<SessionSummary> SessionSummaries => Set<SessionSummary>();
    public DbSet<SessionRun> SessionRuns => Set<SessionRun>();

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

        modelBuilder.Entity<Session>(session =>
        {
            session.HasKey(entity => entity.SessionId);
            session.Property(entity => entity.SessionId).ValueGeneratedNever();
            session.Property(entity => entity.DisplayName).IsRequired();
            session.Property(entity => entity.CreatedAt).IsRequired();
            session.Property(entity => entity.LastActivityAt).IsRequired();
        });

        modelBuilder.Entity<ConversationMessage>(message =>
        {
            message.ToTable("ConversationMessages", table => table.HasCheckConstraint(
                "CK_ConversationMessages_SequenceNumber",
                "\"SequenceNumber\" > 0"));
            message.HasKey(entity => entity.MessageId);
            message.Property(entity => entity.MessageId).ValueGeneratedNever();
            message.Property(entity => entity.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
            message.Property(entity => entity.SequenceNumber).IsRequired();
            message.HasIndex(entity => new { entity.SessionId, entity.SequenceNumber }).IsUnique();
            message.HasOne<Session>()
                .WithMany()
                .HasForeignKey(entity => entity.SessionId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);
            message.HasOne<SessionRun>()
                .WithMany()
                .HasForeignKey(entity => new { entity.SessionId, entity.RunId })
                .HasPrincipalKey(entity => new { entity.SessionId, entity.RunId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConversationMessagePart>(part =>
        {
            part.ToTable("ConversationMessageParts", table => table.HasCheckConstraint(
                "CK_ConversationMessageParts_KindFields",
                "(\"Kind\" = 'Text' AND \"Ordinal\" >= 0 AND \"Text\" IS NOT NULL AND \"CallId\" IS NULL AND \"ToolName\" IS NULL AND \"ArgumentsJson\" IS NULL AND \"ResultJson\" IS NULL) " +
                "OR (\"Kind\" = 'ToolCall' AND \"Ordinal\" >= 0 AND \"Text\" IS NULL AND \"CallId\" IS NOT NULL AND length(trim(\"CallId\")) > 0 AND \"ToolName\" IS NOT NULL AND length(trim(\"ToolName\")) > 0 AND \"ArgumentsJson\" IS NOT NULL AND \"ResultJson\" IS NULL) " +
                "OR (\"Kind\" = 'ToolResult' AND \"Ordinal\" >= 0 AND \"Text\" IS NULL AND \"CallId\" IS NOT NULL AND length(trim(\"CallId\")) > 0 AND \"ToolName\" IS NULL AND \"ArgumentsJson\" IS NULL AND \"ResultJson\" IS NOT NULL)"));
            part.HasKey(entity => new { entity.MessageId, entity.Ordinal });
            part.Property(entity => entity.MessageId).ValueGeneratedNever();
            part.Property(entity => entity.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
            part.Property(entity => entity.ArgumentsJson).HasColumnType("TEXT");
            part.Property(entity => entity.ResultJson).HasColumnType("TEXT");
            part.HasOne<ConversationMessage>()
                .WithMany(entity => entity.Parts)
                .HasForeignKey(entity => entity.MessageId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SessionSummary>(summary =>
        {
            summary.ToTable("SessionSummaries", table =>
            {
                table.HasCheckConstraint("CK_SessionSummaries_Coverage", "\"CoveredThroughSequenceNumber\" >= 0");
                table.HasCheckConstraint("CK_SessionSummaries_Version", "\"Version\" > 0");
            });
            summary.HasKey(entity => entity.SessionId);
            summary.Property(entity => entity.SessionId).ValueGeneratedNever();
            summary.Property(entity => entity.SummaryText).IsRequired();
            summary.Property(entity => entity.CoveredThroughSequenceNumber).IsRequired();
            summary.Property(entity => entity.Version).IsRequired();
            summary.Property(entity => entity.CreatedAt).IsRequired();
            summary.HasOne<Session>()
                .WithOne()
                .HasForeignKey<SessionSummary>(entity => entity.SessionId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SessionRun>(run =>
        {
            run.ToTable("SessionRuns", table =>
            {
                table.HasCheckConstraint("CK_SessionRuns_NonNegativeCounters",
                    "\"ModelStepCount\" >= 0 AND \"ToolCallCount\" >= 0 " +
                    "AND (\"InputTokenCount\" IS NULL OR \"InputTokenCount\" >= 0) " +
                    "AND (\"OutputTokenCount\" IS NULL OR \"OutputTokenCount\" >= 0) " +
                    "AND (\"TotalTokenCount\" IS NULL OR \"TotalTokenCount\" >= 0)");
                table.HasCheckConstraint("CK_SessionRuns_StatusEndTime",
                    "(\"Status\" = 'Running' AND \"EndedAt\" IS NULL) OR (\"Status\" <> 'Running' AND \"EndedAt\" IS NOT NULL)");
            });
            run.HasKey(entity => entity.RunId);
            run.Property(entity => entity.RunId).ValueGeneratedNever();
            run.Property(entity => entity.SessionId).IsRequired();
            run.Property(entity => entity.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            run.Property(entity => entity.ModelStepCount).IsRequired();
            run.Property(entity => entity.ToolCallCount).IsRequired();
            run.HasAlternateKey(entity => new { entity.SessionId, entity.RunId });
            run.HasIndex(entity => entity.SessionId)
                .IsUnique()
                .HasFilter("\"Status\" = 'Running'");
            run.HasOne<Session>()
                .WithMany()
                .HasForeignKey(entity => entity.SessionId)
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