using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.App;

internal sealed class ChatRunner(IChatClient chatClient, TimeSpan requestTimeout)
{
    public const string SystemPrompt = "Help users understand expense questions. Do not invent transactions or claim access to data that was not supplied.";

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
                    new(ChatRole.System, SystemPrompt),
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
