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
}

[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection
{
}