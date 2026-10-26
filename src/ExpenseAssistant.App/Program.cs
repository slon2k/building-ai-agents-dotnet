using ExpenseAssistant.App;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json;

IConfiguration configuration = new ConfigurationBuilder()
	.SetBasePath(AppContext.BaseDirectory)
	.AddJsonFile("appsettings.json", optional: false)
	.AddUserSecrets<AzureOpenAIOptions>(optional: true)
	.AddEnvironmentVariables()
	.Build();

AzureOpenAIOptions options = configuration
	.GetSection(AzureOpenAIOptions.SectionName)
	.Get<AzureOpenAIOptions>() ?? new AzureOpenAIOptions();
options.Endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? options.Endpoint;
options.Deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? options.Deployment;
options.ApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? options.ApiKey;
options.Validate();

using var httpClient = new HttpClient();
using var request = new HttpRequestMessage(
	HttpMethod.Post,
	$"{options.Endpoint.TrimEnd('/')}/openai/v1/responses");

request.Headers.Add("api-key", options.ApiKey);
request.Content = new StringContent(
	JsonSerializer.Serialize(new
	{
		model = options.Deployment,
		instructions = "Help users understand expense questions. Do not invent transactions or claim access to data that was not supplied.",
		input = "How much did I spend on groceries in March 2026?",
		store = false
	}),
	Encoding.UTF8,
	"application/json");

using HttpResponseMessage response = await httpClient.SendAsync(request);
string responseJson = await response.Content.ReadAsStringAsync();

Console.WriteLine($"HTTP {(int)response.StatusCode} {response.StatusCode}");
Console.WriteLine("Raw response JSON:");
Console.WriteLine(responseJson);

if (!response.IsSuccessStatusCode)
{
	return;
}

using JsonDocument document = JsonDocument.Parse(responseJson);
JsonElement root = document.RootElement;

if (root.TryGetProperty("status", out JsonElement status))
{
	Console.WriteLine($"Response status: {status.GetString()}");
}

if (root.TryGetProperty("output_text", out JsonElement outputText))
{
	Console.WriteLine($"Assistant: {outputText.GetString()}");
}

if (root.TryGetProperty("usage", out JsonElement usage))
{
	Console.WriteLine($"Usage: {usage.GetRawText()}");
}
