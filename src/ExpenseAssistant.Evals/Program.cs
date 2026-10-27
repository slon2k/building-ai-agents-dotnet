using System.Text.Json;
using System.Text.Json.Serialization;
using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

bool validateOnly = args.Length > 0 && args[0] == "--validate";
string[] scenarioArguments = validateOnly ? [.. args.Skip(1)] : args;

if (scenarioArguments.Length > 1)
{
	Console.Error.WriteLine("Usage: ExpenseAssistant.Evals [--validate] [path-to-scenarios.json]");
	return 2;
}

string scenarioPath = Path.GetFullPath(scenarioArguments.Length == 1
	? scenarioArguments[0]
	: Path.Combine("evals", "scenarios.json"));

List<EvaluationScenario?> scenarios;
try
{
	string scenarioJson = await File.ReadAllTextAsync(scenarioPath);
	scenarios = JsonSerializer.Deserialize<List<EvaluationScenario?>>(scenarioJson, new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
	}) ?? throw new InvalidDataException("The scenario file must contain a JSON array.");
	ValidateScenarios(scenarios);
}
catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
{
	Console.Error.WriteLine($"Scenario file error: {exception.Message}");
	return 2;
}

if (validateOnly)
{
	Console.WriteLine($"Validated {scenarios.Count} scenario(s) in {scenarioPath}.");
	return 0;
}

IConfiguration configuration = new ConfigurationBuilder()
	.SetBasePath(AppContext.BaseDirectory)
	.AddJsonFile("appsettings.json", optional: true)
	.AddUserSecrets<Program>(optional: true)
	.AddEnvironmentVariables()
	.Build();

AzureOpenAIOptions modelOptions = configuration
	.GetSection(AzureOpenAIOptions.SectionName)
	.Get<AzureOpenAIOptions>() ?? new AzureOpenAIOptions();
modelOptions.Validate();

int timeoutSeconds = configuration.GetValue("Assistant:RequestTimeoutSeconds", 30);
if (timeoutSeconds <= 0)
{
	Console.Error.WriteLine("Assistant:RequestTimeoutSeconds must be positive.");
	return 2;
}

string systemPrompt = configuration["Assistant:SystemPrompt"] ?? string.Empty;
if (string.IsNullOrWhiteSpace(systemPrompt))
{
	Console.Error.WriteLine("Assistant:SystemPrompt must be configured.");
	return 2;
}

IChatClient chatClient = AzureOpenAIChatClientFactory.Create(modelOptions);
var scenarioResults = new List<EvaluationScenarioResult>();

foreach (EvaluationScenario scenario in scenarios.Cast<EvaluationScenario>())
{
	var runner = new ChatRunner(chatClient, TimeSpan.FromSeconds(timeoutSeconds), systemPrompt);
	ChatRunResult run = await runner.RunAsync(scenario.Steps![0].Text!, CancellationToken.None);
	ChatResponse? response = run.Response;

	scenarioResults.Add(new EvaluationScenarioResult(
		scenario.Id,
		scenario.Tags ?? [],
		scenario.Checks!,
		scenario.ExpectedBehavior!,
		run.SessionId,
		run.RunId,
		run.Status.ToString(),
		run.Duration.TotalMilliseconds,
		response?.Text,
		response?.FinishReason?.ToString(),
		response?.Usage?.InputTokenCount,
		response?.Usage?.OutputTokenCount,
		response?.Usage?.TotalTokenCount,
		run.Error));

	Console.WriteLine($"{scenario.Id}: {run.Status} ({run.Duration.TotalMilliseconds:0} ms)");
}

var report = new EvaluationReport(
	DateTimeOffset.UtcNow,
	"Azure OpenAI",
	modelOptions.Deployment,
	timeoutSeconds,
	systemPrompt,
	scenarioResults);

string reportDirectory = Path.Combine(Path.GetDirectoryName(scenarioPath)!, "results");
Directory.CreateDirectory(reportDirectory);
string reportPath = Path.Combine(reportDirectory, $"evaluation-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.json");
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
{
	WriteIndented = true
}));

Console.WriteLine($"Report: {reportPath}");
Console.WriteLine("Review each response against its expected behavior; this runner does not score answers automatically.");

return scenarioResults.All(result => result.Status == nameof(ChatRunStatus.Completed)) ? 0 : 1;

static void ValidateScenarios(IReadOnlyList<EvaluationScenario?> scenarios)
{
	if (scenarios.Count == 0)
	{
		throw new InvalidDataException("The scenario file must contain at least one scenario.");
	}

	var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	for (int index = 0; index < scenarios.Count; index++)
	{
		EvaluationScenario? scenario = scenarios[index];
		if (scenario is null)
		{
			throw new InvalidDataException($"Scenario at index {index} must be an object.");
		}

		if (string.IsNullOrWhiteSpace(scenario.Id) || !ids.Add(scenario.Id))
		{
			throw new InvalidDataException($"Scenario at index {index} has a missing or duplicate id.");
		}

		if (!string.Equals(scenario.CheckMode, "manual", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException($"Scenario '{scenario.Id}' must use checkMode 'manual' in this runner version.");
		}

		if (scenario.Steps is not [{ Type: "userMessage", Text: not null and not "" }])
		{
			throw new InvalidDataException($"Scenario '{scenario.Id}' must have exactly one non-empty userMessage step.");
		}

		if (scenario.Checks is not { Length: > 0 } ||
			string.IsNullOrWhiteSpace(scenario.ExpectedBehavior))
		{
			throw new InvalidDataException($"Scenario '{scenario.Id}' requires checks and expectedBehavior for manual review.");
		}
	}
}

internal sealed class EvaluationScenario
{
	public string Id { get; init; } = string.Empty;
	public string[]? Tags { get; init; }
	public EvaluationStep[]? Steps { get; init; }
	public string[]? Checks { get; init; }
	public string CheckMode { get; init; } = string.Empty;
	public string? ExpectedBehavior { get; init; }
}

internal sealed class EvaluationStep
{
	public string Type { get; init; } = string.Empty;
	public string? Text { get; init; }
}

internal sealed record EvaluationReport(
	DateTimeOffset CreatedAtUtc,
	string Provider,
	string Deployment,
	int RequestTimeoutSeconds,
	string SystemPrompt,
	IReadOnlyList<EvaluationScenarioResult> Scenarios);

internal sealed record EvaluationScenarioResult(
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
	string? Error);