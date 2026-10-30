using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace ExpenseAssistant.Infrastructure;

public static class AzureOpenAIChatClientFactory
{
    public const int MaxProviderRetries = 3;

    public static IChatClient Create(AzureOpenAIOptions options) => Create(options, null);

    public static IChatClient Create(AzureOpenAIOptions options, PipelineTransport? transport)
    {
        var endpoint = new Uri($"{options.Endpoint.TrimEnd('/')}/openai/v1/");
        var credential = new ApiKeyCredential(options.ApiKey);
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            RetryPolicy = new ClientRetryPolicy(MaxProviderRetries)
        };
        if (transport is not null)
        {
            clientOptions.Transport = transport;
        }

        return new ChatClient(
            model: options.Deployment,
            credential: credential,
            options: clientOptions)
            .AsIChatClient();
    }
}