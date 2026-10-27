using ExpenseAssistant.App;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using System.Text;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

IConfiguration configuration = new ConfigurationBuilder()
	.SetBasePath(AppContext.BaseDirectory)
	.AddJsonFile("appsettings.json", optional: true)
	.AddUserSecrets<AzureOpenAIOptions>(optional: true)
	.AddEnvironmentVariables()
	.Build();

AzureOpenAIOptions options = configuration
	.GetSection(AzureOpenAIOptions.SectionName)
	.Get<AzureOpenAIOptions>() ?? new AzureOpenAIOptions();

options.Validate();

AssistantOptions assistantOptions = configuration
	.GetSection(AssistantOptions.SectionName)
	.Get<AssistantOptions>() ?? new AssistantOptions();

assistantOptions.Validate();

IChatClient chatClient = AzureOpenAIChatClientFactory.Create(options);
var runner = new ChatRunner(chatClient, TimeSpan.FromSeconds(assistantOptions.RequestTimeoutSeconds));

Console.WriteLine($"Session: {runner.SessionId}");
Console.WriteLine("Ask an expense question. Press Esc to cancel a request, type 'exit' to quit.");

while (true)
{
	Console.Write("You: ");
	string? input = await Console.In.ReadLineAsync();

	if (input is null || input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
	{
		break;
	}

	if (string.IsNullOrWhiteSpace(input))
	{
		continue;
	}

	using var cts = new CancellationTokenSource();
	Task<ChatRunResult> runTask = runner.RunAsync(input, cts.Token);

	// .NET has no async key read, so poll for Esc while the request runs.
	while (!runTask.IsCompleted)
	{
		if (!Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
		{
			cts.Cancel();
		}

		await Task.Delay(50);
	}

	ChatRunResult result = await runTask;

	switch (result)
	{
		case { Status: ChatRunStatus.Completed, Response: { } response }:
			response.PrintResponse();
			response.PrintMetadata();
			break;
		case { Status: ChatRunStatus.Cancelled }:
			Console.WriteLine("[cancelled]");
			break;
		default:
			Console.WriteLine($"[{result.Status}] {result.Error}");
			break;
	}

	Console.WriteLine($"Run: {result.RunId}, status={result.Status}, duration={result.Duration.TotalMilliseconds:0} ms");
}
