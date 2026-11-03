using System.Collections.Concurrent;
using System.Diagnostics;
using ExpenseAssistant.App;
using ExpenseAssistant.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ExpenseAssistant.Tests;

[Collection("Console")]
public class ConsoleChatTests
{
    [Fact]
    public async Task RunAsync_WithRegisteredAgent_CompletesToolRoundAndDisposesScope()
    {
        TextReader originalInput = Console.In;
        TextWriter originalOutput = Console.Out;
        using var output = new StringWriter();
        List<TrackingQueries> queries = [];
        Queue<ChatResponse> responses = new([
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("summary", "GetSpendingSummary", new Dictionary<string, object?>
                {
                    ["startDate"] = "2026-03-01", ["endDate"] = "2026-04-01"
                })])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "No matching spending."))
        ]);
        var client = new FakeChatClient((_, _) => Task.FromResult(responses.Dequeue()));
        await using ServiceProvider provider = CreateProvider(client, queries);
        try
        {
            Console.SetIn(new StringReader("March expenses?\nexit\n"));
            Console.SetOut(output);

            await provider.GetRequiredService<ConsoleChat>().RunAsync();

            Assert.Equal(2, client.ReceivedRequests.Count);
            Assert.Equal(ChatRole.Tool, client.ReceivedRequests[1].Last().Role);
            TrackingQueries scopedQueries = Assert.Single(queries);
            Assert.Equal(1, scopedQueries.SummaryCalls);
            Assert.True(scopedQueries.IsDisposed);
            Assert.Contains("Assistant: No matching spending.", output.ToString());
            Assert.Contains("status=Completed", output.ToString());
            Assert.Contains("Run usage", output.ToString());
        }
        finally
        {
            Console.SetIn(originalInput);
            Console.SetOut(originalOutput);
        }
    }

    [Fact]
    public async Task RunAsync_SupportsSessionCommandsAndRunsInSelectedSession()
    {
        TextReader originalInput = Console.In;
        TextWriter originalOutput = Console.Out;
        using var output = new StringWriter();
        List<TrackingQueries> queries = [];
        var client = new FakeChatClient((_, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))));
        await using ServiceProvider provider = CreateProvider(client, queries);
        MemorySessionStore sessionStore = (MemorySessionStore)provider.GetRequiredService<ISessionStore>();
        Session existingSession = await sessionStore.CreateSessionAsync("Existing session");
        sessionStore.AddTextMessage(existingSession.SessionId, "SAVED_HISTORY_MARKER");
        var runSpans = new ConcurrentQueue<Activity>();
        using ActivityListener listener = ListenForRuns(runSpans);

        try
        {
            Console.SetIn(new StringReader(
                $"/sessions\n/history\n/new Weekend trip\n/use {existingSession.SessionId}\nQuestion?\nexit\n"));
            Console.SetOut(output);

            await provider.GetRequiredService<ConsoleChat>().RunAsync();

            Assert.Contains("Existing session", output.ToString());
            Assert.Contains("SAVED_HISTORY_MARKER", output.ToString());
            Assert.Contains("Weekend trip", output.ToString());
            Assert.Contains($"Selected session: Existing session ({existingSession.SessionId})", output.ToString());
            Assert.Equal(existingSession.SessionId.ToString(), Assert.Single(runSpans).GetTagItem("session.id"));
            Assert.Single(client.ReceivedRequests);
            Assert.Collection(await sessionStore.ListSessionsAsync(),
                session => Assert.Equal("Weekend trip", session.DisplayName),
                session => Assert.Equal("Existing session", session.DisplayName));
        }
        finally
        {
            Console.SetIn(originalInput);
            Console.SetOut(originalOutput);
        }
    }

    [Fact]
    public async Task RunAsync_WhenHostStops_WaitsForRunCleanupBeforeDisposingScope()
    {
        TextReader originalInput = Console.In;
        TextWriter originalOutput = Console.Out;
        using var output = new StringWriter();
        using var stopping = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<TrackingQueries> queries = [];
        bool runFinished = false;
        var client = new FakeChatClient(async (_, token) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "unreachable"));
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.SetResult();
                await releaseCleanup.Task;
                throw;
            }
            finally
            {
                runFinished = true;
            }
        });
        await using ServiceProvider provider = CreateProvider(client, queries);
        Task? consoleRun = null;
        try
        {
            Console.SetIn(new StringReader("Expenses?\nexit\n"));
            Console.SetOut(output);
            consoleRun = provider.GetRequiredService<ConsoleChat>().RunAsync(stopping.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping.Cancel();
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.ThrowsAsync<TimeoutException>(() => consoleRun.WaitAsync(TimeSpan.FromMilliseconds(200)));
            Assert.False(runFinished);
            Assert.False(Assert.Single(queries).IsDisposed);

            releaseCleanup.SetResult();
            await consoleRun.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(runFinished);
            Assert.True(Assert.Single(queries).IsDisposed);
            Assert.Single(client.ReceivedRequests);
            Assert.Contains("[cancelled]", output.ToString());
        }
        finally
        {
            stopping.Cancel();
            releaseCleanup.TrySetResult();
            try
            {
                if (consoleRun is not null)
                {
                    await consoleRun.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            finally
            {
                Console.SetIn(originalInput);
                Console.SetOut(originalOutput);
            }
        }
    }

    [Fact]
    public async Task AddAgentRunner_UsesScopedDispatchersAndRunners()
    {
        List<TrackingQueries> queries = [];
        var client = new FakeChatClient((_, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))));
        await using ServiceProvider provider = CreateProvider(client, queries);
        await using AsyncServiceScope first = provider.CreateAsyncScope();
        await using AsyncServiceScope second = provider.CreateAsyncScope();

        Assert.Same(first.ServiceProvider.GetRequiredService<ToolDispatcher>(), first.ServiceProvider.GetRequiredService<ToolDispatcher>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<ToolDispatcher>(), second.ServiceProvider.GetRequiredService<ToolDispatcher>());
        Assert.Same(first.ServiceProvider.GetRequiredService<AgentRunner>(), first.ServiceProvider.GetRequiredService<AgentRunner>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<AgentRunner>(), second.ServiceProvider.GetRequiredService<AgentRunner>());
    }

    private static ServiceProvider CreateProvider(IChatClient client, List<TrackingQueries> queries)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(client);
        services.AddSingleton(new AssistantOptions { SystemPrompt = "Use tools." });
        services.AddScoped<ITransactionQueries>(_ =>
        {
            var scopedQueries = new TrackingQueries();
            queries.Add(scopedQueries);
            return scopedQueries;
        });
        services.AddScoped<ExpenseTools>();
        services.AddSingleton<ISessionStore, MemorySessionStore>();
        services.AddAgentRunner();
        services.AddSingleton<ConsoleChat>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private sealed class TrackingQueries : ITransactionQueries, IDisposable
    {
        public bool IsDisposed { get; private set; }
        public int SummaryCalls { get; private set; }

        public Task<TransactionSearchResult> SearchTransactionsAsync(DateOnly startDate, DateOnly endDate,
            string? categoryId = null, string? merchantText = null, int limit = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransactionSearchResult([], false));

        public Task<SpendingSummary> GetSpendingSummaryAsync(DateOnly startDate, DateOnly endDate,
            string? categoryId = null, CancellationToken cancellationToken = default)
        {
            SummaryCalls++;
            return Task.FromResult(new SpendingSummary(0m, 0, "EUR", []));
        }

        public void Dispose() => IsDisposed = true;
    }

    private static ActivityListener ListenForRuns(ConcurrentQueue<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentRunner.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "assistant.run")
                {
                    spans.Enqueue(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class MemorySessionStore : ISessionStore
    {
        private readonly List<Session> sessions = [];
        private readonly List<ConversationMessage> messages = [];

        public Task<Session> CreateSessionAsync(string displayName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var session = new Session
            {
                SessionId = Guid.NewGuid(),
                DisplayName = displayName.Trim(),
                CreatedAt = now,
                LastActivityAt = now
            };
            sessions.Add(session);
            return Task.FromResult(session);
        }

        public Task<IReadOnlyList<Session>> ListSessionsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<Session> result = sessions.OrderByDescending(session => session.LastActivityAt).ToArray();
            return Task.FromResult(result);
        }

        public Task<Session?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(sessions.SingleOrDefault(session => session.SessionId == sessionId));
        }

        public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
            Guid sessionId, long afterSequenceNumber = 0, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ConversationMessage> result = messages
                .Where(message => message.SessionId == sessionId && message.SequenceNumber > afterSequenceNumber)
                .OrderBy(message => message.SequenceNumber)
                .ToArray();
            return Task.FromResult(result);
        }

        public void AddTextMessage(Guid sessionId, string text)
        {
            Guid messageId = Guid.NewGuid();
            messages.Add(new ConversationMessage
            {
                MessageId = messageId,
                SessionId = sessionId,
                SequenceNumber = messages.Count(message => message.SessionId == sessionId) + 1,
                Role = ConversationMessageRole.User,
                Parts =
                [
                    new ConversationMessagePart
                    {
                        MessageId = messageId,
                        Ordinal = 0,
                        Kind = ConversationMessagePartKind.Text,
                        Text = text
                    }
                ]
            });
        }

        public Task<IReadOnlyList<ConversationMessage>> AppendMessagesAsync(
            Guid sessionId, IReadOnlyList<ConversationMessageDraft> messages, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionSummary?> GetSummaryAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveSummaryAsync(SessionSummary summary, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionRun> StartRunAsync(Guid sessionId, Guid runId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionRun> FinishRunAsync(SessionRun finishedRun, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> RecoverInterruptedRunsAsync(DateTimeOffset endedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}

[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection
{
}