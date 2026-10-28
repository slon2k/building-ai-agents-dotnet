using ExpenseAssistant.Core;
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
        PrintUsage(response.Usage, "Usage");
    }

    internal static void PrintMetadata(this ChatRunResult result)
    {
        Console.WriteLine($"Finish reason: {result.Response?.FinishReason}");
        PrintUsage(result.Usage, "Run usage");
    }

    private static void PrintUsage(UsageDetails? usage, string label)
    {
        if (usage is not null)
        {
            Console.WriteLine(
                $"{label}: input={FormatTokenCount(usage.InputTokenCount)}, " +
                $"output={FormatTokenCount(usage.OutputTokenCount)}, " +
                $"total={FormatTokenCount(usage.TotalTokenCount)}");
        }
        else
        {
            Console.WriteLine($"{label}: not reported");
        }
    }

    private static string FormatTokenCount(long? count) => count?.ToString() ?? "not reported";
}