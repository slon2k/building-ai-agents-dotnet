using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ExpenseAssistant.App;
using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Evals;

public sealed class EvaluationScenarioRunner(IChatClient chatClient, AssistantOptions options, FixtureAssertions? fixtures = null)
{
    public async Task<EvaluationScenarioResult> RunAsync(EvaluationScenario scenario, CancellationToken cancellationToken = default)
    {
        ScenarioCatalog.Validate([scenario]);
        var checks = new Dictionary<string, bool>();
        var recordingClient = new RecordingChatClient(chatClient);
        ChatRunResult run;

        if (scenario.Mode == "model-only")
        {
            var runner = new ChatRunner(recordingClient, TimeSpan.FromSeconds(options.RequestTimeoutSeconds), options.SystemPrompt);
            run = await runner.RunAsync(scenario.Steps![0].Text!, cancellationToken);
        }
        else
        {
            if (fixtures is null)
            {
                throw new InvalidDataException("Tool scenarios require versioned fixture assertions.");
            }

            fixtures.Validate([scenario]);
            var failure = new QueryFailureInterceptor();
            var databaseOptions = new DbContextOptionsBuilder<ExpenseDbContext>()
                .UseSqlite("Data Source=:memory:;Foreign Keys=True")
                .AddInterceptors(failure)
                .Options;
            await using var context = new ExpenseDbContext(databaseOptions);
            await context.Database.OpenConnectionAsync(cancellationToken);
            await ExpenseDatabaseInitializer.InitializeAsync(context, cancellationToken);
            context.ChangeTracker.Clear();
            var queries = new TransactionQueries(context);
            FixtureAssertion? fixture = scenario.FixtureAssertionId is { } id ? fixtures.Assertions[id] : null;
            if (fixture is not null)
            {
                checks["fixtureMatches"] = await fixture.VerifyAsync(queries, cancellationToken);
                if (!checks["fixtureMatches"])
                {
                    throw new InvalidDataException($"Fixture verification failed for '{scenario.Id}' before model execution.");
                }
            }

            string before = await SnapshotAsync(context, cancellationToken);
            failure.Enabled = scenario.InjectQueryFailure;
            try
            {
                var runner = new ToolChatRunner(recordingClient, new ExpenseTools(queries),
                    TimeSpan.FromSeconds(options.RequestTimeoutSeconds), options.SystemPrompt, options.MaxToolCalls);
                run = await runner.RunAsync(Guid.NewGuid(), scenario.Steps![0].Text!, cancellationToken);
            }
            finally
            {
                failure.Enabled = false;
            }

            checks["databaseUnchanged"] = before == await SnapshotAsync(context, cancellationToken);
            foreach (ExpectedToolCall expected in scenario.ExpectedToolCalls)
            {
                ToolCallEvidence[] matches = recordingClient.ToolCalls.Where(call => Matches(call, expected)).ToArray();
                checks[$"tool:{expected.Name}"] = matches.Any(call => HasExpectedOutcome(call, expected.ErrorCode));
                if (fixture is not null && expected.ErrorCode is null)
                {
                    checks[$"result:{expected.Name}"] = matches.Any(call => MatchesFixture(call, fixture));
                }
            }
        }

        UsageDetails? usage = scenario.Mode == "tools" ? run.Usage : run.Response?.Usage;
        return new EvaluationScenarioResult(
            scenario.Id, scenario.Tags ?? [], scenario.Checks!, scenario.ExpectedBehavior!,
            run.SessionId, run.RunId, run.Status.ToString(), run.Duration.TotalMilliseconds,
            run.Response?.Text, run.Response?.FinishReason?.ToString(),
            usage?.InputTokenCount, usage?.OutputTokenCount, usage?.TotalTokenCount, run.Error,
            scenario.Mode, scenario.DatasetVersion, recordingClient.ToolCalls.AsReadOnly(), checks);
    }

    private static bool Matches(ToolCallEvidence call, ExpectedToolCall expected) => call.Name == expected.Name
        && call.Arguments.ValueKind == JsonValueKind.Object
        && expected.Arguments.EnumerateObject().All(argument =>
            call.Arguments.TryGetProperty(argument.Name, out JsonElement actual) && JsonElement.DeepEquals(actual, argument.Value));

    private static bool HasExpectedOutcome(ToolCallEvidence call, string? errorCode)
    {
        if (call.Result is not { } envelope || !envelope.TryGetProperty("isSuccess", out JsonElement success))
        {
            return false;
        }

        return errorCode is null ? success.GetBoolean()
            : !success.GetBoolean() && envelope.GetProperty("error").GetProperty("code").GetString() == errorCode;
    }

    private static bool MatchesFixture(ToolCallEvidence call, FixtureAssertion fixture)
    {
        if (!HasExpectedOutcome(call, null))
        {
            return false;
        }

        JsonElement data = call.Result!.Value.GetProperty("result");
        if (call.Name == "GetSpendingSummary")
        {
            return data.GetProperty("totalSpending").GetDecimal() == fixture.TotalSpending
                && data.GetProperty("transactionCount").GetInt32() == fixture.TransactionCount
                && data.GetProperty("currency").GetString() == fixture.Currency;
        }

        int limit = call.Arguments.TryGetProperty("limit", out JsonElement value) ? value.GetInt32() : 20;
        string?[] ids = data.GetProperty("transactions").EnumerateArray()
            .Select(transaction => transaction.GetProperty("id").GetString()).ToArray();
        return ids.SequenceEqual(fixture.RecordIds.Take(limit))
            && data.GetProperty("returnedCount").GetInt32() == ids.Length
            && data.GetProperty("hasMore").GetBoolean() == (fixture.TransactionCount > limit);
    }

    private static async Task<string> SnapshotAsync(ExpenseDbContext context, CancellationToken cancellationToken)
    {
        var transactions = await context.Transactions.AsNoTracking().OrderBy(transaction => transaction.Id)
            .Select(transaction => new
            {
                transaction.Id, transaction.Date, transaction.Merchant, transaction.Description,
                transaction.CategoryId, transaction.Amount, transaction.Currency
            }).ToListAsync(cancellationToken);
        var categories = await context.Categories.AsNoTracking().OrderBy(category => category.Id)
            .Select(category => new { category.Id, category.Name }).ToListAsync(cancellationToken);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { transactions, categories })));
    }

    private sealed class QueryFailureInterceptor : DbCommandInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("\"Transactions\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Synthetic query failure for evaluation.");
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class RecordingChatClient(IChatClient inner) : IChatClient
    {
        public List<ToolCallEvidence> ToolCalls { get; } = [];

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ChatMessage[] snapshot = messages.ToArray();
            foreach (FunctionResultContent result in snapshot.SelectMany(message => message.Contents).OfType<FunctionResultContent>())
            {
                int index = ToolCalls.FindIndex(call => call.CallId == result.CallId);
                if (index >= 0)
                {
                    ToolCalls[index] = ToolCalls[index] with { Result = JsonSerializer.SerializeToElement(result.Result, JsonSerializerOptions.Web) };
                }
            }

            ChatResponse response = await inner.GetResponseAsync(snapshot, options, cancellationToken);
            foreach (FunctionCallContent call in response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>())
            {
                ToolCalls.Add(new(call.CallId, call.Name, JsonSerializer.SerializeToElement(call.Arguments, JsonSerializerOptions.Web)));
            }

            return response;
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);
        public void Dispose() { }
    }
}