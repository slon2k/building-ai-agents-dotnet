using ExpenseAssistant.Core;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.App;

public interface IConversationSummarizer
{
	Task<GeneratedConversationSummary> SummarizeAsync(
		ConversationSummaryRequest request, CancellationToken cancellationToken);
}

public sealed record ConversationSummaryRequest(
	Guid SessionId,
	string? PreviousSummary,
	long PreviouslyCoveredThroughSequenceNumber,
	IReadOnlyList<ConversationMessage> Messages);

public sealed record GeneratedConversationSummary(
	string SummaryText,
	string? ModelId,
	string PromptVersion,
	UsageDetails? Usage = null);

public enum ConversationSummaryUpdateStatus
{
	NotNeeded,
	Updated,
	Failed
}

public sealed record ConversationSummaryUpdateResult(
	ConversationSummaryUpdateStatus Status,
	SessionSummary? Summary,
	UsageDetails? Usage = null,
	string? ErrorCode = null);

public sealed class ConversationSummaryService(
	ISessionStore sessionStore,
	ConversationSummarySelector selector,
	IConversationSummarizer summarizer)
{
	public async Task<ConversationSummaryUpdateResult> SummarizeIfNeededAsync(
		Guid sessionId,
		int retainedCompletedRunCount,
		CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfEqual(sessionId, Guid.Empty);
		ArgumentOutOfRangeException.ThrowIfLessThan(retainedCompletedRunCount, 1);

		Session? session = await sessionStore.GetSessionAsync(sessionId, cancellationToken);
		if (session is null)
		{
			throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");
		}

		SessionSummary? previousSummary = await sessionStore.GetSummaryAsync(sessionId, cancellationToken);
		IReadOnlyList<ConversationMessage> messages = await sessionStore.GetMessagesAsync(
			sessionId, cancellationToken: cancellationToken);
		IReadOnlyList<SessionRun> runs = await sessionStore.GetRunsAsync(sessionId, cancellationToken);
		ConversationSummarySelection selection = selector.Select(
			sessionId, messages, runs, previousSummary, retainedCompletedRunCount);
		if (!selection.HasMessages)
		{
			return new ConversationSummaryUpdateResult(
				ConversationSummaryUpdateStatus.NotNeeded, previousSummary);
		}

		GeneratedConversationSummary generated;
		try
		{
			generated = await summarizer.SummarizeAsync(new ConversationSummaryRequest(
				sessionId,
				previousSummary?.SummaryText,
				selection.PreviousCoverage,
				selection.MessagesToSummarize), cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception)
		{
			return Failed(previousSummary);
		}

		if (string.IsNullOrWhiteSpace(generated.SummaryText)
			|| string.IsNullOrWhiteSpace(generated.PromptVersion))
		{
			return Failed(previousSummary, generated.Usage);
		}

		var updatedSummary = new SessionSummary
		{
			SessionId = sessionId,
			SummaryText = generated.SummaryText.Trim(),
			CoveredThroughSequenceNumber = selection.CoveredThroughSequenceNumber,
			Version = (previousSummary?.Version ?? 0) + 1,
			CreatedAt = DateTimeOffset.UtcNow,
			ModelId = generated.ModelId,
			PromptVersion = generated.PromptVersion
		};

		await sessionStore.SaveSummaryAsync(updatedSummary, cancellationToken);
		return new ConversationSummaryUpdateResult(
			ConversationSummaryUpdateStatus.Updated, updatedSummary, generated.Usage);
	}

	private static ConversationSummaryUpdateResult Failed(SessionSummary? previousSummary, UsageDetails? usage = null) =>
		new(ConversationSummaryUpdateStatus.Failed, previousSummary, usage, "SUMMARY_GENERATION_FAILED");
}