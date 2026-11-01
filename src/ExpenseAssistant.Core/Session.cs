namespace ExpenseAssistant.Core;

public sealed class Session
{
    public required Guid SessionId { get; set; }

    public required string DisplayName { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    public required DateTimeOffset LastActivityAt { get; set; }
}