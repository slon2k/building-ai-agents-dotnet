using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace ExpenseAssistant.Infrastructure;

public static class AzureOpenAIChatClientFactory
{
    public static IChatClient Create(AzureOpenAIOptions options)
    {
        var endpoint = new Uri($"{options.Endpoint.TrimEnd('/')}/openai/v1/");
        var credential = new ApiKeyCredential(options.ApiKey);
        var clientOptions = new OpenAIClientOptions { Endpoint = endpoint };

        return new ChatClient(
            model: options.Deployment,
            credential: credential,
            options: clientOptions)
            .AsIChatClient();
    }
}