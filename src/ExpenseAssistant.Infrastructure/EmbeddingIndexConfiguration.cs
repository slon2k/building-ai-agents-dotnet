using System.Text.Json;

namespace ExpenseAssistant.Infrastructure;

public sealed record EmbeddingIndexConfiguration(string IndexId, string ModelConfigurationId)
{
    public static EmbeddingIndexConfiguration Create(
        AzureOpenAIOptions options, string indexId, string modelConfigurationVersion)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateEmbeddings();
        ArgumentException.ThrowIfNullOrWhiteSpace(indexId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelConfigurationVersion);
        string modelConfigurationId = JsonSerializer.Serialize(new
        {
            Provider = "azure-openai",
            Endpoint = new Uri(options.Endpoint.TrimEnd('/') + "/").AbsoluteUri,
            Deployment = options.EmbeddingDeployment,
            Version = modelConfigurationVersion
        });
        return new EmbeddingIndexConfiguration(indexId, modelConfigurationId);
    }
}
