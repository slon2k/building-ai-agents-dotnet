using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpenseAssistant.Infrastructure.Migrations;

public partial class InitialSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Adopt the course's EnsureCreated schemas without replacing existing records.
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS "Categories" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Categories" PRIMARY KEY,
                "Name" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "Transactions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Transactions" PRIMARY KEY,
                "Date" TEXT NOT NULL,
                "Merchant" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "CategoryId" TEXT NOT NULL,
                "AmountMinorUnits" INTEGER NOT NULL,
                "Currency" TEXT NOT NULL,
                CONSTRAINT "FK_Transactions_Categories_CategoryId"
                    FOREIGN KEY ("CategoryId") REFERENCES "Categories" ("Id") ON DELETE RESTRICT
            );
            CREATE TABLE IF NOT EXISTS "EmbeddingIndexMetadata" (
                "IndexId" TEXT NOT NULL CONSTRAINT "PK_EmbeddingIndexMetadata" PRIMARY KEY,
                "ModelConfigurationId" TEXT NOT NULL,
                "Dimensions" INTEGER NOT NULL,
                "TextConstructionVersion" TEXT NOT NULL,
                "DatasetVersion" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "TransactionEmbeddings" (
                "TransactionId" TEXT NOT NULL,
                "IndexId" TEXT NOT NULL,
                "SearchableTextHash" TEXT NOT NULL,
                "VectorJson" TEXT NOT NULL,
                CONSTRAINT "PK_TransactionEmbeddings" PRIMARY KEY ("IndexId", "TransactionId"),
                CONSTRAINT "FK_TransactionEmbeddings_EmbeddingIndexMetadata_IndexId"
                    FOREIGN KEY ("IndexId") REFERENCES "EmbeddingIndexMetadata" ("IndexId") ON DELETE CASCADE,
                CONSTRAINT "FK_TransactionEmbeddings_Transactions_TransactionId"
                    FOREIGN KEY ("TransactionId") REFERENCES "Transactions" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_Transactions_CategoryId" ON "Transactions" ("CategoryId");
            CREATE INDEX IF NOT EXISTS "IX_TransactionEmbeddings_TransactionId"
                ON "TransactionEmbeddings" ("TransactionId");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException(
            "The initial migration adopts existing course data. Restore a backup instead of dropping these tables.");
    }
}
