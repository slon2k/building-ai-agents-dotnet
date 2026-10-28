using System.Text.Json;
using ExpenseAssistant.Infrastructure;

namespace ExpenseAssistant.Evals;

public sealed class EvaluationScenario
{
    public string Id { get; init; } = string.Empty;
    public string Mode { get; init; } = "model-only";
    public string? DatasetVersion { get; init; }
    public string? FixtureAssertionId { get; init; }
    public bool InjectQueryFailure { get; init; }
    public ExpectedToolCall[] ExpectedToolCalls { get; init; } = [];
    public string[]? Tags { get; init; }
    public EvaluationStep[]? Steps { get; init; }
    public string[]? Checks { get; init; }
    public string CheckMode { get; init; } = string.Empty;
    public string? ExpectedBehavior { get; init; }
}

public sealed class EvaluationStep
{
    public string Type { get; init; } = string.Empty;
    public string? Text { get; init; }
}

public sealed class ExpectedToolCall
{
    public string Name { get; init; } = string.Empty;
    public JsonElement Arguments { get; init; }
    public string? ErrorCode { get; init; }
}

public static class ScenarioCatalog
{
    public static void Validate(IReadOnlyList<EvaluationScenario?> scenarios)
    {
        if (scenarios.Count == 0)
        {
            throw new InvalidDataException("The scenario file must contain at least one scenario.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < scenarios.Count; index++)
        {
            EvaluationScenario? scenario = scenarios[index];
            if (scenario is null || string.IsNullOrWhiteSpace(scenario.Id) || !ids.Add(scenario.Id))
            {
                throw new InvalidDataException($"Scenario at index {index} has a missing or duplicate id.");
            }

            if (scenario.Mode is not ("model-only" or "tools") || scenario.ExpectedToolCalls is null)
            {
                throw new InvalidDataException($"Scenario '{scenario.Id}' has an unsupported mode.");
            }

            if (!string.Equals(scenario.CheckMode, "manual", StringComparison.OrdinalIgnoreCase)
                || scenario.Steps is not [{ Type: "userMessage", Text: not null }]
                || string.IsNullOrWhiteSpace(scenario.Steps[0].Text)
                || scenario.Checks is not { Length: > 0 }
                || scenario.Checks.Any(string.IsNullOrWhiteSpace)
                || string.IsNullOrWhiteSpace(scenario.ExpectedBehavior))
            {
                throw new InvalidDataException($"Scenario '{scenario.Id}' requires one non-empty userMessage, manual checks and expectedBehavior.");
            }

            if (scenario.Mode == "tools" && scenario.DatasetVersion != ExpenseDatabaseInitializer.DatasetVersion)
            {
                throw new InvalidDataException($"Scenario '{scenario.Id}' requires datasetVersion '{ExpenseDatabaseInitializer.DatasetVersion}'.");
            }

            if (scenario.Mode == "model-only" && (scenario.DatasetVersion is not null
                || scenario.FixtureAssertionId is not null || scenario.InjectQueryFailure || scenario.ExpectedToolCalls.Length > 0))
            {
                throw new InvalidDataException($"Model-only scenario '{scenario.Id}' cannot declare tool or fixture settings.");
            }

            foreach (ExpectedToolCall call in scenario.ExpectedToolCalls)
            {
                if (call is null || call.Name is not ("SearchTransactions" or "GetSpendingSummary") || call.Arguments.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException($"Scenario '{scenario.Id}' has an invalid expected tool call.");
                }
            }
        }
    }
}

public sealed record ToolCallEvidence(string CallId, string Name, JsonElement Arguments, JsonElement? Result = null);

public sealed record EvaluationReport(
    DateTimeOffset CreatedAtUtc,
    string Provider,
    string Deployment,
    int RequestTimeoutSeconds,
    string SystemPrompt,
    IReadOnlyList<EvaluationScenarioResult> Scenarios);

public sealed record EvaluationScenarioResult(
    string Id,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Checks,
    string ExpectedBehavior,
    Guid SessionId,
    Guid RunId,
    string Status,
    double DurationMilliseconds,
    string? Response,
    string? FinishReason,
    long? InputTokenCount,
    long? OutputTokenCount,
    long? TotalTokenCount,
    string? Error,
    string Mode = "model-only",
    string? DatasetVersion = null,
    IReadOnlyList<ToolCallEvidence>? ToolCalls = null,
    IReadOnlyDictionary<string, bool>? DeterministicChecks = null,
    string ManualReview = "NotReviewed");