using ExpenseAssistant.App;
using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using System.Text;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

IConfiguration configuration = new ConfigurationBuilder()
	.SetBasePath(AppContext.BaseDirectory)
	.AddJsonFile("appsettings.json", optional: true)
	.AddUserSecrets<Program>(optional: true)
	.AddEnvironmentVariables()
	.Build();

string connectionString = configuration.GetConnectionString("Expenses")
	?? new SqliteConnectionStringBuilder
	{
		DataSource = Path.Combine(AppContext.BaseDirectory, "expense-assistant.db"),
		ForeignKeys = true
	}.ToString();
var databaseOptions = new DbContextOptionsBuilder<ExpenseDbContext>()
	.UseSqlite(connectionString)
	.Options;

await using (var context = new ExpenseDbContext(databaseOptions))
{
	await ExpenseDatabaseInitializer.InitializeAsync(context);
}

if (args.Contains("--seed-only", StringComparer.OrdinalIgnoreCase))
{
	Console.WriteLine($"Database initialized with synthetic fixture {ExpenseDatabaseInitializer.DatasetVersion} ({ExpenseDatabaseInitializer.Currency}); existing records preserved.");
	return;
}

AzureOpenAIOptions options = configuration
	.GetSection(AzureOpenAIOptions.SectionName)
	.Get<AzureOpenAIOptions>() ?? new AzureOpenAIOptions();

options.Validate();

AssistantOptions assistantOptions = configuration
	.GetSection(AssistantOptions.SectionName)
	.Get<AssistantOptions>() ?? new AssistantOptions();

assistantOptions.Validate();

IChatClient chatClient = AzureOpenAIChatClientFactory.Create(options);
var runner = new ChatRunner(
	chatClient,
	TimeSpan.FromSeconds(assistantOptions.RequestTimeoutSeconds),
	assistantOptions.SystemPrompt);

await new ConsoleChat(runner).RunAsync();
