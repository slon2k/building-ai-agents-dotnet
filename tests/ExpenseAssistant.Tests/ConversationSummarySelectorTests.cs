using ExpenseAssistant.App;
using ExpenseAssistant.Core;

namespace ExpenseAssistant.Tests;

public class ConversationSummarySelectorTests
{
	[Fact]
	public void Select_SummarizesOldCompletedRunsAndRetainsNewestRuns()
	{
		Guid sessionId = Guid.NewGuid();
		(SessionRun[] runs, ConversationMessage[] messages) = CreateRuns(sessionId, 4, SessionRunStatus.Completed);

		ConversationSummarySelection selection = new ConversationSummarySelector()
			.Select(sessionId, messages, runs, null, retainedCompletedRunCount: 2);

		Assert.Equal(new long[] { 1, 2, 3, 4 }, selection.SelectedSequenceNumbers);
		Assert.Equal(4, selection.CoveredThroughSequenceNumber);
		Assert.Equal(0, selection.PreviousCoverage);
	}

	[Fact]
	public void Select_StopsAtNonCompletedRunWithoutAdvancingCoveragePastIt()
	{
		Guid sessionId = Guid.NewGuid();
		(SessionRun[] runs, ConversationMessage[] messages) = CreateRuns(sessionId, 3,
			SessionRunStatus.Completed, SessionRunStatus.Interrupted, SessionRunStatus.Completed);

		ConversationSummarySelection selection = new ConversationSummarySelector()
			.Select(sessionId, messages, runs, null, retainedCompletedRunCount: 1);

		Assert.Equal(new long[] { 1, 2 }, selection.SelectedSequenceNumbers);
		Assert.Equal(2, selection.CoveredThroughSequenceNumber);
	}

	[Fact]
	public void Select_ContinuesAfterExistingCoverageAndRetainsMostRecentCompletedRun()
	{
		Guid sessionId = Guid.NewGuid();
		(SessionRun[] runs, ConversationMessage[] messages) = CreateRuns(sessionId, 3, SessionRunStatus.Completed);
		SessionSummary currentSummary = CreateSummary(sessionId, coveredThrough: 2);

		ConversationSummarySelection selection = new ConversationSummarySelector()
			.Select(sessionId, messages, runs, currentSummary, retainedCompletedRunCount: 1);

		Assert.Equal(new long[] { 3, 4 }, selection.SelectedSequenceNumbers);
		Assert.Equal(4, selection.CoveredThroughSequenceNumber);
		Assert.Equal(2, selection.PreviousCoverage);
	}

	[Fact]
	public void Select_DoesNotSelectRunWithIncompleteToolExchange()
	{
		Guid sessionId = Guid.NewGuid();
		Guid runId = Guid.NewGuid();
		SessionRun run = CreateRun(sessionId, runId, SessionRunStatus.Completed);
		ConversationMessage[] messages =
		[
			CreateMessage(sessionId, runId, 1, ConversationMessageRole.User, new TextMessagePartDraft("Question")),
			CreateMessage(sessionId, runId, 2, ConversationMessageRole.Assistant,
				new ToolCallMessagePartDraft("orphan-call", "Search", "{}"))
		];

		ConversationSummarySelection selection = new ConversationSummarySelector()
			.Select(sessionId, messages, [run], null, retainedCompletedRunCount: 1);

		Assert.Empty(selection.MessagesToSummarize);
		Assert.Equal(0, selection.CoveredThroughSequenceNumber);
	}

	[Fact]
	public void Select_RejectsCrossSessionRunOrMessage()
	{
		Guid sessionId = Guid.NewGuid();
		Guid otherSessionId = Guid.NewGuid();
		(SessionRun[] runs, ConversationMessage[] messages) = CreateRuns(otherSessionId, 1, SessionRunStatus.Completed);

		Assert.Throws<ArgumentException>(() => new ConversationSummarySelector()
			.Select(sessionId, messages, runs, null, retainedCompletedRunCount: 1));
		Assert.Throws<ArgumentException>(() => new ConversationSummarySelector()
			.Select(sessionId, [], runs, null, retainedCompletedRunCount: 1));
	}

	private static (SessionRun[] Runs, ConversationMessage[] Messages) CreateRuns(
		Guid sessionId, int count, params SessionRunStatus[] statuses)
	{
		var runs = new List<SessionRun>();
		var messages = new List<ConversationMessage>();
		for (int index = 0; index < count; index++)
		{
			Guid runId = Guid.NewGuid();
			SessionRunStatus status = statuses.Length == 1 ? statuses[0] : statuses[index];
			runs.Add(CreateRun(sessionId, runId, status));
			long sequence = index * 2L + 1;
			messages.Add(CreateMessage(sessionId, runId, sequence, ConversationMessageRole.User,
				new TextMessagePartDraft($"User {index + 1}")));
			messages.Add(CreateMessage(sessionId, runId, sequence + 1, ConversationMessageRole.Assistant,
				new TextMessagePartDraft($"Assistant {index + 1}")));
		}

		return (runs.ToArray(), messages.ToArray());
	}

	private static SessionRun CreateRun(Guid sessionId, Guid runId, SessionRunStatus status) => new()
	{
		RunId = runId,
		SessionId = sessionId,
		StartedAt = DateTimeOffset.UtcNow,
		EndedAt = status == SessionRunStatus.Running ? null : DateTimeOffset.UtcNow,
		Status = status
	};

	private static SessionSummary CreateSummary(Guid sessionId, long coveredThrough) => new()
	{
		SessionId = sessionId,
		SummaryText = "Existing summary",
		CoveredThroughSequenceNumber = coveredThrough,
		Version = 1,
		CreatedAt = DateTimeOffset.UtcNow
	};

	private static ConversationMessage CreateMessage(
		Guid sessionId, Guid? runId, long sequence, ConversationMessageRole role,
		params ConversationMessagePartDraft[] drafts)
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
			RunId = runId,
			SequenceNumber = sequence,
			Role = role,
			Parts = parts
		};
	}
}