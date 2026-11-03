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
            MemorySessionStore sessionStore = (MemorySessionStore)provider.GetRequiredService<ISessionStore>();
            Session session = Assert.Single(await sessionStore.ListSessionsAsync());
            ConversationMessage[] storedMessages = (await sessionStore.GetMessagesAsync(session.SessionId)).ToArray();
            Assert.Equal(new[]
            {
                ConversationMessageRole.User,
                ConversationMessageRole.Assistant,
                ConversationMessageRole.Tool,
                ConversationMessageRole.Assistant
            }, storedMessages.Select(message => message.Role));
            Assert.All(storedMessages, message => Assert.NotNull(message.RunId));
            SessionRun run = Assert.Single(sessionStore.Runs);
            Assert.Equal(SessionRunStatus.Completed, run.Status);
            Assert.Equal(2, run.ModelStepCount);
            Assert.Equal(1, run.ToolCallCount);
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
        sessionStore.AddIncompleteToolCall(existingSession.SessionId, "orphan-call");
        Session otherSession = await sessionStore.CreateSessionAsync("Other session");
        sessionStore.AddTextMessage(otherSession.SessionId, "OTHER_SESSION_MARKER");
        var runSpans = new ConcurrentQueue<Activity>();
        using ActivityListener listener = ListenForRuns(runSpans);

        try
        {
            Console.SetIn(new StringReader(
                $"/sessions\n/history\n/new Weekend trip\n/use {existingSession.SessionId}\nQuestion?\nexit\n"));
            Console.SetOut(output);

            await provider.GetRequiredService<ConsoleChat>().RunAsync();

            Assert.Contains("Existing session", output.ToString());
            Assert.Contains("OTHER_SESSION_MARKER", output.ToString());
            Assert.Contains("Weekend trip", output.ToString());
            Assert.Contains($"Selected session: Existing session ({existingSession.SessionId})", output.ToString());
            Assert.Equal(existingSession.SessionId.ToString(), Assert.Single(runSpans).GetTagItem("session.id"));
            Assert.Single(client.ReceivedRequests);
            IReadOnlyList<ChatMessage> request = Assert.Single(client.ReceivedRequests);
            Assert.Contains(request, message => message.Text == "SAVED_HISTORY_MARKER");
            Assert.DoesNotContain(request, message => message.Text == "OTHER_SESSION_MARKER");
            Assert.DoesNotContain(request.SelectMany(message => message.Contents),
                content => content is FunctionCallContent call && call.CallId == "orphan-call");
            Assert.Collection(await sessionStore.ListSessionsAsync(),
                session => Assert.Equal("Existing session", session.DisplayName),
                session => Assert.Equal("Weekend trip", session.DisplayName),
                session => Assert.Equal("Other session", session.DisplayName));
        }
        finally
        {
            Console.SetIn(originalInput);
            Console.SetOut(originalOutput);
        }
    }

    [Fact]
    public async Task RunAsync_PersistsTurnsAndResumesHistoryOnNextConsoleRun()
    {
        TextReader originalInput = Console.In;
        TextWriter originalOutput = Console.Out;
        using var output = new StringWriter();
        List<TrackingQueries> queries = [];
        Queue<ChatResponse> responses = new([
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "First answer.")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Follow-up answer."))
        ]);
        var client = new FakeChatClient((_, _) => Task.FromResult(responses.Dequeue()));
        await using ServiceProvider provider = CreateProvider(client, queries);
        MemorySessionStore sessionStore = (MemorySessionStore)provider.GetRequiredService<ISessionStore>();

        try
        {
            Console.SetOut(output);
            Console.SetIn(new StringReader("First question?\nexit\n"));
            await provider.GetRequiredService<ConsoleChat>().RunAsync();

            Console.SetIn(new StringReader("Follow-up question?\nexit\n"));
            await provider.GetRequiredService<ConsoleChat>().RunAsync();

            Session session = Assert.Single(await sessionStore.ListSessionsAsync());
            ConversationMessage[] messages = (await sessionStore.GetMessagesAsync(session.SessionId)).ToArray();
            Assert.Equal(new[]
            {
                ConversationMessageRole.User,
                ConversationMessageRole.Assistant,
                ConversationMessageRole.User,
                ConversationMessageRole.Assistant
            }, messages.Select(message => message.Role));
            Assert.Equal(messages[0].RunId, messages[1].RunId);
            Assert.Equal(messages[2].RunId, messages[3].RunId);
            Assert.NotEqual(messages[0].RunId, messages[2].RunId);
            Assert.Equal(new[] { SessionRunStatus.Completed, SessionRunStatus.Completed },
                sessionStore.Runs.Select(run => run.Status));
            Assert.Equal(2, client.ReceivedRequests.Count);
            Assert.Equal(2, client.ReceivedRequests[0].Count);
            Assert.Equal(4, client.ReceivedRequests[1].Count);
            Assert.Contains(client.ReceivedRequests[1], message => message.Text == "First question?");
            Assert.Contains(client.ReceivedRequests[1], message => message.Text == "First answer.");
            Assert.Contains(client.ReceivedRequests[1], message => message.Text == "Follow-up question?");
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
            MemorySessionStore sessionStore = (MemorySessionStore)provider.GetRequiredService<ISessionStore>();
            Session session = Assert.Single(await sessionStore.ListSessionsAsync());
            Assert.Equal(SessionRunStatus.Cancelled, Assert.Single(sessionStore.Runs).Status);
            Assert.Equal(ConversationMessageRole.User,
                Assert.Single(await sessionStore.GetMessagesAsync(session.SessionId)).Role);
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
        private readonly List<SessionRun> runs = [];

        public IReadOnlyList<SessionRun> Runs => runs;

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

        public Task<IReadOnlyList<ConversationMessage>> AppendMessagesAsync(
            Guid sessionId, IReadOnlyList<ConversationMessageDraft> drafts, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (drafts.Count == 0)
            {
                throw new ArgumentException("At least one message is required.", nameof(drafts));
            }

            Session session = sessions.SingleOrDefault(item => item.SessionId == sessionId)
                ?? throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");
            Guid? runId = drafts[0].RunId;
            if (drafts.Any(draft => draft.RunId != runId)
                || runId is Guid activeRunId && !runs.Any(run => run.SessionId == sessionId
                    && run.RunId == activeRunId && run.Status == SessionRunStatus.Running))
            {
                throw new InvalidOperationException("Messages must belong to one active session run.");
            }

            long nextSequence = messages.Where(message => message.SessionId == sessionId)
                .Select(message => message.SequenceNumber).DefaultIfEmpty().Max() + 1;
            var appended = new List<ConversationMessage>(drafts.Count);
            foreach (ConversationMessageDraft draft in drafts)
            {
                Guid messageId = Guid.NewGuid();
                List<ConversationMessagePart> parts = draft.Parts.Select((part, ordinal) => part switch
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
                }).ToList();
                var message = new ConversationMessage
                {
                    MessageId = messageId,
                    SessionId = sessionId,
                    RunId = draft.RunId,
                    SequenceNumber = nextSequence++,
                    Role = draft.Role,
                    Parts = parts
                };
                appended.Add(message);
            }

            messages.AddRange(appended);
            session.LastActivityAt = DateTimeOffset.UtcNow;
            return Task.FromResult<IReadOnlyList<ConversationMessage>>(appended);
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
            Session session = sessions.Single(item => item.SessionId == sessionId);
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
            session.LastActivityAt = DateTimeOffset.UtcNow;
        }

        public void AddIncompleteToolCall(Guid sessionId, string callId)
        {
            Guid messageId = Guid.NewGuid();
            messages.Add(new ConversationMessage
            {
                MessageId = messageId,
                SessionId = sessionId,
                SequenceNumber = messages.Count(message => message.SessionId == sessionId) + 1,
                Role = ConversationMessageRole.Assistant,
                Parts =
                [
                    new ConversationMessagePart
                    {
                        MessageId = messageId,
                        Ordinal = 0,
                        Kind = ConversationMessagePartKind.ToolCall,
                        CallId = callId,
                        ToolName = "SearchTransactions",
                        ArgumentsJson = "{}"
                    }
                ]
            });
        }

        public Task<SessionSummary?> GetSummaryAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionSummary?>(null);

        public Task SaveSummaryAsync(SessionSummary summary, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SessionRun> StartRunAsync(Guid sessionId, Guid runId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sessions.All(session => session.SessionId != sessionId))
            {
                throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");
            }

            var run = new SessionRun
            {
                RunId = runId,
                SessionId = sessionId,
                StartedAt = DateTimeOffset.UtcNow,
                Status = SessionRunStatus.Running
            };
            runs.Add(run);
            return Task.FromResult(run);
        }

        public Task<SessionRun> FinishRunAsync(SessionRun finishedRun, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SessionRun run = runs.Single(item => item.SessionId == finishedRun.SessionId && item.RunId == finishedRun.RunId);
            run.EndedAt = finishedRun.EndedAt;
            run.Status = finishedRun.Status;
            run.StopReason = finishedRun.StopReason;
            run.ModelStepCount = finishedRun.ModelStepCount;
            run.ToolCallCount = finishedRun.ToolCallCount;
            run.InputTokenCount = finishedRun.InputTokenCount;
            run.OutputTokenCount = finishedRun.OutputTokenCount;
            run.TotalTokenCount = finishedRun.TotalTokenCount;
            return Task.FromResult(run);
        }

        public Task<int> RecoverInterruptedRunsAsync(DateTimeOffset endedAt, CancellationToken cancellationToken = default) =>
            Task.FromResult(runs.Where(run => run.Status == SessionRunStatus.Running).Count(run =>
            {
                run.Status = SessionRunStatus.Interrupted;
                run.EndedAt = endedAt;
                return true;
            }));
    }
}

[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection
{
}