namespace ExpenseAssistant.Infrastructure;

public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    public string Endpoint { get; set; } = string.Empty;

    public string Deployment { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Set AzureOpenAI:Endpoint to the HTTPS endpoint for your Azure OpenAI resource.");
        }

        if (string.IsNullOrWhiteSpace(Deployment))
        {
            throw new InvalidOperationException(
                "Set AzureOpenAI:Deployment to your Azure OpenAI deployment name.");
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new InvalidOperationException(
                "Set AzureOpenAI:ApiKey with .NET User Secrets or the AZURE_OPENAI_API_KEY environment variable.");
        }
    }
}