namespace ExpenseAssistant.Core;

public enum ConversationMessageRole
{
    User,
    Assistant,
    Tool
}

public sealed record ConversationMessageDraft(
    ConversationMessageRole Role,
    IReadOnlyList<ConversationMessagePartDraft> Parts,
    Guid? RunId = null);

public abstract record ConversationMessagePartDraft;

public sealed record TextMessagePartDraft(string Text) : ConversationMessagePartDraft;

public sealed record ToolCallMessagePartDraft(
    string CallId,
    string ToolName,
    string ArgumentsJson) : ConversationMessagePartDraft;

public sealed record ToolResultMessagePartDraft(
    string CallId,
    string ResultJson) : ConversationMessagePartDraft;

public sealed class ConversationMessage
{
    public required Guid MessageId { get; set; }

    public required Guid SessionId { get; set; }

    public Guid? RunId { get; set; }

    public required long SequenceNumber { get; set; }

    public required ConversationMessageRole Role { get; set; }

    public ICollection<ConversationMessagePart> Parts { get; set; } = new List<ConversationMessagePart>();
}