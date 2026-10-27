using ExpenseAssistant.App;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Tests;

public class ChatRunnerTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static FakeChatClient Replying(string text, UsageDetails? usage = null) =>
        new((_, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { Usage = usage }));

    private static FakeChatClient WaitingForCancellation() =>
        new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Unreachable");
        });

    [Fact]
    public async Task Sends_system_instruction_and_user_input_as_separate_messages()
    {
        var client = Replying("ok");
        var runner = new ChatRunner(client, DefaultTimeout);

        await runner.RunAsync("How much did I spend?", CancellationToken.None);

        Assert.Collection(client.ReceivedMessages,
            m => { Assert.Equal(ChatRole.System, m.Role); Assert.Equal(ChatRunner.SystemPrompt, m.Text); },
            m => { Assert.Equal(ChatRole.User, m.Role); Assert.Equal("How much did I spend?", m.Text); });
    }

    [Fact]
    public async Task Completed_run_carries_session_run_and_response()
    {
        var runner = new ChatRunner(Replying("hello"), DefaultTimeout);

        ChatRunResult result = await runner.RunAsync("hi", CancellationToken.None);

        Assert.Equal(ChatRunStatus.Completed, result.Status);
        Assert.Equal(runner.SessionId, result.SessionId);
        Assert.NotEqual(Guid.Empty, result.RunId);
        Assert.Equal("hello", result.Response?.Text);
        Assert.True(result.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Runs_in_one_session_share_session_id_but_get_distinct_run_ids()
    {
        var runner = new ChatRunner(Replying("ok"), DefaultTimeout);

        ChatRunResult first = await runner.RunAsync("a", CancellationToken.None);
        ChatRunResult second = await runner.RunAsync("b", CancellationToken.None);

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.NotEqual(first.RunId, second.RunId);
    }

    [Fact]
    public async Task Missing_usage_metadata_is_left_unknown()
    {
        var runner = new ChatRunner(Replying("ok", usage: null), DefaultTimeout);

        ChatRunResult result = await runner.RunAsync("hi", CancellationToken.None);

        Assert.Equal(ChatRunStatus.Completed, result.Status);
        Assert.Null(result.Response?.Usage);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_and_reported_as_cancelled()
    {
        var runner = new ChatRunner(WaitingForCancellation(), DefaultTimeout);
        using var cts = new CancellationTokenSource();

        Task<ChatRunResult> runTask = runner.RunAsync("hi", cts.Token);
        cts.Cancel();
        ChatRunResult result = await runTask;

        Assert.Equal(ChatRunStatus.Cancelled, result.Status);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task Exceeding_the_timeout_is_reported_as_timed_out()
    {
        var runner = new ChatRunner(WaitingForCancellation(), TimeSpan.FromMilliseconds(50));

        ChatRunResult result = await runner.RunAsync("hi", CancellationToken.None);

        Assert.Equal(ChatRunStatus.TimedOut, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Client_failure_is_reported_without_throwing()
    {
        var client = new FakeChatClient((_, _) => throw new HttpRequestException("Service unavailable"));
        var runner = new ChatRunner(client, DefaultTimeout);

        ChatRunResult result = await runner.RunAsync("hi", CancellationToken.None);

        Assert.Equal(ChatRunStatus.Failed, result.Status);
        Assert.Equal("HttpRequestException: Service unavailable", result.Error);
        Assert.Equal(runner.SessionId, result.SessionId);
    }
}
