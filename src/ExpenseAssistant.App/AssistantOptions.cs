namespace ExpenseAssistant.App;

internal sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    public int RequestTimeoutSeconds { get; set; } = 30;

    public void Validate()
    {
        if (RequestTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                "Set Assistant:RequestTimeoutSeconds to a positive number of seconds.");
        }
    }
}
