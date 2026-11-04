using ExpenseAssistant.App;

namespace ExpenseAssistant.Tests;

public class AssistantOptionsTests
{
    [Fact]
    public void Validate_WithDefaultContextBudget_LeavesExpectedInputCapacity()
    {
        AssistantOptions options = CreateOptions();

        options.Validate();

        Assert.Equal(16_384, options.ContextBudgetTokens);
        Assert.Equal(2_048, options.ReservedOutputTokens);
        Assert.Equal(1_024, options.ContextSafetyMarginTokens);
        Assert.Equal("o200k_base", options.TokenizerEncoding);
        Assert.Equal(13_312, options.AvailableInputTokens);
    }

    [Theory]
    [InlineData(0, 2_048, 1_024)]
    [InlineData(16_384, 0, 1_024)]
    [InlineData(16_384, 2_048, 0)]
    [InlineData(3_072, 2_048, 1_024)]
    [InlineData(int.MaxValue, int.MaxValue, 1)]
    public void Validate_RejectsInvalidContextBudget(int contextBudget, int reservedOutput, int safetyMargin)
    {
        AssistantOptions options = CreateOptions();
        options.ContextBudgetTokens = contextBudget;
        options.ReservedOutputTokens = reservedOutput;
        options.ContextSafetyMarginTokens = safetyMargin;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsBlankTokenizerEncoding()
    {
        AssistantOptions options = CreateOptions();
        options.TokenizerEncoding = " ";

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    private static AssistantOptions CreateOptions() => new()
    {
        SystemPrompt = "Test assistant."
    };
}