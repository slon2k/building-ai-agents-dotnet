using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpenseAssistant.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "SessionRuns",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StopReason = table.Column<int>(type: "INTEGER", nullable: true),
                    ModelStepCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ToolCallCount = table.Column<int>(type: "INTEGER", nullable: false),
                    InputTokenCount = table.Column<long>(type: "INTEGER", nullable: true),
                    OutputTokenCount = table.Column<long>(type: "INTEGER", nullable: true),
                    TotalTokenCount = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionRuns", x => x.RunId);
                    table.UniqueConstraint("AK_SessionRuns_SessionId_RunId", x => new { x.SessionId, x.RunId });
                    table.CheckConstraint("CK_SessionRuns_NonNegativeCounters", "\"ModelStepCount\" >= 0 AND \"ToolCallCount\" >= 0 AND (\"InputTokenCount\" IS NULL OR \"InputTokenCount\" >= 0) AND (\"OutputTokenCount\" IS NULL OR \"OutputTokenCount\" >= 0) AND (\"TotalTokenCount\" IS NULL OR \"TotalTokenCount\" >= 0)");
                    table.CheckConstraint("CK_SessionRuns_StatusEndTime", "(\"Status\" = 'Running' AND \"EndedAt\" IS NULL) OR (\"Status\" <> 'Running' AND \"EndedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_SessionRuns_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SessionSummaries",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SummaryText = table.Column<string>(type: "TEXT", nullable: false),
                    CoveredThroughSequenceNumber = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: true),
                    PromptVersion = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionSummaries", x => x.SessionId);
                    table.CheckConstraint("CK_SessionSummaries_Coverage", "\"CoveredThroughSequenceNumber\" >= 0");
                    table.CheckConstraint("CK_SessionSummaries_Version", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_SessionSummaries_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConversationMessages",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SequenceNumber = table.Column<long>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMessages", x => x.MessageId);
                    table.CheckConstraint("CK_ConversationMessages_SequenceNumber", "\"SequenceNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_ConversationMessages_SessionRuns_SessionId_RunId",
                        columns: x => new { x.SessionId, x.RunId },
                        principalTable: "SessionRuns",
                        principalColumns: new[] { "SessionId", "RunId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConversationMessages_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConversationMessageParts",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: true),
                    CallId = table.Column<string>(type: "TEXT", nullable: true),
                    ToolName = table.Column<string>(type: "TEXT", nullable: true),
                    ArgumentsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMessageParts", x => new { x.MessageId, x.Ordinal });
                    table.CheckConstraint("CK_ConversationMessageParts_KindFields", "(\"Kind\" = 'Text' AND \"Ordinal\" >= 0 AND \"Text\" IS NOT NULL AND \"CallId\" IS NULL AND \"ToolName\" IS NULL AND \"ArgumentsJson\" IS NULL AND \"ResultJson\" IS NULL) OR (\"Kind\" = 'ToolCall' AND \"Ordinal\" >= 0 AND \"Text\" IS NULL AND \"CallId\" IS NOT NULL AND length(trim(\"CallId\")) > 0 AND \"ToolName\" IS NOT NULL AND length(trim(\"ToolName\")) > 0 AND \"ArgumentsJson\" IS NOT NULL AND \"ResultJson\" IS NULL) OR (\"Kind\" = 'ToolResult' AND \"Ordinal\" >= 0 AND \"Text\" IS NULL AND \"CallId\" IS NOT NULL AND length(trim(\"CallId\")) > 0 AND \"ToolName\" IS NULL AND \"ArgumentsJson\" IS NULL AND \"ResultJson\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ConversationMessageParts_ConversationMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "ConversationMessages",
                        principalColumn: "MessageId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_SessionId_RunId",
                table: "ConversationMessages",
                columns: new[] { "SessionId", "RunId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_SessionId_SequenceNumber",
                table: "ConversationMessages",
                columns: new[] { "SessionId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SessionRuns_SessionId",
                table: "SessionRuns",
                column: "SessionId",
                unique: true,
                filter: "\"Status\" = 'Running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationMessageParts");

            migrationBuilder.DropTable(
                name: "SessionSummaries");

            migrationBuilder.DropTable(
                name: "ConversationMessages");

            migrationBuilder.DropTable(
                name: "SessionRuns");

            migrationBuilder.DropTable(
                name: "Sessions");
        }
    }
}
