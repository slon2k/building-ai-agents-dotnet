using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Tests;

internal sealed class FakeChatClient(Func<IEnumerable<ChatMessage>, CancellationToken, Task<ChatResponse>> handler) : IChatClient
{
    public List<ChatMessage> ReceivedMessages { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ReceivedMessages.AddRange(messages);
        return handler(messages, cancellationToken);
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
