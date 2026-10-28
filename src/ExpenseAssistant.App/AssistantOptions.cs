namespace ExpenseAssistant.App;

public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    public int RequestTimeoutSeconds { get; set; } = 30;

    public int MaxToolCalls { get; set; } = 4;

    public string SystemPrompt { get; set; } = string.Empty;

    public void Validate()
    {
        if (RequestTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                "Set Assistant:RequestTimeoutSeconds to a positive number of seconds.");
        }

        if (MaxToolCalls <= 0)
        {
            throw new InvalidOperationException("Set Assistant:MaxToolCalls to a positive number.");
        }

        if (string.IsNullOrWhiteSpace(SystemPrompt))
        {
            throw new InvalidOperationException(
                "Set Assistant:SystemPrompt to the instructions for the assistant.");
        }
    }
}
