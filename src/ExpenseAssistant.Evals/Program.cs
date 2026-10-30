using System.Text.Json;
using System.Text.Json.Serialization;
using ExpenseAssistant.App;
using ExpenseAssistant.Evals;
using ExpenseAssistant.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

bool validateOnly = false;
string mode = "model-only";
string? scenarioFile = null;
for (int index = 0; index < args.Length; index++)
{
	if (args[index] == "--validate")
	{
		validateOnly = true;
	}
	else if (args[index] == "--mode" && index + 1 < args.Length)
	{
		mode = args[++index];
	}
	else if (!args[index].StartsWith("--", StringComparison.Ordinal) && scenarioFile is null)
	{
		scenarioFile = args[index];
	}
	else
	{
		Console.Error.WriteLine("Usage: ExpenseAssistant.Evals [--validate] [--mode model-only|tools|all] [path-to-scenarios.json]");
		return 2;
	}
}

if (mode is not ("model-only" or "tools" or "all"))
{
	Console.Error.WriteLine("Mode must be model-only, tools, or all.");
	return 2;
}

string scenarioPath = Path.GetFullPath(scenarioFile ?? Path.Combine("evals", "scenarios.json"));
var jsonOptions = new JsonSerializerOptions
{
	PropertyNameCaseInsensitive = true,
	UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
};

List<EvaluationScenario?> scenarios;
FixtureAssertions? fixtures = null;
try
{
	string scenarioJson = await File.ReadAllTextAsync(scenarioPath);
	scenarios = JsonSerializer.Deserialize<List<EvaluationScenario?>>(scenarioJson, jsonOptions)
		?? throw new InvalidDataException("The scenario file must contain a JSON array.");
	ScenarioCatalog.Validate(scenarios);
	if (scenarios.Any(scenario => scenario!.Mode == "tools"))
	{
		string fixturePath = Path.Combine(Path.GetDirectoryName(scenarioPath)!, "fixtures", $"{ExpenseDatabaseInitializer.DatasetVersion}.json");
		fixtures = JsonSerializer.Deserialize<FixtureAssertions>(await File.ReadAllTextAsync(fixturePath), jsonOptions)
			?? throw new InvalidDataException("Fixture assertions must be an object.");
		fixtures.Validate(scenarios.Cast<EvaluationScenario>());
		var databaseOptions = new DbContextOptionsBuilder<ExpenseDbContext>()
			.UseSqlite("Data Source=:memory:;Foreign Keys=True").Options;
		await using var context = new ExpenseDbContext(databaseOptions);
		await context.Database.OpenConnectionAsync();
		await ExpenseDatabaseInitializer.InitializeAsync(context);
		foreach (var assertion in fixtures.Assertions)
		{
			if (!await assertion.Value.VerifyAsync(new TransactionQueries(context), CancellationToken.None))
			{
				throw new InvalidDataException($"Fixture assertion '{assertion.Key}' does not match the seeded dataset.");
			}
		}
	}
}
catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
{
	Console.Error.WriteLine($"Scenario file error: {exception.Message}");
	return 2;
}

if (validateOnly)
{
	Console.WriteLine($"Validated {scenarios.Count} scenario(s) and {fixtures?.Assertions.Count ?? 0} fixture assertion(s) in {scenarioPath}. No model requests made.");
	return 0;
}

EvaluationScenario[] selected = scenarios.Cast<EvaluationScenario>()
	.Where(scenario => mode == "all" || scenario.Mode == mode).ToArray();
if (selected.Length == 0)
{
	Console.Error.WriteLine($"No scenarios match mode '{mode}'.");
	return 2;
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

AssistantOptions assistantOptions = configuration
	.GetSection(AssistantOptions.SectionName)
	.Get<AssistantOptions>() ?? new AssistantOptions();

int timeoutSeconds = assistantOptions.RequestTimeoutSeconds;
if (timeoutSeconds <= 0)
{
	Console.Error.WriteLine("Assistant:RequestTimeoutSeconds must be positive.");
	return 2;
}

string systemPrompt = assistantOptions.SystemPrompt;
if (string.IsNullOrWhiteSpace(systemPrompt))
{
	Console.Error.WriteLine("Assistant:SystemPrompt must be configured.");
	return 2;
}

if (selected.Any(scenario => scenario.Mode == "tools") && assistantOptions.MaxToolCalls <= 0)
{
	Console.Error.WriteLine("Assistant:MaxToolCalls must be positive.");
	return 2;
}

using IChatClient chatClient = AzureOpenAIChatClientFactory.Create(modelOptions);
var evaluator = new EvaluationScenarioRunner(chatClient, assistantOptions, fixtures);
var scenarioResults = new List<EvaluationScenarioResult>();

Console.WriteLine($"Running {selected.Length} scenario(s), mode={mode}. These requests use the configured model.");
foreach (EvaluationScenario scenario in selected)
{
	EvaluationScenarioResult result = await evaluator.RunAsync(scenario);
	scenarioResults.Add(result);
	Console.WriteLine($"{scenario.Id}: {result.Status} ({result.DurationMilliseconds:0} ms), deterministicChecksPass={result.DeterministicChecks!.Values.All(passed => passed)}");
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
Console.WriteLine("Review each response against its expected behavior. ManualReview remains NotReviewed; deterministic checks do not grade answer quality.");

return scenarioResults.All(result => result.Status == nameof(ChatRunStatus.Completed)
	&& result.DeterministicChecks!.Values.All(passed => passed)) ? 0 : 1;

