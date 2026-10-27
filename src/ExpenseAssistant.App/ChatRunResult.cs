using Microsoft.Extensions.AI;

namespace ExpenseAssistant.App;

internal enum ChatRunStatus
{
    Completed,
    Cancelled,
    TimedOut,
    Failed
}

internal sealed record ChatRunResult(
    Guid SessionId,
    Guid RunId,
    ChatRunStatus Status,
    TimeSpan Duration,
    ChatResponse? Response = null,
    string? Error = null);
