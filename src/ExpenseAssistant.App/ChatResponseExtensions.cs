using Microsoft.Extensions.AI;

namespace ExpenseAssistant.App;

internal static class ChatResponseExtensions
{
    internal static void PrintResponse(this ChatResponse response)
    {
        Console.WriteLine($"Assistant: {response.Text}");
    }

    internal static void PrintMetadata(this ChatResponse response)
    {
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