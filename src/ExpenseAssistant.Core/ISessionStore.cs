namespace ExpenseAssistant.Core;

public interface ISessionStore
{
    Task<Session> CreateSessionAsync(string displayName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Session>> ListSessionsAsync(CancellationToken cancellationToken = default);

    Task<Session?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        Guid sessionId,
        long afterSequenceNumber = 0,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationMessage>> AppendMessagesAsync(
        Guid sessionId,
        IReadOnlyList<ConversationMessageDraft> messages,
        CancellationToken cancellationToken = default);

    Task<SessionSummary?> GetSummaryAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task SaveSummaryAsync(SessionSummary summary, CancellationToken cancellationToken = default);

    Task<SessionRun> StartRunAsync(Guid sessionId, Guid runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionRun>> GetRunsAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<SessionRun> FinishRunAsync(SessionRun finishedRun, CancellationToken cancellationToken = default);

    Task<int> RecoverInterruptedRunsAsync(DateTimeOffset endedAt, CancellationToken cancellationToken = default);
}