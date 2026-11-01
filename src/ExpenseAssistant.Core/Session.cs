namespace ExpenseAssistant.Core;

public sealed class Session
{
    public required Guid SessionId { get; set; }

    public required string DisplayName { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    public required DateTimeOffset LastActivityAt { get; set; }
}

public enum ConversationMessageRole
{
    System,
    User,
    Assistant,
    Tool
}

public sealed record ConversationMessageDraft(
    ConversationMessageRole Role,
    int ContentVersion,
    string ContentJson);

public sealed class ConversationMessage
{
    public required Guid MessageId { get; set; }

    public required Guid SessionId { get; set; }

    public Guid? RunId { get; set; }

    public required long SequenceNumber { get; set; }

    public required ConversationMessageRole Role { get; set; }

    public required int ContentVersion { get; set; }

    public required string ContentJson { get; set; }
}