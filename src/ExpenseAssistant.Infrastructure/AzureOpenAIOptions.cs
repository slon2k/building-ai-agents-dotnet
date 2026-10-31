namespace ExpenseAssistant.Infrastructure;

public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    public string Endpoint { get; set; } = string.Empty;

    public string Deployment { get; set; } = string.Empty;

    public string EmbeddingDeployment { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public void Validate()
    {
        ValidateConnection();

        if (string.IsNullOrWhiteSpace(Deployment))
        {
            throw new InvalidOperationException(
                "Set AzureOpenAI:Deployment to your Azure OpenAI deployment name.");
        }
    }

    public void ValidateEmbeddings()
    {
        ValidateConnection();

        if (string.IsNullOrWhiteSpace(EmbeddingDeployment))
        {
            throw new InvalidOperationException(
                "Set AzureOpenAI:EmbeddingDeployment to an embedding-capable Azure OpenAI deployment name.");
        }
    }

    private void ValidateConnection()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Set AzureOpenAI:Endpoint to the HTTPS endpoint for your Azure OpenAI resource.");
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new InvalidOperationException(
                "Set AzureOpenAI:ApiKey with .NET User Secrets or the AzureOpenAI__ApiKey environment variable.");
        }
    }
}