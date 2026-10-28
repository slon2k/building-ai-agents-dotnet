using System.Text.Json;
using ExpenseAssistant.App;
using ExpenseAssistant.Evals;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Tests;

public class EvaluationScenarioTests
{
    [Fact]
    public async Task ToolScenario_RecordsArgumentsResultsAndIndependentChecks()
    {
        var scenario = CreateScenario();
        Queue<ChatResponse> responses = new([
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("summary-call", "GetSpendingSummary", Arguments())])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "EUR 21.25 across two transactions."))
        ]);
        var client = new FakeChatClient((_, _) => Task.FromResult(responses.Dequeue()));
        var runner = new EvaluationScenarioRunner(client, Options(), Fixtures());

        EvaluationScenarioResult result = await runner.RunAsync(scenario);

        Assert.Equal("Completed", result.Status);
        Assert.Equal("NotReviewed", result.ManualReview);
        Assert.Equal("v1", result.DatasetVersion);
        Assert.All(result.DeterministicChecks!, check => Assert.True(check.Value, check.Key));
        ToolCallEvidence call = Assert.Single(result.ToolCalls!);
        Assert.Equal("summary-call", call.CallId);
        Assert.Equal("2026-03-01", call.Arguments.GetProperty("startDate").GetString());
        Assert.Equal(21.25m, call.Result!.Value.GetProperty("result").GetProperty("totalSpending").GetDecimal());
    }

    [Fact]
    public void Validation_RejectsMissingDatasetVersion()
    {
        EvaluationScenario scenario = JsonSerializer.Deserialize<EvaluationScenario>("""
            { "Id": "bad", "Mode": "tools", "Steps": [{ "Type": "userMessage", "Text": "Expenses?" }],
              "Checks": ["grounded"], "CheckMode": "manual", "ExpectedBehavior": "Grounded answer" }
            """)!;

        Assert.Throws<InvalidDataException>(() => ScenarioCatalog.Validate([scenario]));
    }

    [Fact]
    public async Task QueryFailureScenario_RecordsFailureAndPreservesDatabase()
    {
        EvaluationScenario scenario = WithChanges(CreateScenario(), """
            { "InjectQueryFailure": true,
              "ExpectedToolCalls": [{ "Name": "GetSpendingSummary", "Arguments": {
                "startDate": "2026-03-01", "endDate": "2026-04-01", "categoryId": "groceries" }, "ErrorCode": "QUERY_FAILED" }] }
            """);
        var client = SummaryClient();

        EvaluationScenarioResult result = await new EvaluationScenarioRunner(client, Options(), Fixtures()).RunAsync(scenario);

        Assert.All(result.DeterministicChecks!, check => Assert.True(check.Value, check.Key));
        ToolCallEvidence call = Assert.Single(result.ToolCalls!);
        Assert.Equal("QUERY_FAILED", call.Result!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.False(call.Result.Value.GetProperty("isSuccess").GetBoolean());
    }

    [Fact]
    public async Task UnsupportedWriteScenario_ProvesDatabaseIsUnchanged()
    {
        EvaluationScenario scenario = WithChanges(CreateScenario(), """
            { "FixtureAssertionId": null, "ExpectedToolCalls": [] }
            """);
        Queue<ChatResponse> responses = new([
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("write-call", "DeleteTransaction", new Dictionary<string, object?> { ["id"] = "TX-G1" })])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "I cannot delete transactions."))
        ]);
        var client = new FakeChatClient((_, _) => Task.FromResult(responses.Dequeue()));

        EvaluationScenarioResult result = await new EvaluationScenarioRunner(client, Options(), Fixtures()).RunAsync(scenario);

        Assert.True(result.DeterministicChecks!["databaseUnchanged"]);
        Assert.Equal("UNKNOWN_TOOL", Assert.Single(result.ToolCalls!).Result!.Value
            .GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("NotReviewed", result.ManualReview);
    }

    [Fact]
    public async Task RepeatedScenarios_UseFreshFixturesAndDistinctRunIds()
    {
        var client = SummaryClient();
        var runner = new EvaluationScenarioRunner(client, Options(), Fixtures());

        EvaluationScenarioResult first = await runner.RunAsync(CreateScenario());
        EvaluationScenarioResult second = await runner.RunAsync(CreateScenario());

        Assert.NotEqual(first.RunId, second.RunId);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.All(first.DeterministicChecks!, check => Assert.True(check.Value, check.Key));
        Assert.All(second.DeterministicChecks!, check => Assert.True(check.Value, check.Key));
    }

    [Fact]
    public async Task IncorrectFixture_RejectsBeforeAnyModelRequest()
    {
        FixtureAssertions fixtures = Fixtures();
        fixtures.Assertions["march-groceries"] = new FixtureAssertion
        {
            StartDate = new DateOnly(2026, 3, 1), EndDate = new DateOnly(2026, 4, 1), CategoryId = "groceries",
            TotalSpending = 99m, TransactionCount = 2, Currency = "EUR", RecordIds = ["TX-G1", "TX-G2"]
        };
        var client = SummaryClient();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new EvaluationScenarioRunner(client, Options(), fixtures).RunAsync(CreateScenario()));

        Assert.Empty(client.ReceivedRequests);
    }

    [Fact]
    public async Task IncorrectToolArguments_FailDeterministicCheckEvenWhenRunCompletes()
    {
        var client = new FakeChatClient((_, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "A guessed answer."))));

        EvaluationScenarioResult result = await new EvaluationScenarioRunner(client, Options(), Fixtures()).RunAsync(CreateScenario());

        Assert.Equal("Completed", result.Status);
        Assert.False(result.DeterministicChecks!["tool:GetSpendingSummary"]);
        Assert.False(result.DeterministicChecks["result:GetSpendingSummary"]);
        Assert.Equal("NotReviewed", result.ManualReview);
    }

    [Fact]
    public async Task ModelOnlyScenario_DoesNotAdvertiseToolsOrRequireFixtures()
    {
        EvaluationScenario scenario = WithChanges(CreateScenario(), """
            { "Mode": "model-only", "DatasetVersion": null, "FixtureAssertionId": null, "ExpectedToolCalls": [] }
            """);
        var client = new FakeChatClient((_, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "No transaction data."))
        {
            Usage = new UsageDetails { TotalTokenCount = 12 }
        }));

        EvaluationScenarioResult result = await new EvaluationScenarioRunner(client, Options()).RunAsync(scenario);

        Assert.Equal("model-only", result.Mode);
        Assert.Null(result.DatasetVersion);
        Assert.Equal(12, result.TotalTokenCount);
        Assert.Empty(result.ToolCalls!);
        Assert.Empty(result.DeterministicChecks!);
        Assert.Null(Assert.Single(client.ReceivedOptions)?.Tools);
    }

    [Theory]
    [InlineData("{\"Mode\":\"unknown\"}")]
    [InlineData("{\"DatasetVersion\":\"v2\"}")]
    [InlineData("{\"ExpectedToolCalls\":null}")]
    [InlineData("{\"ExpectedToolCalls\":[null]}")]
    [InlineData("{\"ExpectedToolCalls\":[{\"Name\":\"DeleteTransaction\",\"Arguments\":{}}]}")]
    [InlineData("{\"Steps\":[{\"Type\":\"userMessage\",\"Text\":\" \"}]}")]
    [InlineData("{\"Checks\":[\"\"]}")]
    [InlineData("{\"CheckMode\":\"automatic\"}")]
    public void Validation_RejectsUnsupportedScenarioContracts(string changes)
    {
        Assert.Throws<InvalidDataException>(() => ScenarioCatalog.Validate([WithChanges(CreateScenario(), changes)]));
    }

    [Fact]
    public void Validation_RejectsDuplicateScenarioIdsAndUnknownFixtureReferences()
    {
        Assert.Throws<InvalidDataException>(() => ScenarioCatalog.Validate([CreateScenario(), CreateScenario()]));
        EvaluationScenario scenario = WithChanges(CreateScenario(), """{ "FixtureAssertionId": "unknown" }""");
        Assert.Throws<InvalidDataException>(() => Fixtures().Validate([scenario]));
    }

    [Fact]
    public async Task LimitedSearchAndSummary_ChecksIndependentFixtureResultsInEitherOrder()
    {
        EvaluationScenario scenario = WithChanges(CreateScenario(), """
            { "ExpectedToolCalls": [
                { "Name": "SearchTransactions", "Arguments": { "startDate": "2026-03-01", "endDate": "2026-04-01", "categoryId": "groceries", "limit": 1 } },
                { "Name": "GetSpendingSummary", "Arguments": { "startDate": "2026-03-01", "endDate": "2026-04-01", "categoryId": "groceries" } }
            ] }
            """);
        Dictionary<string, object?> searchArguments = Arguments();
        searchArguments["limit"] = 1;
        Queue<ChatResponse> responses = new([
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("summary", "GetSpendingSummary", Arguments()),
                new FunctionCallContent("search", "SearchTransactions", searchArguments)
            ])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "One displayed purchase; complete total EUR 21.25."))
        ]);
        var client = new FakeChatClient((_, _) => Task.FromResult(responses.Dequeue()));

        EvaluationScenarioResult result = await new EvaluationScenarioRunner(client, Options(), Fixtures()).RunAsync(scenario);

        Assert.Equal(2, result.ToolCalls!.Count);
        Assert.All(result.DeterministicChecks!, check => Assert.True(check.Value, check.Key));
    }

    private static FakeChatClient SummaryClient() => new((messages, _) => Task.FromResult(
        messages.Any(message => message.Role == ChatRole.Tool)
            ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "Review this answer manually."))
            : new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("summary-call", "GetSpendingSummary", Arguments())]))));

    private static EvaluationScenario WithChanges(EvaluationScenario scenario, string json)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(scenario))!;
        foreach (var field in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!)
        {
            fields[field.Key] = field.Value;
        }
        return JsonSerializer.Deserialize<EvaluationScenario>(JsonSerializer.Serialize(fields))!;
    }

    private static EvaluationScenario CreateScenario() => new()
    {
        Id = "march-groceries", Mode = "tools", DatasetVersion = "v1", FixtureAssertionId = "march-groceries",
        Steps = [new EvaluationStep { Type = "userMessage", Text = "March 2026 groceries?" }],
        Checks = ["grounded_summary"], CheckMode = "manual", ExpectedBehavior = "Uses the fixture summary.",
        ExpectedToolCalls = [new ExpectedToolCall
        {
            Name = "GetSpendingSummary", Arguments = JsonSerializer.SerializeToElement(Arguments())
        }]
    };

    private static Dictionary<string, object?> Arguments() => JsonSerializer.Deserialize<Dictionary<string, object?>>("""
        { "startDate": "2026-03-01", "endDate": "2026-04-01", "categoryId": "groceries" }
        """)!;

    private static AssistantOptions Options() => new() { SystemPrompt = "Use the supplied tools." };

    private static FixtureAssertions Fixtures() => new()
    {
        DatasetVersion = "v1",
        Assertions = new Dictionary<string, FixtureAssertion>
        {
            ["march-groceries"] = new()
            {
                StartDate = new DateOnly(2026, 3, 1), EndDate = new DateOnly(2026, 4, 1), CategoryId = "groceries",
                TotalSpending = 21.25m, TransactionCount = 2, Currency = "EUR", RecordIds = ["TX-G1", "TX-G2"]
            }
        }
    };
}