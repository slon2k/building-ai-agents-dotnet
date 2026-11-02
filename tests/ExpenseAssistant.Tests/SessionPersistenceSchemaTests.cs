using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ExpenseAssistant.Tests;

public class SessionPersistenceSchemaTests
{
    [Fact]
    public async Task SaveChangesAsync_PersistsOrderedMessageParts()
    {
        await using var context = CreateContext();
        Guid sessionId = Guid.NewGuid();
        Guid messageId = Guid.NewGuid();
        context.Sessions.Add(CreateSession(sessionId));
        context.ConversationMessages.Add(new ConversationMessage
        {
            MessageId = messageId,
            SessionId = sessionId,
            SequenceNumber = 1,
            Role = ConversationMessageRole.Assistant,
            Parts =
            [
                new ConversationMessagePart
                {
                    MessageId = messageId,
                    Ordinal = 1,
                    Kind = ConversationMessagePartKind.ToolCall,
                    CallId = "call-1",
                    ToolName = "SearchTransactions",
                    ArgumentsJson = "{}"
                },
                new ConversationMessagePart
                {
                    MessageId = messageId,
                    Ordinal = 0,
                    Kind = ConversationMessagePartKind.Text,
                    Text = "Searching."
                }
            ]
        });
        ConversationMessage message = context.ConversationMessages.Local.Single();

        await context.SaveChangesAsync();

        ConversationMessagePart[] parts = await context.ConversationMessageParts
            .Where(part => part.MessageId == message.MessageId)
            .OrderBy(part => part.Ordinal)
            .ToArrayAsync();
        Assert.Collection(parts,
            part => Assert.Equal(ConversationMessagePartKind.Text, part.Kind),
            part =>
            {
                Assert.Equal(ConversationMessagePartKind.ToolCall, part.Kind);
                Assert.Equal("call-1", part.CallId);
            });
    }

    [Fact]
    public async Task SaveChangesAsync_RejectsPartFieldsThatDoNotMatchKind()
    {
        await using var context = CreateContext();
        Guid sessionId = Guid.NewGuid();
        Guid messageId = Guid.NewGuid();
        context.Sessions.Add(CreateSession(sessionId));
        context.ConversationMessages.Add(new ConversationMessage
        {
            MessageId = messageId,
            SessionId = sessionId,
            SequenceNumber = 1,
            Role = ConversationMessageRole.User,
            Parts =
            [
                new ConversationMessagePart
                {
                    MessageId = messageId,
                    Ordinal = 0,
                    Kind = ConversationMessagePartKind.Text,
                    Text = "Hello",
                    CallId = "unexpected-call-id"
                }
            ]
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_RejectsToolCallWithoutCallIdOrToolName()
    {
        await using var context = CreateContext();
        Guid sessionId = Guid.NewGuid();
        Guid messageId = Guid.NewGuid();
        context.Sessions.Add(CreateSession(sessionId));
        context.ConversationMessages.Add(new ConversationMessage
        {
            MessageId = messageId,
            SessionId = sessionId,
            SequenceNumber = 1,
            Role = ConversationMessageRole.Assistant,
            Parts =
            [
                new ConversationMessagePart
                {
                    MessageId = messageId,
                    Ordinal = 0,
                    Kind = ConversationMessagePartKind.ToolCall,
                    ArgumentsJson = "{}"
                }
            ]
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_RejectsRunOwnedByAnotherSession()
    {
        await using var context = CreateContext();
        Guid runOwnerSessionId = Guid.NewGuid();
        Guid messageSessionId = Guid.NewGuid();
        Guid runId = Guid.NewGuid();
        context.Sessions.AddRange(CreateSession(runOwnerSessionId), CreateSession(messageSessionId));
        context.SessionRuns.Add(CreateRun(runId, runOwnerSessionId));
        context.ConversationMessages.Add(new ConversationMessage
        {
            MessageId = Guid.NewGuid(),
            SessionId = messageSessionId,
            RunId = runId,
            SequenceNumber = 1,
            Role = ConversationMessageRole.User
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_RejectsMultipleRunningRunsForSession()
    {
        await using var context = CreateContext();
        Guid sessionId = Guid.NewGuid();
        context.Sessions.Add(CreateSession(sessionId));
        context.SessionRuns.AddRange(CreateRun(Guid.NewGuid(), sessionId), CreateRun(Guid.NewGuid(), sessionId));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    private static Session CreateSession(Guid sessionId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new Session
        {
            SessionId = sessionId,
            DisplayName = "Test session",
            CreatedAt = now,
            LastActivityAt = now
        };
    }

    private static SessionRun CreateRun(Guid runId, Guid sessionId) => new()
    {
        RunId = runId,
        SessionId = sessionId,
        StartedAt = DateTimeOffset.UtcNow,
        Status = SessionRunStatus.Running
    };

    private static ExpenseDbContext CreateContext()
    {
        var context = new ExpenseDbContext(new DbContextOptionsBuilder<ExpenseDbContext>()
            .UseSqlite("Data Source=:memory:;Foreign Keys=True").Options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }
}