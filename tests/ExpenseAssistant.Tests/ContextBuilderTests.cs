using ExpenseAssistant.App;
using ExpenseAssistant.Core;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Tests;

public class ContextBuilderTests
{
    [Fact]
    public void Build_WithSummary_ExcludesCoveredMessagesAndReportsIncludedSequences()
    {
        Guid sessionId = Guid.NewGuid();
        ConversationMessage[] messages = Enumerable.Range(1, 8)
            .Select(sequence => TextMessage(sessionId, sequence,
                sequence % 2 == 1 ? ConversationMessageRole.User : ConversationMessageRole.Assistant,
                $"MESSAGE_{sequence}"))
            .ToArray();
        var summary = Summary(sessionId, "SUMMARY_MARKER covers older discussion", coveredThrough: 4);

        ContextBuildResult result = new ContextBuilder().Build(sessionId, messages, summary);

        Assert.Equal(4, result.SummaryCoveredThroughSequenceNumber);
        Assert.Equal(new long[] { 5, 6, 7, 8 }, result.IncludedSequenceNumbers);
        Assert.Equal(5, result.PriorMessages.Count);
        Assert.Equal(ChatRole.User, result.PriorMessages[0].Role);
        Assert.Contains("SUMMARY_MARKER", result.PriorMessages[0].Text);
        Assert.Contains("through message 4", result.PriorMessages[0].Text);
        Assert.Equal(new[] { "MESSAGE_5", "MESSAGE_6", "MESSAGE_7", "MESSAGE_8" },
            result.PriorMessages.Skip(1).Select(message => message.Text));
        Assert.DoesNotContain(result.PriorMessages, message => message.Text.Contains("MESSAGE_1", StringComparison.Ordinal));
        Assert.DoesNotContain(result.PriorMessages, message => message.Text.Contains("MESSAGE_4", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WithCompleteMultiCallExchange_PreservesOrderAndBothSequences()
    {
        Guid sessionId = Guid.NewGuid();
        ConversationMessage[] messages =
        [
            TextMessage(sessionId, 1, ConversationMessageRole.User, "Question"),
            Message(sessionId, 2, ConversationMessageRole.Assistant,
                new TextMessagePartDraft("Searching"),
                new ToolCallMessagePartDraft("call-a", "Search", "{}"),
                new ToolCallMessagePartDraft("call-b", "Summary", "{}")),
            Message(sessionId, 3, ConversationMessageRole.Tool,
                new ToolResultMessagePartDraft("call-a", "{\"result\":1}"),
                new ToolResultMessagePartDraft("call-b", "{\"result\":2}")),
            TextMessage(sessionId, 4, ConversationMessageRole.Assistant, "Done")
        ];

        ContextBuildResult result = new ContextBuilder().Build(sessionId, messages.Reverse().ToArray(), null);

        Assert.Equal(new long[] { 1, 2, 3, 4 }, result.IncludedSequenceNumbers);
        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant },
            result.PriorMessages.Select(message => message.Role));
        Assert.Equal(new[] { "call-a", "call-b" },
            result.PriorMessages[2].Contents.OfType<FunctionResultContent>().Select(result => result.CallId));
    }

    [Fact]
    public void Build_ExcludesIncompleteToolExchangeFromMessagesAndSequenceDiagnostics()
    {
        Guid sessionId = Guid.NewGuid();
        ConversationMessage[] messages =
        [
            TextMessage(sessionId, 1, ConversationMessageRole.User, "Question"),
            Message(sessionId, 2, ConversationMessageRole.Assistant,
                new ToolCallMessagePartDraft("orphan-call", "Search", "{}"))
        ];

        ContextBuildResult result = new ContextBuilder().Build(sessionId, messages, null);

        Assert.Equal(new long[] { 1 }, result.IncludedSequenceNumbers);
        Assert.Single(result.PriorMessages);
        Assert.Equal("Question", result.PriorMessages[0].Text);
    }

    [Fact]
    public void Build_RejectsMessagesOrSummaryFromAnotherSession()
    {
        Guid sessionId = Guid.NewGuid();
        Guid otherSessionId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => new ContextBuilder().Build(sessionId,
            [TextMessage(otherSessionId, 1, ConversationMessageRole.User, "OTHER_SESSION_MARKER")], null));
        Assert.Throws<ArgumentException>(() => new ContextBuilder().Build(sessionId, [],
            Summary(otherSessionId, "OTHER_SUMMARY_MARKER", coveredThrough: 0)));
    }

    [Fact]
    public void Build_RejectsSummaryCoverageThatSplitsToolExchange()
    {
        Guid sessionId = Guid.NewGuid();
        ConversationMessage[] messages =
        [
            TextMessage(sessionId, 1, ConversationMessageRole.User, "Question"),
            Message(sessionId, 2, ConversationMessageRole.Assistant,
                new ToolCallMessagePartDraft("call-1", "Search", "{}")),
            Message(sessionId, 3, ConversationMessageRole.Tool,
                new ToolResultMessagePartDraft("call-1", "{\"result\":1}"))
        ];

        Assert.Throws<ArgumentException>(() => new ContextBuilder().Build(
            sessionId, messages, Summary(sessionId, "Unsafe coverage", coveredThrough: 2)));
    }

    private static SessionSummary Summary(Guid sessionId, string text, long coveredThrough) => new()
    {
        SessionId = sessionId,
        SummaryText = text,
        CoveredThroughSequenceNumber = coveredThrough,
        Version = 1,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static ConversationMessage TextMessage(
        Guid sessionId, long sequence, ConversationMessageRole role, string text) =>
        Message(sessionId, sequence, role, new TextMessagePartDraft(text));

    private static ConversationMessage Message(
        Guid sessionId, long sequence, ConversationMessageRole role, params ConversationMessagePartDraft[] drafts)
    {
        Guid messageId = Guid.NewGuid();
        ConversationMessagePart[] parts = drafts.Select((draft, ordinal) => draft switch
        {
            TextMessagePartDraft text => new ConversationMessagePart
            {
                MessageId = messageId,
                Ordinal = ordinal,
                Kind = ConversationMessagePartKind.Text,
                Text = text.Text
            },
            ToolCallMessagePartDraft call => new ConversationMessagePart
            {
                MessageId = messageId,
                Ordinal = ordinal,
                Kind = ConversationMessagePartKind.ToolCall,
                CallId = call.CallId,
                ToolName = call.ToolName,
                ArgumentsJson = call.ArgumentsJson
            },
            ToolResultMessagePartDraft result => new ConversationMessagePart
            {
                MessageId = messageId,
                Ordinal = ordinal,
                Kind = ConversationMessagePartKind.ToolResult,
                CallId = result.CallId,
                ResultJson = result.ResultJson
            },
            _ => throw new ArgumentException("Unsupported message part.", nameof(drafts))
        }).ToArray();

        return new ConversationMessage
        {
            MessageId = messageId,
            SessionId = sessionId,
            SequenceNumber = sequence,
            Role = role,
            Parts = parts
        };
    }
}