using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ExpenseAssistant.Tests;

public class SessionStoreTests
{
    [Fact]
    public async Task CreateSessionAsync_CreatesAndListsSessions()
    {
        await using var context = CreateContext();
        var store = new SessionStore(context);

        Session session = await store.CreateSessionAsync("  Travel  ");

        Assert.Equal("Travel", session.DisplayName);
        Assert.NotEqual(Guid.Empty, session.SessionId);
        Assert.Equal(session.CreatedAt, session.LastActivityAt);
        Assert.Equal(session.SessionId, (await store.GetSessionAsync(session.SessionId))?.SessionId);
        Assert.Equal(session.SessionId, Assert.Single(await store.ListSessionsAsync()).SessionId);
    }

    [Fact]
    public async Task AppendMessagesAsync_AssignsSessionSequenceAndPreservesOrderedParts()
    {
        await using var context = CreateContext();
        var store = new SessionStore(context);
        Session session = await store.CreateSessionAsync("Travel");
        SessionRun run = await store.StartRunAsync(session.SessionId, Guid.NewGuid());

        IReadOnlyList<ConversationMessage> appended = await store.AppendMessagesAsync(session.SessionId,
        [
            new ConversationMessageDraft(ConversationMessageRole.User,
                [new TextMessagePartDraft("Find my train tickets." )], run.RunId),
            new ConversationMessageDraft(ConversationMessageRole.Assistant,
            [
                new TextMessagePartDraft("Searching."),
                new ToolCallMessagePartDraft("call-1", "SearchTransactions", "{\"query\":\"train\"}")
            ], run.RunId),
            new ConversationMessageDraft(ConversationMessageRole.Tool,
                [new ToolResultMessagePartDraft("call-1", "{\"matches\":[]}")], run.RunId)
        ]);

        Assert.Equal(new long[] { 1, 2, 3 }, appended.Select(message => message.SequenceNumber));
        IReadOnlyList<ConversationMessage> stored = await store.GetMessagesAsync(session.SessionId);
        Assert.Equal(new long[] { 1, 2, 3 }, stored.Select(message => message.SequenceNumber));
        Assert.Equal(ConversationMessagePartKind.Text, stored[1].Parts.First().Kind);
        Assert.Equal(ConversationMessagePartKind.ToolCall, stored[1].Parts.Last().Kind);
        Assert.Equal("call-1", stored[2].Parts.Single().CallId);
        Assert.Empty(await store.GetMessagesAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task StartRunAsync_EnforcesSingleActiveRunAndFinishAllowsNextRun()
    {
        await using var context = CreateContext();
        var store = new SessionStore(context);
        Session session = await store.CreateSessionAsync("Travel");
        SessionRun run = await store.StartRunAsync(session.SessionId, Guid.NewGuid());

        await Assert.ThrowsAsync<SessionRunAlreadyActiveException>(
            () => store.StartRunAsync(session.SessionId, Guid.NewGuid()));

        SessionRun finished = await store.FinishRunAsync(new SessionRun
        {
            RunId = run.RunId,
            SessionId = session.SessionId,
            StartedAt = run.StartedAt,
            EndedAt = DateTimeOffset.UtcNow,
            Status = SessionRunStatus.Completed,
            ModelStepCount = 2,
            ToolCallCount = 1,
            InputTokenCount = 40,
            OutputTokenCount = 12,
            TotalTokenCount = 52
        });

        Assert.Equal(SessionRunStatus.Completed, finished.Status);
        Assert.Equal(2, finished.ModelStepCount);
        Assert.Equal(52, finished.TotalTokenCount);
        await store.StartRunAsync(session.SessionId, Guid.NewGuid());
    }

    [Fact]
    public async Task RecoverInterruptedRunsAsync_MarksEveryRunningRunInterrupted()
    {
        await using var context = CreateContext();
        var store = new SessionStore(context);
        Session first = await store.CreateSessionAsync("First");
        Session second = await store.CreateSessionAsync("Second");
        await store.StartRunAsync(first.SessionId, Guid.NewGuid());
        await store.StartRunAsync(second.SessionId, Guid.NewGuid());
        DateTimeOffset endedAt = DateTimeOffset.UtcNow;

        int recovered = await store.RecoverInterruptedRunsAsync(endedAt);

        Assert.Equal(2, recovered);
        SessionRun[] runs = await context.SessionRuns.AsNoTracking().OrderBy(run => run.SessionId).ToArrayAsync();
        Assert.All(runs, run =>
        {
            Assert.Equal(SessionRunStatus.Interrupted, run.Status);
            Assert.Equal(endedAt, run.EndedAt);
        });
    }

    [Fact]
    public async Task SaveSummaryAsync_RequiresMonotonicVersionAndStoredCoverage()
    {
        await using var context = CreateContext();
        var store = new SessionStore(context);
        Session session = await store.CreateSessionAsync("Travel");
        await store.AppendMessagesAsync(session.SessionId,
        [
            new ConversationMessageDraft(ConversationMessageRole.User, [new TextMessagePartDraft("March travel")])
        ]);
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        await store.SaveSummaryAsync(new SessionSummary
        {
            SessionId = session.SessionId,
            SummaryText = "Asked about March travel.",
            CoveredThroughSequenceNumber = 1,
            Version = 1,
            CreatedAt = createdAt,
            ModelId = "test-model",
            PromptVersion = "summary-v1"
        });
        await store.SaveSummaryAsync(new SessionSummary
        {
            SessionId = session.SessionId,
            SummaryText = "Updated summary.",
            CoveredThroughSequenceNumber = 1,
            Version = 2,
            CreatedAt = createdAt.AddMinutes(1)
        });

        SessionSummary current = (await store.GetSummaryAsync(session.SessionId))!;
        Assert.Equal(2, current.Version);
        Assert.Equal(1, current.CoveredThroughSequenceNumber);
        Assert.Equal("Updated summary.", current.SummaryText);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveSummaryAsync(new SessionSummary
        {
            SessionId = session.SessionId,
            SummaryText = "Future coverage.",
            CoveredThroughSequenceNumber = 2,
            Version = 3,
            CreatedAt = createdAt.AddMinutes(2)
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveSummaryAsync(new SessionSummary
        {
            SessionId = session.SessionId,
            SummaryText = "Regressed coverage.",
            CoveredThroughSequenceNumber = 0,
            Version = 3,
            CreatedAt = createdAt.AddMinutes(2)
        }));
    }

    [Fact]
    public async Task AppendMessagesAsync_RejectsRunFromAnotherSession()
    {
        await using var context = CreateContext();
        var store = new SessionStore(context);
        Session owner = await store.CreateSessionAsync("Owner");
        Session other = await store.CreateSessionAsync("Other");
        SessionRun run = await store.StartRunAsync(owner.SessionId, Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendMessagesAsync(other.SessionId,
        [
            new ConversationMessageDraft(ConversationMessageRole.User,
                [new TextMessagePartDraft("Wrong session")], run.RunId)
        ]));
        Assert.Empty(await store.GetMessagesAsync(other.SessionId));
    }

    [Fact]
    public async Task SessionHistoryAndActiveRun_PersistAcrossRestartAndRecoverWithoutCrossSessionLeakage()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"expense-session-{Guid.NewGuid():N}.db");
        Guid firstSessionId;
        Guid secondSessionId;
        Guid interruptedRunId = Guid.NewGuid();
        try
        {
            await using (ExpenseDbContext context = CreateFileContext(databasePath))
            {
                await context.Database.EnsureCreatedAsync();
                var store = new SessionStore(context);
                Session first = await store.CreateSessionAsync("First");
                Session second = await store.CreateSessionAsync("Second");
                firstSessionId = first.SessionId;
                secondSessionId = second.SessionId;
                await store.StartRunAsync(firstSessionId, interruptedRunId);
                await store.AppendMessagesAsync(firstSessionId,
                [
                    new ConversationMessageDraft(ConversationMessageRole.User,
                        [new TextMessagePartDraft("SESSION_A_MARKER")], interruptedRunId),
                    new ConversationMessageDraft(ConversationMessageRole.Assistant,
                        [new ToolCallMessagePartDraft("unfinished-call", "SearchTransactions", "{}")] , interruptedRunId)
                ]);
                await store.AppendMessagesAsync(secondSessionId,
                [
                    new ConversationMessageDraft(ConversationMessageRole.User,
                        [new TextMessagePartDraft("SESSION_B_MARKER")])
                ]);
            }

            await using (ExpenseDbContext context = CreateFileContext(databasePath))
            {
                var store = new SessionStore(context);
                DateTimeOffset recoveredAt = DateTimeOffset.UtcNow;
                Assert.Equal(1, await store.RecoverInterruptedRunsAsync(recoveredAt));

                IReadOnlyList<ConversationMessage> firstMessages = await store.GetMessagesAsync(firstSessionId);
                IReadOnlyList<ConversationMessage> secondMessages = await store.GetMessagesAsync(secondSessionId);
                Assert.Equal(2, firstMessages.Count);
                Assert.Single(secondMessages);
                Assert.Contains(firstMessages.SelectMany(message => message.Parts),
                    part => part.Text == "SESSION_A_MARKER");
                Assert.DoesNotContain(firstMessages.SelectMany(message => message.Parts),
                    part => part.Text == "SESSION_B_MARKER");
                Assert.Contains(secondMessages.SelectMany(message => message.Parts),
                    part => part.Text == "SESSION_B_MARKER");
                Assert.DoesNotContain(secondMessages.SelectMany(message => message.Parts),
                    part => part.Text == "SESSION_A_MARKER");
                Assert.Equal("unfinished-call", Assert.Single(firstMessages[1].Parts).CallId);

                SessionRun run = await context.SessionRuns.AsNoTracking()
                    .SingleAsync(item => item.RunId == interruptedRunId);
                Assert.Equal(SessionRunStatus.Interrupted, run.Status);
                Assert.Equal(recoveredAt, run.EndedAt);
            }
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static ExpenseDbContext CreateContext()
    {
        var context = new ExpenseDbContext(new DbContextOptionsBuilder<ExpenseDbContext>()
            .UseSqlite("Data Source=:memory:;Foreign Keys=True").Options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    private static ExpenseDbContext CreateFileContext(string databasePath) =>
        new(new DbContextOptionsBuilder<ExpenseDbContext>()
            .UseSqlite($"Data Source={databasePath};Foreign Keys=True;Pooling=False")
            .Options);
}