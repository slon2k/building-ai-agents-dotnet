namespace ExpenseAssistant.Infrastructure;

internal static class EmbeddingVector
{
    public static void Validate(ReadOnlySpan<float> vector, int? dimensions = null)
    {
        if (dimensions is not null && vector.Length != dimensions)
        {
            throw new InvalidOperationException("Embedding vector dimensions do not match the index.");
        }

        bool nonzero = false;
        foreach (float value in vector)
        {
            if (!float.IsFinite(value))
            {
                throw new InvalidOperationException("The embedding vector contains a non-finite element.");
            }
            nonzero |= value != 0;
        }
        if (!nonzero)
        {
            throw new InvalidOperationException("The embedding vector is empty or has zero magnitude.");
        }
    }
}
