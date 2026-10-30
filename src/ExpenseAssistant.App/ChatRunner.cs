using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.App;

public sealed class ChatRunner(IChatClient chatClient, TimeSpan requestTimeout, string systemPrompt)
{
    public Guid SessionId { get; } = Guid.NewGuid();

    public async Task<ChatRunResult> RunAsync(string userInput, CancellationToken cancellationToken)
    {
        Guid runId = Guid.NewGuid();
        long startTimestamp = Stopwatch.GetTimestamp();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(requestTimeout);

        try
        {
            ChatResponse response = await chatClient.GetResponseAsync(
                [
                    new(ChatRole.System, systemPrompt),
                    new(ChatRole.User, userInput)
                ],
                cancellationToken: timeoutCts.Token);

            return new(SessionId, runId, ChatRunStatus.Completed, Stopwatch.GetElapsedTime(startTimestamp), response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(SessionId, runId, ChatRunStatus.Cancelled, Stopwatch.GetElapsedTime(startTimestamp));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return new(SessionId, runId, ChatRunStatus.TimedOut, Stopwatch.GetElapsedTime(startTimestamp),
                Error: $"Request exceeded the {requestTimeout.TotalSeconds:0.#}s timeout.");
        }
        catch (Exception ex)
        {
            // Only the type and message are surfaced; request details may carry credentials.
            return new(SessionId, runId, ChatRunStatus.Failed, Stopwatch.GetElapsedTime(startTimestamp),
                Error: $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

public enum ChatRunStatus
{
    Completed,
    Cancelled,
    TimedOut,
    Failed
}

public sealed record ChatRunResult(
    Guid SessionId,
    Guid RunId,
    ChatRunStatus Status,
    TimeSpan Duration,
    ChatResponse? Response = null,
    string? Error = null,
    UsageDetails? Usage = null);