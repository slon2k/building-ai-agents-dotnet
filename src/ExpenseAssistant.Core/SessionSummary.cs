namespace ExpenseAssistant.Core;

public sealed class SessionSummary
{
    public required Guid SessionId { get; set; }

    public required string SummaryText { get; set; }

    public required long CoveredThroughSequenceNumber { get; set; }

    public required int Version { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    public string? ModelId { get; set; }

    public string? PromptVersion { get; set; }
}