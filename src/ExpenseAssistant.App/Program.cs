using ExpenseAssistant.App;
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

await AzureOpenAIChatExample.RunAsync(options);
