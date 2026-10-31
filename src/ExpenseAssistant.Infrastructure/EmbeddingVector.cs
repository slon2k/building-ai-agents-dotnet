namespace ExpenseAssistant.Infrastructure;

internal static class EmbeddingVector
{
    public static double CosineSimilarity(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        Validate(left);
        Validate(right, left.Length);
        double dot = 0;
        double leftSquared = 0;
        double rightSquared = 0;
        for (int index = 0; index < left.Length; index++)
        {
            double a = left[index];
            double b = right[index];
            dot += a * b;
            leftSquared += a * a;
            rightSquared += b * b;
        }
        return Math.Clamp(dot / (Math.Sqrt(leftSquared) * Math.Sqrt(rightSquared)), -1, 1);
    }

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
