using ExpenseAssistant.App;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.Tests;

public class InputTokenEstimatorTests
{
    [Fact]
    public void Estimate_CountsToolSchemasAndMessageContent()
    {
        var estimator = new InputTokenEstimator("o200k_base");
        ChatMessage message = new(ChatRole.User, "March grocery transactions");
        AIFunction tool = AIFunctionFactory.Create((string query) => query, "SearchTransactions");

        long withoutTools = estimator.Estimate([message], []);
        long withTool = estimator.Estimate([message], [tool]);

        Assert.True(withoutTools > 0);
        Assert.True(withTool > withoutTools);
    }

    [Fact]
    public void Estimate_CountsToolCallsAndResultsInMessages()
    {
        var estimator = new InputTokenEstimator("o200k_base");
        ChatMessage plainMessage = new(ChatRole.User, "Question");
        ChatMessage toolRequest = new(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "SearchTransactions", new Dictionary<string, object?>
            {
                ["query"] = "March groceries"
            })]);
        ChatMessage toolResult = new(ChatRole.Tool,
            [new FunctionResultContent("call-1", new { returnedCount = 2, merchant = "Green Market" })]);

        long plainEstimate = estimator.Estimate([plainMessage], []);
        long exchangeEstimate = estimator.Estimate([plainMessage, toolRequest, toolResult], []);

        Assert.True(exchangeEstimate > plainEstimate);
    }
}