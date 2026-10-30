using ExpenseAssistant.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ExpenseAssistant.App;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgentRunner(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped(provider => new ToolDispatcher(provider.GetRequiredService<ExpenseTools>().CreateFunctions()));
        services.AddScoped(provider =>
        {
            AssistantOptions options = provider.GetRequiredService<AssistantOptions>();
            return new AgentRunner(provider.GetRequiredService<IChatClient>(), provider.GetRequiredService<ToolDispatcher>(),
                options.SystemPrompt,
                new AgentRunOptions(MaxToolCalls: options.MaxToolCalls, MaxDuration: TimeSpan.FromSeconds(options.RequestTimeoutSeconds)),
                provider.GetRequiredService<ILogger<AgentRunner>>());
        });
        return services;
    }

    public static IServiceCollection AddChatClient(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(services =>
            AzureOpenAIChatClientFactory.Create(services.GetRequiredService<AzureOpenAIOptions>()));

        return services;
    }

    public static IServiceCollection AddToolChatRunner(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped(provider =>
        {
            AssistantOptions options = provider.GetRequiredService<AssistantOptions>();
            return new ToolChatRunner(
                provider.GetRequiredService<IChatClient>(),
                provider.GetRequiredService<ExpenseTools>(),
                TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
                options.SystemPrompt,
                options.MaxToolCalls,
                provider.GetRequiredService<ILogger<ToolChatRunner>>());
        });

        return services;
    }
}