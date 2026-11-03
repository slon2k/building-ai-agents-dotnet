using ExpenseAssistant.Core;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.App;

public sealed class ContextBuilder
{
    public ContextBuildResult Build(
        Guid sessionId, IReadOnlyList<ConversationMessage> messages, SessionSummary? summary)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(sessionId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Any(message => message.SessionId != sessionId))
        {
            throw new ArgumentException("All conversation messages must belong to the requested session.", nameof(messages));
        }

        if (summary is not null)
        {
            if (summary.SessionId != sessionId)
            {
                throw new ArgumentException("The summary must belong to the requested session.", nameof(summary));
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(summary.SummaryText);
            ArgumentOutOfRangeException.ThrowIfNegative(summary.CoveredThroughSequenceNumber);
        }

        ConversationMessage[] orderedMessages = messages.OrderBy(message => message.SequenceNumber).ToArray();
        if (orderedMessages.Any(message => message.SequenceNumber <= 0)
            || orderedMessages.Select(message => message.SequenceNumber).Distinct().Count() != orderedMessages.Length)
        {
            throw new ArgumentException("Message sequence numbers must be positive and unique within the session.", nameof(messages));
        }

        long coveredThrough = summary?.CoveredThroughSequenceNumber ?? 0;
        if (coveredThrough > (orderedMessages.LastOrDefault()?.SequenceNumber ?? 0))
        {
            throw new ArgumentException("Summary coverage cannot exceed the supplied session history.", nameof(summary));
        }
        if (SplitsToolExchange(orderedMessages, coveredThrough))
        {
            throw new ArgumentException("Summary coverage cannot split a completed tool exchange.", nameof(summary));
        }

        ConversationMessage[] eligibleMessages = orderedMessages
            .Where(message => message.SequenceNumber > coveredThrough)
            .ToArray();
        IReadOnlyList<ChatMessage> priorMessages = ConversationMessageMapper.ToChatMessages(
            eligibleMessages, out IReadOnlyList<long> includedSequenceNumbers);

        if (summary is not null)
        {
            var summaryMessage = new ChatMessage(ChatRole.User,
                $"[Conversation summary through message {coveredThrough}]\n{summary.SummaryText}");
            priorMessages = new[] { summaryMessage }.Concat(priorMessages).ToArray();
        }

        return new ContextBuildResult(priorMessages, coveredThrough, includedSequenceNumbers);
    }

    private static bool SplitsToolExchange(IReadOnlyList<ConversationMessage> messages, long coveredThrough)
    {
        for (int index = 0; index + 1 < messages.Count; index++)
        {
            ConversationMessage assistant = messages[index];
            ConversationMessage tool = messages[index + 1];
            if (assistant.Role != ConversationMessageRole.Assistant
                || tool.Role != ConversationMessageRole.Tool
                || assistant.SequenceNumber > coveredThrough
                || tool.SequenceNumber <= coveredThrough)
            {
                continue;
            }

            string[] callIds = assistant.Parts
                .Where(part => part.Kind == ConversationMessagePartKind.ToolCall)
                .OrderBy(part => part.Ordinal)
                .Select(part => part.CallId!)
                .ToArray();
            string[] resultIds = tool.Parts
                .Where(part => part.Kind == ConversationMessagePartKind.ToolResult)
                .OrderBy(part => part.Ordinal)
                .Select(part => part.CallId!)
                .ToArray();
            if (callIds.Length > 0 && callIds.SequenceEqual(resultIds, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed record ContextBuildResult(
    IReadOnlyList<ChatMessage> PriorMessages,
    long SummaryCoveredThroughSequenceNumber,
    IReadOnlyList<long> IncludedSequenceNumbers);