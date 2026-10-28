using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Tests;

internal sealed class FakeChatClient(Func<IEnumerable<ChatMessage>, CancellationToken, Task<ChatResponse>> handler) : IChatClient
{
    public List<ChatMessage> ReceivedMessages { get; } = [];
    public List<IReadOnlyList<ChatMessage>> ReceivedRequests { get; } = [];
    public List<ChatOptions?> ReceivedOptions { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatMessage[] snapshot = messages.ToArray();
        ReceivedRequests.Add(snapshot);
        ReceivedOptions.Add(options);
        ReceivedMessages.AddRange(snapshot);
        return handler(snapshot, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
