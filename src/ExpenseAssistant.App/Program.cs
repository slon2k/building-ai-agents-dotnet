using ExpenseAssistant.App;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

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

const string SystemPrompt = "Help users understand expense questions. Do not invent transactions or claim access to data that was not supplied.";

IChatClient chatClient = AzureOpenAIChatClientFactory.Create(options);

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
	Task<ChatResponse> responseTask = chatClient.GetResponseAsync(
		[
			new(ChatRole.System, SystemPrompt),
			new(ChatRole.User, input)
		],
		cancellationToken: cts.Token);

	// .NET has no async key read, so poll for Esc while the request runs.
	while (!responseTask.IsCompleted)
	{
		if (!Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
		{
			cts.Cancel();
		}

		await Task.Delay(50);
	}

	try
	{
		ChatResponse response = await responseTask;
		response.PrintResponse();
		response.PrintMetadata();
	}
	catch (OperationCanceledException) when (cts.IsCancellationRequested)
	{
		Console.WriteLine("[cancelled]");
	}
}
