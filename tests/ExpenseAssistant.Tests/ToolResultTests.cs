using ExpenseAssistant.App;
using ExpenseAssistant.Core;

namespace ExpenseAssistant.Tests;

public class ToolResultTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Success_WithResult_PreservesSuccessfulState(bool useImplicitConversion)
    {
        var data = new TransactionSearchResult([], false);

        ToolResult<TransactionSearchResult> result = useImplicitConversion
            ? data
            : ToolResult<TransactionSearchResult>.Success(data);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Same(data, result.Result);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failure_WithError_PreservesFailedState(bool useImplicitConversion)
    {
        var error = new Error("INVALID_DATE_RANGE", "The end date must be after the start date.");

        ToolResult<TransactionSearchResult> result = useImplicitConversion
            ? error
            : ToolResult<TransactionSearchResult>.Failure(error);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Null(result.Result);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public void Success_WithNullResult_Throws()
    {
        Assert.Throws<ArgumentException>(() => ToolResult<TransactionSearchResult>.Success(null!));
    }

    [Fact]
    public void Failure_WithNullError_Throws()
    {
        Assert.Throws<ArgumentException>(() => ToolResult<TransactionSearchResult>.Failure(null!));
    }
}