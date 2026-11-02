namespace ExpenseAssistant.Core;

public enum ConversationMessagePartKind
{
    Text,
    ToolCall,
    ToolResult
}

public sealed class ConversationMessagePart
{
    public required Guid MessageId { get; set; }

    public required int Ordinal { get; set; }

    public required ConversationMessagePartKind Kind { get; set; }

    public string? Text { get; set; }

    public string? CallId { get; set; }

    public string? ToolName { get; set; }

    public string? ArgumentsJson { get; set; }

    public string? ResultJson { get; set; }
}