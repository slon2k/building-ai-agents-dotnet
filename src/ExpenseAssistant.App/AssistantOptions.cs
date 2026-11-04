namespace ExpenseAssistant.App;

public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    public int RequestTimeoutSeconds { get; set; } = 30;

    public int MaxToolCalls { get; set; } = 4;

    public int ContextBudgetTokens { get; set; } = 16_384;

    public int ReservedOutputTokens { get; set; } = 2_048;

    public int ContextSafetyMarginTokens { get; set; } = 1_024;

    public string TokenizerEncoding { get; set; } = "o200k_base";

    public int AvailableInputTokens => ContextBudgetTokens - ReservedOutputTokens - ContextSafetyMarginTokens;

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

        if (ContextBudgetTokens <= 0)
        {
            throw new InvalidOperationException("Set Assistant:ContextBudgetTokens to a positive number of tokens.");
        }

        if (ReservedOutputTokens <= 0)
        {
            throw new InvalidOperationException("Set Assistant:ReservedOutputTokens to a positive number of tokens.");
        }

        if (ContextSafetyMarginTokens <= 0)
        {
            throw new InvalidOperationException("Set Assistant:ContextSafetyMarginTokens to a positive number of tokens.");
        }

        if ((long)ReservedOutputTokens + ContextSafetyMarginTokens >= ContextBudgetTokens)
        {
            throw new InvalidOperationException(
                "Assistant:ContextBudgetTokens must exceed ReservedOutputTokens plus ContextSafetyMarginTokens.");
        }

        if (string.IsNullOrWhiteSpace(TokenizerEncoding))
        {
            throw new InvalidOperationException("Set Assistant:TokenizerEncoding to a supported tokenizer encoding.");
        }

        if (string.IsNullOrWhiteSpace(SystemPrompt))
        {
            throw new InvalidOperationException(
                "Set Assistant:SystemPrompt to the instructions for the assistant.");
        }
    }
}
