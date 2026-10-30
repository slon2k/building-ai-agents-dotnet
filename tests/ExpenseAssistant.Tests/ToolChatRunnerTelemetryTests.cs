using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ExpenseAssistant.App;
using ExpenseAssistant.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Json;

namespace ExpenseAssistant.Tests;

public class ToolChatRunnerTelemetryTests
{
    [Fact]
    public async Task RunAsync_WithFileLogging_PreservesStructuredCorrelationWithoutPayloads()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ExpenseAssistant.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "events.jsonl");
        Directory.CreateDirectory(directory);

        try
        {
            await using var context = await CreateContextAsync();
            Guid sessionId = Guid.NewGuid();
            ChatRunResult result;
            using (var fileLogger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .WriteTo.File(new JsonFormatter(), path)
                .CreateLogger())
            using (ILoggerFactory factory = LoggerFactory.Create(builder =>
                builder.ClearProviders().AddSerilog(fileLogger)))
            {
                Queue<ChatResponse> responses = new([
                    new ChatResponse(new ChatMessage(ChatRole.Assistant, [CreateCall()])),
                    new ChatResponse(new ChatMessage(ChatRole.Assistant, "PRIVATE_FINAL_TEXT"))
                ]);
                var client = new FakeChatClient((_, _) => Task.FromResult(responses.Dequeue()));
                var runner = new ToolChatRunner(client, new ExpenseTools(new TransactionQueries(context)),
                    TimeSpan.FromSeconds(30), "PRIVATE_SYSTEM_PROMPT", logger: factory.CreateLogger<ToolChatRunner>());

                result = await runner.RunAsync(sessionId, "PRIVATE_USER_TEXT", CancellationToken.None);
            }

            Assert.Equal(ChatRunStatus.Completed, result.Status);
            string[] lines = await File.ReadAllLinesAsync(path);
            Assert.NotEmpty(lines);
            JsonElement[] events = lines.Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Assert.All(events, entry =>
            {
                Assert.True(entry.TryGetProperty("Timestamp", out _));
                Assert.True(entry.TryGetProperty("Level", out _));
                JsonElement properties = entry.GetProperty("Properties");
                Assert.Equal(sessionId.ToString(), properties.GetProperty("SessionId").GetString());
                Assert.Equal(result.RunId.ToString(), properties.GetProperty("RunId").GetString());
            });
            JsonElement toolEvent = Assert.Single(events, entry => entry.GetProperty("MessageTemplate")
                .GetString()!.StartsWith("Tool finished", StringComparison.Ordinal));
            Assert.Equal("telemetry-call", toolEvent.GetProperty("Properties").GetProperty("ToolCallId").GetString());
            Assert.Equal("GetSpendingSummary", toolEvent.GetProperty("Properties").GetProperty("ToolName").GetString());
            string content = string.Join('\n', lines);
            Assert.DoesNotContain("PRIVATE_", content);
            Assert.DoesNotContain("groceries", content);
            Assert.DoesNotContain("2026-03-01", content);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RecordsCorrelatedSpansAndSafeLogsWithAggregateUsage()
    {
        await using var context = await CreateContextAsync();
        Guid sessionId = Guid.NewGuid();
        var spans = new ConcurrentQueue<Activity>();
        using ActivityListener listener = Listen(sessionId, spans);
        var logger = new RecordingLogger();
        var initialUsage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 3, TotalTokenCount = 13 };
        var finalUsage = new UsageDetails { InputTokenCount = 20, OutputTokenCount = 5, TotalTokenCount = 25 };
        var initial = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new TextContent("PRIVATE_INITIAL_TEXT"), CreateCall()])) { Usage = initialUsage };
        var final = new ChatResponse(new ChatMessage(ChatRole.Assistant, "PRIVATE_FINAL_TEXT")) { Usage = finalUsage };
        Queue<ChatResponse> responses = new([initial, final]);
        var client = new FakeChatClient((_, _) => Task.FromResult(responses.Dequeue()));
        var runner = CreateRunner(client, context, logger);

        ChatRunResult result = await runner.RunAsync(sessionId, "PRIVATE_USER_TEXT", CancellationToken.None);

        Assert.Equal(ChatRunStatus.Completed, result.Status);
        Assert.Equal(30, result.Usage?.InputTokenCount);
        Assert.Equal(8, result.Usage?.OutputTokenCount);
        Assert.Equal(38, result.Usage?.TotalTokenCount);
        Assert.Same(finalUsage, result.Response?.Usage);
        Assert.Equal(20, finalUsage.InputTokenCount);
        Activity root = Assert.Single(spans, span => span.OperationName == "assistant.run");
        Assert.Equal(4, spans.Count);
        Assert.Equal("Completed", root.GetTagItem("outcome"));
        Assert.Equal(30L, root.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(ActivityStatusCode.Ok, root.Status);
        Assert.Equal(new[] { "model.final", "model.initial", "tool.execute" }, spans
            .Where(span => span != root).Select(span => span.OperationName).Order(StringComparer.Ordinal));

        foreach (Activity span in spans)
        {
            Assert.Equal(sessionId.ToString(), span.GetTagItem("session.id"));
            Assert.Equal(result.RunId.ToString(), span.GetTagItem("run.id"));
            Assert.IsType<double>(span.GetTagItem("duration.ms"));
            if (span != root)
            {
                Assert.Equal(root.SpanId, span.ParentSpanId);
                Assert.Equal(root.TraceId, span.TraceId);
            }
        }

        Activity tool = Assert.Single(spans, span => span.OperationName == "tool.execute");
        Assert.Equal("telemetry-call", tool.GetTagItem("tool.call.id"));
        Assert.Equal("GetSpendingSummary", tool.GetTagItem("tool.name"));
        Assert.Contains(logger.Scopes, scope => scope.Contains(sessionId.ToString()) && scope.Contains(result.RunId.ToString()));
        Assert.Contains(logger.Scopes, scope => scope.Contains("telemetry-call") && scope.Contains("GetSpendingSummary"));
        Assert.Contains(logger.Properties, property => property.Key == "TotalTokens" && Equals(property.Value, 38L));

        string telemetry = string.Join('\n', logger.Messages.Concat(logger.Scopes)
            .Concat(logger.Properties.Select(property => $"{property.Key}={property.Value}"))
            .Concat(spans.SelectMany(span => span.TagObjects).Select(tag => $"{tag.Key}={tag.Value}")));
        Assert.DoesNotContain("PRIVATE_", telemetry);
        Assert.DoesNotContain("groceries", telemetry);
        Assert.DoesNotContain("2026-03-01", telemetry);
        Assert.DoesNotContain("21.25", telemetry);
    }

    [Theory]
    [InlineData("unknown", "UNKNOWN_TOOL")]
    [InlineData("invalid", "INVALID_ARGUMENTS")]
    [InlineData("database", "QUERY_FAILED")]
    [InlineData("limit", "TOOL_CALL_LIMIT")]
    public async Task RunAsync_RecordsToolFailureCodesWithoutLoggingUntrustedNames(string failure, string expectedCode)
    {
        await using var context = await CreateContextAsync();
        if (failure == "database")
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE Transactions");
        }

        Guid sessionId = Guid.NewGuid();
        var spans = new ConcurrentQueue<Activity>();
        using ActivityListener listener = Listen(sessionId, spans);
        var logger = new RecordingLogger();
        FunctionCallContent call = failure switch
        {
            "unknown" => new("telemetry-call", "PRIVATE_TOOL_NAME", new Dictionary<string, object?>()),
            "invalid" => new("telemetry-call", "GetSpendingSummary", new Dictionary<string, object?>()),
            _ => CreateCall()
        };
        List<AIContent> contents = [call];
        if (failure == "limit")
        {
            contents.Add(CreateCall("over-limit"));
        }

        Queue<ChatResponse> responses = new([
            new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Answer"))
        ]);
        var client = new FakeChatClient((_, _) => Task.FromResult(responses.Dequeue()));
        var runner = new ToolChatRunner(client, new ExpenseTools(new TransactionQueries(context)),
            TimeSpan.FromSeconds(30), "PRIVATE_SYSTEM_PROMPT", maxToolCalls: 1, logger: logger);

        ChatRunResult result = await runner.RunAsync(sessionId, "PRIVATE_USER_TEXT", CancellationToken.None);

        Assert.Equal(ChatRunStatus.Completed, result.Status);
        Activity failedTool = Assert.Single(spans, span => Equals(span.GetTagItem("error.code"), expectedCode));
        Assert.Equal("tool.execute", failedTool.OperationName);
        Assert.Equal(ActivityStatusCode.Error, failedTool.Status);
        Assert.Contains(logger.Properties, property => property.Key == "ErrorCode" && Equals(property.Value, expectedCode));
        Assert.DoesNotContain(logger.Messages.Concat(logger.Scopes), text => text.Contains("PRIVATE_"));
        if (failure == "unknown")
        {
            Assert.Equal("unrecognized", failedTool.GetTagItem("tool.name"));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RunAsync_WhenARequestOmitsUsage_LeavesRunUsageUnknown(int missingUsageIndex)
    {
        await using var context = await CreateContextAsync();
        ChatResponse[] responses =
        [
            new(new ChatMessage(ChatRole.Assistant, [CreateCall()])),
            new(new ChatMessage(ChatRole.Assistant, "Answer"))
        ];
        responses[1 - missingUsageIndex].Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2, TotalTokenCount = 12 };
        var queue = new Queue<ChatResponse>(responses);
        var client = new FakeChatClient((_, _) => Task.FromResult(queue.Dequeue()));

        ChatRunResult result = await CreateRunner(client, context, new RecordingLogger())
            .RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(ChatRunStatus.Completed, result.Status);
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task RunAsync_WhenUsageHasMissingCount_DoesNotTreatItAsZero()
    {
        await using var context = await CreateContextAsync();
        Queue<ChatResponse> responses = new([
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [CreateCall()]))
            {
                Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 3 }
            },
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Answer"))
            {
                Usage = new UsageDetails { InputTokenCount = 20, OutputTokenCount = 5, TotalTokenCount = 25 }
            }
        ]);
        var client = new FakeChatClient((_, _) => Task.FromResult(responses.Dequeue()));

        ChatRunResult result = await CreateRunner(client, context, new RecordingLogger())
            .RunAsync(Guid.NewGuid(), "Expenses?", CancellationToken.None);

        Assert.Equal(30, result.Usage?.InputTokenCount);
        Assert.Equal(8, result.Usage?.OutputTokenCount);
        Assert.Null(result.Usage?.TotalTokenCount);
    }

    [Theory]
    [InlineData(false, "Cancelled")]
    [InlineData(true, "TimedOut")]
    public async Task RunAsync_RecordsCancellationAndTimeoutOutcomes(bool timeout, string expectedOutcome)
    {
        await using var context = await CreateContextAsync();
        Guid sessionId = Guid.NewGuid();
        var spans = new ConcurrentQueue<Activity>();
        using ActivityListener listener = Listen(sessionId, spans);
        var logger = new RecordingLogger();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeChatClient(async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("PRIVATE_UNREACHABLE_EXCEPTION");
        });
        var runner = new ToolChatRunner(client, new ExpenseTools(new TransactionQueries(context)),
            timeout ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(30), "PRIVATE_SYSTEM_PROMPT", logger: logger);
        using var cancellation = new CancellationTokenSource();

        Task<ChatRunResult> run = runner.RunAsync(sessionId, "PRIVATE_USER_TEXT", cancellation.Token);
        await started.Task;
        if (!timeout)
        {
            cancellation.Cancel();
        }
        ChatRunResult result = await run;

        Assert.Equal(expectedOutcome, result.Status.ToString());
        Assert.Null(result.Usage);
        Assert.Equal(2, spans.Count);
        Assert.All(spans, span =>
        {
            Assert.Equal(expectedOutcome, span.GetTagItem("error.code"));
            Assert.Equal(ActivityStatusCode.Error, span.Status);
        });
        Assert.Contains(logger.Properties, property => property.Key == "ErrorCode" && Equals(property.Value, expectedOutcome));
        Assert.DoesNotContain(logger.Messages.Concat(logger.Scopes), text => text.Contains("PRIVATE_"));
    }

    [Fact]
    public async Task RunAsync_WhenProviderFails_LogsNoExceptionDetails()
    {
        await using var context = await CreateContextAsync();
        var logger = new RecordingLogger();
        var client = new FakeChatClient((_, _) => throw new InvalidOperationException("PRIVATE_PROVIDER_EXCEPTION"));

        ChatRunResult result = await CreateRunner(client, context, logger)
            .RunAsync(Guid.NewGuid(), "PRIVATE_USER_TEXT", CancellationToken.None);

        Assert.Equal(ChatRunStatus.Failed, result.Status);
        Assert.Contains(logger.Properties, property => property.Key == "ErrorCode" && Equals(property.Value, "Failed"));
        Assert.DoesNotContain(logger.Messages.Concat(logger.Scopes), text => text.Contains("PRIVATE_"));
    }

    private static ActivityListener Listen(Guid sessionId, ConcurrentQueue<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ToolChatRunner.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (Equals(activity.GetTagItem("session.id"), sessionId.ToString()))
                {
                    spans.Enqueue(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static ToolChatRunner CreateRunner(FakeChatClient client, ExpenseDbContext context, RecordingLogger logger) =>
        new(client, new ExpenseTools(new TransactionQueries(context)), TimeSpan.FromSeconds(30), "PRIVATE_SYSTEM_PROMPT", logger: logger);

    private static FunctionCallContent CreateCall(string id = "telemetry-call") => new(id, "GetSpendingSummary",
        JsonSerializer.Deserialize<Dictionary<string, object?>>("""
            { "startDate": "2026-03-01", "endDate": "2026-04-01", "categoryId": "groceries" }
            """)!);

    private static async Task<ExpenseDbContext> CreateContextAsync()
    {
        var options = new DbContextOptionsBuilder<ExpenseDbContext>()
            .UseSqlite("Data Source=:memory:;Foreign Keys=True").Options;
        var context = new ExpenseDbContext(options);
        await context.Database.OpenConnectionAsync();
        await ExpenseDatabaseInitializer.InitializeAsync(context);
        context.ChangeTracker.Clear();
        return context;
    }

    private sealed class RecordingLogger : ILogger<ToolChatRunner>
    {
        public List<string> Messages { get; } = [];
        public List<string> Scopes { get; } = [];
        public List<KeyValuePair<string, object?>> Properties { get; } = [];

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Scopes.Add(state.ToString()!);
            return null;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Messages.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                Properties.AddRange(properties);
            }
        }
    }
}