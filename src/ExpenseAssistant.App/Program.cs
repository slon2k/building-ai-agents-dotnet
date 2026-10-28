using ExpenseAssistant.App;
using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
	ContentRootPath = AppContext.BaseDirectory
});

builder.Configuration.Sources.Clear();
builder.Configuration
	.SetBasePath(AppContext.BaseDirectory)
	.AddJsonFile("appsettings.json", optional: true)
	.AddUserSecrets<Program>(optional: true)
	.AddEnvironmentVariables();

builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);

string connectionString = builder.Configuration.GetConnectionString("Expenses")
	?? new SqliteConnectionStringBuilder
	{
		DataSource = Path.Combine(AppContext.BaseDirectory, "expense-assistant.db"),
		ForeignKeys = true
	}.ToString();

builder.Services.AddDbContext<ExpenseDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped<ITransactionQueries, TransactionQueries>();
builder.Services.AddScoped<ExpenseTools>();

builder.Services.AddSingleton(services =>
{
	AzureOpenAIOptions options = services.GetRequiredService<IConfiguration>()
		.GetSection(AzureOpenAIOptions.SectionName)
		.Get<AzureOpenAIOptions>() ?? new AzureOpenAIOptions();
	options.Validate();
	return options;
});

builder.Services.AddSingleton(services =>
{
	AssistantOptions options = services.GetRequiredService<IConfiguration>()
		.GetSection(AssistantOptions.SectionName)
		.Get<AssistantOptions>() ?? new AssistantOptions();
	options.Validate();
	return options;
});

builder.Services.AddSingleton<IChatClient>(services =>
	AzureOpenAIChatClientFactory.Create(services.GetRequiredService<AzureOpenAIOptions>()));
builder.Services.AddSingleton(services =>
{
	AssistantOptions options = services.GetRequiredService<AssistantOptions>();
	return new ChatRunner(
		services.GetRequiredService<IChatClient>(),
		TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
		options.SystemPrompt);
});
builder.Services.AddSingleton<ConsoleChat>();

using IHost host = builder.Build();
await host.StartAsync();
CancellationToken stoppingToken = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

try
{
	await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
	{
		ExpenseDbContext context = scope.ServiceProvider.GetRequiredService<ExpenseDbContext>();
		await ExpenseDatabaseInitializer.InitializeAsync(context, stoppingToken);
	}

	if (args.Contains("--seed-only", StringComparer.OrdinalIgnoreCase))
	{
		Console.WriteLine($"Database initialized with synthetic fixture {ExpenseDatabaseInitializer.DatasetVersion} ({ExpenseDatabaseInitializer.Currency}); existing records preserved.");
		return;
	}

	await host.Services.GetRequiredService<ConsoleChat>().RunAsync(stoppingToken);
}
catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
{
}
finally
{
	await host.StopAsync();
}
