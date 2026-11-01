namespace ExpenseAssistant.Core;

public sealed class SessionRun
{
    public required Guid RunId { get; set; }

    public required Guid SessionId { get; set; }

    public required DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public required SessionRunStatus Status { get; set; }

    public SessionRunStopReason? StopReason { get; set; }

    public int ModelStepCount { get; set; }

    public int ToolCallCount { get; set; }

    public long? InputTokenCount { get; set; }

    public long? OutputTokenCount { get; set; }

    public long? TotalTokenCount { get; set; }
}

public enum SessionRunStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
    BudgetExceeded,
    Interrupted
}

public enum SessionRunStopReason
{
    ModelStepLimit,
    ToolCallLimit,
    DurationLimit,
    ContextLimit
}