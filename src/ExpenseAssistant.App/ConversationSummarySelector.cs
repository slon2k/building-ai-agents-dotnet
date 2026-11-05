using ExpenseAssistant.Core;

namespace ExpenseAssistant.App;

public sealed class ConversationSummarySelector
{
	public ConversationSummarySelection Select(
		Guid sessionId,
		IReadOnlyList<ConversationMessage> messages,
		IReadOnlyList<SessionRun> runs,
		SessionSummary? currentSummary,
		int retainedCompletedRunCount)
	{
		ArgumentOutOfRangeException.ThrowIfEqual(sessionId, Guid.Empty);
		ArgumentNullException.ThrowIfNull(messages);
		ArgumentNullException.ThrowIfNull(runs);
		ArgumentOutOfRangeException.ThrowIfLessThan(retainedCompletedRunCount, 1);

		if (messages.Any(message => message.SessionId != sessionId))
		{
			throw new ArgumentException("All conversation messages must belong to the requested session.", nameof(messages));
		}
		if (runs.Any(run => run.SessionId != sessionId))
		{
			throw new ArgumentException("All session runs must belong to the requested session.", nameof(runs));
		}
		if (runs.Select(run => run.RunId).Distinct().Count() != runs.Count)
		{
			throw new ArgumentException("Run identifiers must be unique within the supplied session runs.", nameof(runs));
		}
		if (currentSummary is not null)
		{
			if (currentSummary.SessionId != sessionId)
			{
				throw new ArgumentException("The summary must belong to the requested session.", nameof(currentSummary));
			}
			ArgumentOutOfRangeException.ThrowIfNegative(currentSummary.CoveredThroughSequenceNumber);
		}

		ConversationMessage[] orderedMessages = messages.OrderBy(message => message.SequenceNumber).ToArray();
		if (orderedMessages.Any(message => message.SequenceNumber <= 0)
			|| orderedMessages.Select(message => message.SequenceNumber).Distinct().Count() != orderedMessages.Length)
		{
			throw new ArgumentException("Message sequence numbers must be positive and unique within the session.", nameof(messages));
		}

		long previousCoverage = currentSummary?.CoveredThroughSequenceNumber ?? 0;
		if (previousCoverage > (orderedMessages.LastOrDefault()?.SequenceNumber ?? 0))
		{
			throw new ArgumentException("Summary coverage cannot exceed the supplied session history.", nameof(currentSummary));
		}

		Dictionary<Guid, SessionRun> runsById = runs.ToDictionary(run => run.RunId);
		List<RunBlock> blocks = CreateBlocks(orderedMessages, runsById);
		if (blocks.Any(block => block.Messages.Any(message => message.SequenceNumber <= previousCoverage)
			&& block.Messages.Any(message => message.SequenceNumber > previousCoverage)))
		{
			throw new ArgumentException("Summary coverage cannot split a run.", nameof(currentSummary));
		}

		RunBlock[] completedBlocks = blocks
			.Where(block => block.LastSequenceNumber > previousCoverage && block.IsCompleted && block.HasValidToolExchanges)
			.ToArray();
		HashSet<Guid> retainedRunIds = completedBlocks
			.TakeLast(retainedCompletedRunCount)
			.Select(block => block.RunId!.Value)
			.ToHashSet();

		var selectedMessages = new List<ConversationMessage>();
		long coveredThrough = previousCoverage;
		foreach (RunBlock block in blocks.Where(block => block.LastSequenceNumber > previousCoverage))
		{
			if (!block.IsCompleted || !block.HasValidToolExchanges || retainedRunIds.Contains(block.RunId!.Value))
			{
				break;
			}

			selectedMessages.AddRange(block.Messages);
			coveredThrough = block.LastSequenceNumber;
		}

		return new ConversationSummarySelection(
			selectedMessages.AsReadOnly(),
			previousCoverage,
			coveredThrough,
			selectedMessages.Select(message => message.SequenceNumber).ToArray());
	}

	private static List<RunBlock> CreateBlocks(
		IReadOnlyList<ConversationMessage> orderedMessages,
		IReadOnlyDictionary<Guid, SessionRun> runsById)
	{
		var blocks = new List<RunBlock>();
		var seenRunIds = new HashSet<Guid>();
		int index = 0;
		while (index < orderedMessages.Count)
		{
			Guid? runId = orderedMessages[index].RunId;
			var blockMessages = new List<ConversationMessage> { orderedMessages[index++] };
			while (index < orderedMessages.Count && orderedMessages[index].RunId == runId)
			{
				blockMessages.Add(orderedMessages[index++]);
			}

			if (runId is Guid id && !seenRunIds.Add(id))
			{
				throw new ArgumentException("Messages for a run must form one contiguous sequence block.", nameof(orderedMessages));
			}

			bool isCompleted = runId is Guid completedRunId
				&& runsById.TryGetValue(completedRunId, out SessionRun? run)
				&& run.Status == SessionRunStatus.Completed
				&& run.EndedAt is not null;
			blocks.Add(new RunBlock(runId, blockMessages, isCompleted, HasValidToolExchanges(blockMessages)));
		}

		return blocks;
	}

	private static bool HasValidToolExchanges(IReadOnlyList<ConversationMessage> messages)
	{
		for (int index = 0; index < messages.Count; index++)
		{
			ConversationMessage message = messages[index];
			ConversationMessagePart[] calls = message.Parts
				.Where(part => part.Kind == ConversationMessagePartKind.ToolCall)
				.OrderBy(part => part.Ordinal)
				.ToArray();
			if (calls.Length > 0)
			{
				if (index + 1 >= messages.Count || messages[index + 1].Role != ConversationMessageRole.Tool)
				{
					return false;
				}

				string[] callIds = calls.Select(part => part.CallId!).ToArray();
				string[] resultIds = messages[++index].Parts
					.Where(part => part.Kind == ConversationMessagePartKind.ToolResult)
					.OrderBy(part => part.Ordinal)
					.Select(part => part.CallId!)
					.ToArray();
				if (!callIds.SequenceEqual(resultIds, StringComparer.Ordinal))
				{
					return false;
				}
			}
			else if (message.Role == ConversationMessageRole.Tool
				|| message.Parts.Any(part => part.Kind == ConversationMessagePartKind.ToolResult))
			{
				return false;
			}
		}

		return true;
	}

	private sealed record RunBlock(
		Guid? RunId,
		IReadOnlyList<ConversationMessage> Messages,
		bool IsCompleted,
		bool HasValidToolExchanges)
	{
		public long LastSequenceNumber => Messages[^1].SequenceNumber;
	}
}

public sealed record ConversationSummarySelection(
	IReadOnlyList<ConversationMessage> MessagesToSummarize,
	long PreviousCoverage,
	long CoveredThroughSequenceNumber,
	IReadOnlyList<long> SelectedSequenceNumbers)
{
	public bool HasMessages => MessagesToSummarize.Count > 0;
}