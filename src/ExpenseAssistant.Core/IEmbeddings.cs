namespace ExpenseAssistant.Core;

public interface IEmbeddings
{
    Task<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken cancellationToken = default);
}
