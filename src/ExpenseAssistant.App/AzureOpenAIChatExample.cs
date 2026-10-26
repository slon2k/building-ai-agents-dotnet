using Microsoft.Extensions.AI;

namespace ExpenseAssistant.App;

internal static class AzureOpenAIChatExample
{
    public static async Task RunAsync(AzureOpenAIOptions options)
    {
        IChatClient chatClient = AzureOpenAIChatClientFactory.Create(options);
        ChatResponse response = await chatClient.GetResponseAsync(
        [
            new(ChatRole.System, "Help users understand expense questions. Do not invent transactions or claim access to data that was not supplied."),
            new(ChatRole.User, "How much did I spend on groceries in March 2026?")
        ]);

        Console.WriteLine($"Assistant: {response.Text}");
        Console.WriteLine($"Finish reason: {response.FinishReason}");

        if (response.Usage is { } usage)
        {
            Console.WriteLine(
                $"Usage: input={FormatTokenCount(usage.InputTokenCount)}, " +
                $"output={FormatTokenCount(usage.OutputTokenCount)}, " +
                $"total={FormatTokenCount(usage.TotalTokenCount)}");
        }
        else
        {
            Console.WriteLine("Usage: not reported");
        }
    }

    private static string FormatTokenCount(long? count) => count?.ToString() ?? "not reported";
}