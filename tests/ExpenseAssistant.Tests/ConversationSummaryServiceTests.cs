using ExpenseAssistant.App;
using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Tests;

public class ConversationSummaryServiceTests
{
	[Fact]
	public async Task SummarizeIfNeededAsync_OnSuccess_AdvancesCoverageAndVersion()
	{
		await using ExpenseDbContext context = CreateContext();
		var store = new SessionStore(context);
		Session session = await store.CreateSessionAsync("Summary success");
		await AddCompletedRunsAsync(store, session.SessionId, 3);
		await store.SaveSummaryAsync(CreateSummary(session.SessionId, "Existing summary", 2, version: 1));
		var summarizer = new FakeSummarizer(request =>
		{
			Assert.Equal("Existing summary", request.PreviousSummary);
			Assert.Equal(2, request.PreviouslyCoveredThroughSequenceNumber);
			Assert.Equal(new long[] { 3, 4 }, request.Messages.Select(message => message.SequenceNumber));
			return Task.FromResult(new GeneratedConversationSummary(
				"Updated summary", "test-model", "summary-v2",
				new UsageDetails { InputTokenCount = 40, OutputTokenCount = 10, TotalTokenCount = 50 }));
		});
		var service = new ConversationSummaryService(store, new ConversationSummarySelector(), summarizer);

		ConversationSummaryUpdateResult result = await service.SummarizeIfNeededAsync(
			session.SessionId, retainedCompletedRunCount: 1);

		Assert.Equal(ConversationSummaryUpdateStatus.Updated, result.Status);
		Assert.Equal(50, result.Usage?.TotalTokenCount);
		Assert.Equal(2, result.Summary?.Version);
		Assert.Equal(4, result.Summary?.CoveredThroughSequenceNumber);
		Assert.Equal("test-model", result.Summary?.ModelId);
		Assert.Equal("summary-v2", result.Summary?.PromptVersion);
		Assert.Equal(1, summarizer.CallCount);
		SessionSummary persisted = (await store.GetSummaryAsync(session.SessionId))!;
		Assert.Equal("Updated summary", persisted.SummaryText);
		Assert.Equal(2, persisted.Version);
		Assert.Equal(4, persisted.CoveredThroughSequenceNumber);
	}

	[Fact]
	public async Task SummarizeIfNeededAsync_WhenGenerationThrows_LeavesPriorSummaryAndCoverageUnchanged()
	{
		await using ExpenseDbContext context = CreateContext();
		var store = new SessionStore(context);
		Session session = await store.CreateSessionAsync("Summary failure");
		await AddCompletedRunsAsync(store, session.SessionId, 3);
		await store.SaveSummaryAsync(CreateSummary(session.SessionId, "Keep me", 2, version: 1));
		var summarizer = new FakeSummarizer(_ => throw new InvalidOperationException("PRIVATE_GENERATOR_FAILURE"));
		var service = new ConversationSummaryService(store, new ConversationSummarySelector(), summarizer);

		ConversationSummaryUpdateResult result = await service.SummarizeIfNeededAsync(
			session.SessionId, retainedCompletedRunCount: 1);

		Assert.Equal(ConversationSummaryUpdateStatus.Failed, result.Status);
		Assert.Equal("SUMMARY_GENERATION_FAILED", result.ErrorCode);
		Assert.Equal("Keep me", result.Summary?.SummaryText);
		Assert.Equal(1, result.Summary?.Version);
		Assert.Equal(2, result.Summary?.CoveredThroughSequenceNumber);
		SessionSummary persisted = (await store.GetSummaryAsync(session.SessionId))!;
		Assert.Equal("Keep me", persisted.SummaryText);
		Assert.Equal(1, persisted.Version);
		Assert.Equal(2, persisted.CoveredThroughSequenceNumber);
	}

	[Fact]
	public async Task SummarizeIfNeededAsync_WhenOutputIsBlank_DoesNotSaveOrAdvanceCoverage()
	{
		await using ExpenseDbContext context = CreateContext();
		var store = new SessionStore(context);
		Session session = await store.CreateSessionAsync("Blank summary");
		await AddCompletedRunsAsync(store, session.SessionId, 2);
		var summarizer = new FakeSummarizer(_ => Task.FromResult(
			new GeneratedConversationSummary(" ", "test-model", "summary-v1")));
		var service = new ConversationSummaryService(store, new ConversationSummarySelector(), summarizer);

		ConversationSummaryUpdateResult result = await service.SummarizeIfNeededAsync(
			session.SessionId, retainedCompletedRunCount: 1);

		Assert.Equal(ConversationSummaryUpdateStatus.Failed, result.Status);
		Assert.Null(await store.GetSummaryAsync(session.SessionId));
	}

	[Fact]
	public async Task SummarizeIfNeededAsync_WhenNoEligibleRuns_DoesNotCallSummarizer()
	{
		await using ExpenseDbContext context = CreateContext();
		var store = new SessionStore(context);
		Session session = await store.CreateSessionAsync("No summary needed");
		var summarizer = new FakeSummarizer(_ => throw new InvalidOperationException("Should not run."));
		var service = new ConversationSummaryService(store, new ConversationSummarySelector(), summarizer);

		ConversationSummaryUpdateResult result = await service.SummarizeIfNeededAsync(
			session.SessionId, retainedCompletedRunCount: 1);

		Assert.Equal(ConversationSummaryUpdateStatus.NotNeeded, result.Status);
		Assert.Equal(0, summarizer.CallCount);
		Assert.Null(await store.GetSummaryAsync(session.SessionId));
	}

	private static async Task AddCompletedRunsAsync(ISessionStore store, Guid sessionId, int count)
	{
		for (int index = 0; index < count; index++)
		{
			Guid runId = Guid.NewGuid();
			SessionRun runningRun = await store.StartRunAsync(sessionId, runId);
			await store.AppendMessagesAsync(sessionId,
			[
				new ConversationMessageDraft(ConversationMessageRole.User,
					[new TextMessagePartDraft($"User turn {index + 1}")], runId),
				new ConversationMessageDraft(ConversationMessageRole.Assistant,
					[new TextMessagePartDraft($"Assistant turn {index + 1}")], runId)
			]);
			await store.FinishRunAsync(new SessionRun
			{
				RunId = runId,
				SessionId = sessionId,
				StartedAt = runningRun.StartedAt,
				EndedAt = DateTimeOffset.UtcNow,
				Status = SessionRunStatus.Completed,
				ModelStepCount = 1
			});
		}
	}

	private static SessionSummary CreateSummary(Guid sessionId, string text, long coverage, int version) => new()
	{
		SessionId = sessionId,
		SummaryText = text,
		CoveredThroughSequenceNumber = coverage,
		Version = version,
		CreatedAt = DateTimeOffset.UtcNow,
		ModelId = "previous-model",
		PromptVersion = "summary-v1"
	};

	private static ExpenseDbContext CreateContext()
	{
		var context = new ExpenseDbContext(new DbContextOptionsBuilder<ExpenseDbContext>()
			.UseSqlite("Data Source=:memory:;Foreign Keys=True").Options);
		context.Database.OpenConnection();
		context.Database.EnsureCreated();
		return context;
	}

	private sealed class FakeSummarizer(
		Func<ConversationSummaryRequest, Task<GeneratedConversationSummary>> summarize) : IConversationSummarizer
	{
		public int CallCount { get; private set; }

		public async Task<GeneratedConversationSummary> SummarizeAsync(
			ConversationSummaryRequest request, CancellationToken cancellationToken)
		{
			CallCount++;
			cancellationToken.ThrowIfCancellationRequested();
			return await summarize(request);
		}
	}
}