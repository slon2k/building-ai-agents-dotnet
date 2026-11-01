namespace ExpenseAssistant.Core;

public sealed class SessionRunAlreadyActiveException(Guid sessionId, Exception? innerException = null)
    : InvalidOperationException($"Session '{sessionId}' already has an active run.", innerException)
{
    public Guid SessionId { get; } = sessionId;
}