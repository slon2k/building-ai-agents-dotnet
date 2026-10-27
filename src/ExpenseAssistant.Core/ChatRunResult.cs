using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Core;

public enum ChatRunStatus
{
    Completed,
    Cancelled,
    TimedOut,
    Failed
}

public sealed record ChatRunResult(
    Guid SessionId,
    Guid RunId,
    ChatRunStatus Status,
    TimeSpan Duration,
    ChatResponse? Response = null,
    string? Error = null);
