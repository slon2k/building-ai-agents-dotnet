using System.Text.Json;
using ExpenseAssistant.Core;
using Microsoft.EntityFrameworkCore;

namespace ExpenseAssistant.Infrastructure;

public sealed class SessionStore(ExpenseDbContext context) : ISessionStore
{
    public async Task<Session> CreateSessionAsync(string displayName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var session = new Session
        {
            SessionId = Guid.NewGuid(),
            DisplayName = displayName.Trim(),
            CreatedAt = now,
            LastActivityAt = now
        };

        context.Sessions.Add(session);
        await context.SaveChangesAsync(cancellationToken);
        return session;
    }

    public async Task<IReadOnlyList<Session>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        List<Session> sessions = await context.Sessions.AsNoTracking()
            .OrderBy(session => session.SessionId)
            .ToListAsync(cancellationToken);
        return sessions.OrderByDescending(session => session.LastActivityAt)
            .ThenBy(session => session.SessionId)
            .ToArray();
    }

    public Task<Session?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        context.Sessions.AsNoTracking().SingleOrDefaultAsync(session => session.SessionId == sessionId, cancellationToken);

    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        Guid sessionId,
        long afterSequenceNumber = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequenceNumber);

        List<ConversationMessage> messages = await context.ConversationMessages.AsNoTracking()
            .Where(message => message.SessionId == sessionId && message.SequenceNumber > afterSequenceNumber)
            .Include(message => message.Parts)
            .OrderBy(message => message.SequenceNumber)
            .ToListAsync(cancellationToken);

        foreach (ConversationMessage message in messages)
        {
            message.Parts = message.Parts.OrderBy(part => part.Ordinal).ToList();
        }

        return messages;
    }

    public async Task<IReadOnlyList<ConversationMessage>> AppendMessagesAsync(
        Guid sessionId,
        IReadOnlyList<ConversationMessageDraft> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            throw new ArgumentException("At least one message is required.", nameof(messages));
        }

        foreach (ConversationMessageDraft message in messages)
        {
            ValidateDraft(message);
        }

        Guid? runId = messages[0].RunId;
        if (messages.Any(message => message.RunId != runId))
        {
            throw new ArgumentException("A message batch must belong to one run.", nameof(messages));
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        Session session = await context.Sessions.SingleOrDefaultAsync(
            entity => entity.SessionId == sessionId, cancellationToken)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");

        if (runId is Guid activeRunId)
        {
            SessionRun? run = await context.SessionRuns.SingleOrDefaultAsync(
                entity => entity.SessionId == sessionId && entity.RunId == activeRunId, cancellationToken);
            if (run is null || run.Status != SessionRunStatus.Running)
            {
                throw new InvalidOperationException("Messages can only be appended to a running session run.");
            }
        }

        long? lastSequenceNumber = await context.ConversationMessages
            .Where(message => message.SessionId == sessionId)
            .Select(message => (long?)message.SequenceNumber)
            .MaxAsync(cancellationToken);
        long nextSequenceNumber = (lastSequenceNumber ?? 0) + 1;
        var persistedMessages = new List<ConversationMessage>(messages.Count);

        foreach (ConversationMessageDraft draft in messages)
        {
            var message = new ConversationMessage
            {
                MessageId = Guid.NewGuid(),
                SessionId = sessionId,
                RunId = draft.RunId,
                SequenceNumber = nextSequenceNumber++,
                Role = draft.Role
            };
            AddParts(message, draft.Parts);
            persistedMessages.Add(message);
        }

        session.LastActivityAt = DateTimeOffset.UtcNow;
        context.ConversationMessages.AddRange(persistedMessages);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return persistedMessages;
    }

    public Task<SessionSummary?> GetSummaryAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        context.SessionSummaries.AsNoTracking()
            .SingleOrDefaultAsync(summary => summary.SessionId == sessionId, cancellationToken);

    public async Task SaveSummaryAsync(SessionSummary summary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary.SummaryText);
        ArgumentOutOfRangeException.ThrowIfNegative(summary.CoveredThroughSequenceNumber);
        ArgumentOutOfRangeException.ThrowIfLessThan(summary.Version, 1);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        bool sessionExists = await context.Sessions.AnyAsync(
            session => session.SessionId == summary.SessionId, cancellationToken);
        if (!sessionExists)
        {
            throw new KeyNotFoundException($"Session '{summary.SessionId}' does not exist.");
        }

        long? lastSequenceNumber = await context.ConversationMessages
            .Where(message => message.SessionId == summary.SessionId)
            .Select(message => (long?)message.SequenceNumber)
            .MaxAsync(cancellationToken);
        if (summary.CoveredThroughSequenceNumber > (lastSequenceNumber ?? 0))
        {
            throw new ArgumentOutOfRangeException(nameof(summary), "Summary coverage cannot exceed stored message history.");
        }

        SessionSummary? current = await context.SessionSummaries.SingleOrDefaultAsync(
            entity => entity.SessionId == summary.SessionId, cancellationToken);
        if (current is null)
        {
            if (summary.Version != 1)
            {
                throw new InvalidOperationException("The first session summary must have version 1.");
            }
            context.SessionSummaries.Add(summary);
        }
        else
        {
            if (summary.Version != current.Version + 1)
            {
                throw new InvalidOperationException("A replacement summary must increment the current version by one.");
            }
            if (summary.CoveredThroughSequenceNumber < current.CoveredThroughSequenceNumber)
            {
                throw new InvalidOperationException("Summary coverage cannot move backwards.");
            }

            current.SummaryText = summary.SummaryText;
            current.CoveredThroughSequenceNumber = summary.CoveredThroughSequenceNumber;
            current.Version = summary.Version;
            current.CreatedAt = summary.CreatedAt;
            current.ModelId = summary.ModelId;
            current.PromptVersion = summary.PromptVersion;
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<SessionRun> StartRunAsync(
        Guid sessionId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        if (!await context.Sessions.AnyAsync(session => session.SessionId == sessionId, cancellationToken))
        {
            throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");
        }

        var run = new SessionRun
        {
            RunId = runId,
            SessionId = sessionId,
            StartedAt = DateTimeOffset.UtcNow,
            Status = SessionRunStatus.Running
        };
        context.SessionRuns.Add(run);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return run;
        }
        catch (DbUpdateException exception)
        {
            context.Entry(run).State = EntityState.Detached;
            bool anotherRunIsActive = await context.SessionRuns.AsNoTracking().AnyAsync(
                existing => existing.SessionId == sessionId && existing.Status == SessionRunStatus.Running,
                cancellationToken);
            if (anotherRunIsActive)
            {
                throw new SessionRunAlreadyActiveException(sessionId, exception);
            }
            throw;
        }
    }

    public async Task<SessionRun> FinishRunAsync(
        SessionRun finishedRun,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finishedRun);
        if (finishedRun.Status == SessionRunStatus.Running)
        {
            throw new ArgumentException("A finished run must have a terminal status.", nameof(finishedRun));
        }
        if (finishedRun.EndedAt is null)
        {
            throw new ArgumentException("A finished run must have an end timestamp.", nameof(finishedRun));
        }
        ValidateNonNegative(finishedRun);

        SessionRun existing = await context.SessionRuns.SingleOrDefaultAsync(
            run => run.SessionId == finishedRun.SessionId && run.RunId == finishedRun.RunId,
            cancellationToken)
            ?? throw new KeyNotFoundException($"Run '{finishedRun.RunId}' does not exist in session '{finishedRun.SessionId}'.");
        if (existing.Status != SessionRunStatus.Running)
        {
            throw new InvalidOperationException("Only a running session run can be finished.");
        }

        existing.Status = finishedRun.Status;
        existing.EndedAt = finishedRun.EndedAt;
        existing.StopReason = finishedRun.StopReason;
        existing.ModelStepCount = finishedRun.ModelStepCount;
        existing.ToolCallCount = finishedRun.ToolCallCount;
        existing.InputTokenCount = finishedRun.InputTokenCount;
        existing.OutputTokenCount = finishedRun.OutputTokenCount;
        existing.TotalTokenCount = finishedRun.TotalTokenCount;
        await context.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public Task<int> RecoverInterruptedRunsAsync(
        DateTimeOffset endedAt,
        CancellationToken cancellationToken = default)
    {
        if (endedAt == default)
        {
            throw new ArgumentException("An interruption timestamp is required.", nameof(endedAt));
        }

        return context.SessionRuns.Where(run => run.Status == SessionRunStatus.Running)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.Status, SessionRunStatus.Interrupted)
                .SetProperty(run => run.EndedAt, endedAt), cancellationToken);
    }

    private static void ValidateDraft(ConversationMessageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(draft.Parts);
        if (!Enum.IsDefined(draft.Role))
        {
            throw new ArgumentOutOfRangeException(nameof(draft), "The message role is not supported.");
        }
        if (draft.Parts.Count == 0)
        {
            throw new ArgumentException("A conversation message must contain at least one part.", nameof(draft));
        }

        bool roleAcceptsParts = draft.Role switch
        {
            ConversationMessageRole.User => draft.Parts.All(part => part is TextMessagePartDraft),
            ConversationMessageRole.Assistant => draft.Parts.All(part => part is TextMessagePartDraft or ToolCallMessagePartDraft),
            ConversationMessageRole.Tool => draft.Parts.All(part => part is ToolResultMessagePartDraft),
            _ => false
        };
        if (!roleAcceptsParts)
        {
            throw new ArgumentException("Message parts are not valid for the message role.", nameof(draft));
        }

        HashSet<string> callIds = new(StringComparer.Ordinal);
        foreach (ConversationMessagePartDraft part in draft.Parts)
        {
            switch (part)
            {
                case TextMessagePartDraft text:
                    ArgumentNullException.ThrowIfNull(text.Text);
                    break;
                case ToolCallMessagePartDraft call:
                    ArgumentException.ThrowIfNullOrWhiteSpace(call.CallId);
                    ArgumentException.ThrowIfNullOrWhiteSpace(call.ToolName);
                    ValidateJson(call.ArgumentsJson, requireObject: true);
                    if (!callIds.Add(call.CallId))
                    {
                        throw new ArgumentException("Tool-call IDs must be unique within a message.", nameof(draft));
                    }
                    break;
                case ToolResultMessagePartDraft result:
                    ArgumentException.ThrowIfNullOrWhiteSpace(result.CallId);
                    ValidateJson(result.ResultJson, requireObject: false);
                    if (!callIds.Add(result.CallId))
                    {
                        throw new ArgumentException("Tool-result IDs must be unique within a message.", nameof(draft));
                    }
                    break;
                default:
                    throw new ArgumentException("The message part type is not supported.", nameof(draft));
            }
        }
    }

    private static void AddParts(ConversationMessage message, IReadOnlyList<ConversationMessagePartDraft> drafts)
    {
        for (int ordinal = 0; ordinal < drafts.Count; ordinal++)
        {
            ConversationMessagePartDraft draft = drafts[ordinal];
            ConversationMessagePart part = draft switch
            {
                TextMessagePartDraft text => new ConversationMessagePart
                {
                    MessageId = message.MessageId,
                    Ordinal = ordinal,
                    Kind = ConversationMessagePartKind.Text,
                    Text = text.Text
                },
                ToolCallMessagePartDraft call => new ConversationMessagePart
                {
                    MessageId = message.MessageId,
                    Ordinal = ordinal,
                    Kind = ConversationMessagePartKind.ToolCall,
                    CallId = call.CallId,
                    ToolName = call.ToolName,
                    ArgumentsJson = call.ArgumentsJson
                },
                ToolResultMessagePartDraft result => new ConversationMessagePart
                {
                    MessageId = message.MessageId,
                    Ordinal = ordinal,
                    Kind = ConversationMessagePartKind.ToolResult,
                    CallId = result.CallId,
                    ResultJson = result.ResultJson
                },
                _ => throw new ArgumentException("The message part type is not supported.", nameof(drafts))
            };
            message.Parts.Add(part);
        }
    }

    private static void ValidateJson(string json, bool requireObject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using JsonDocument document = JsonDocument.Parse(json);
        if (requireObject && document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool-call arguments must be a JSON object.", nameof(json));
        }
    }

    private static void ValidateNonNegative(SessionRun run)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(run.ModelStepCount);
        ArgumentOutOfRangeException.ThrowIfNegative(run.ToolCallCount);
        if (run.InputTokenCount < 0 || run.OutputTokenCount < 0 || run.TotalTokenCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(run), "Token counts cannot be negative.");
        }
    }
}