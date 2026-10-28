namespace ExpenseAssistant.App;

public sealed record ToolResult<TResult> where TResult : class
{
    private ToolResult(bool isSuccess, TResult? result = default, Error? error = default)
    {
        if (isSuccess && error is not null)
        {
            throw new ArgumentException("A successful result cannot have an error.", nameof(error));
        }
        if (!isSuccess && result is not null)
        {
            throw new ArgumentException("A failed result cannot have a result.", nameof(result));
        }
        if (!isSuccess && error is null)
        {
            throw new ArgumentException("A failed result must have an error.", nameof(error));
        }
        if (isSuccess && result is null)
        {
            throw new ArgumentException("A successful result must have a result.", nameof(result));
        }

        IsSuccess = isSuccess;
        Result = result;
        Error = error;
    }

    public TResult? Result { get; }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error? Error { get; }

    public static ToolResult<TResult> Success(TResult result) => new(true, result);

    public static ToolResult<TResult> Failure(Error error) => new(false, default, error);

    public static implicit operator ToolResult<TResult>(TResult result) => Success(result);

    public static implicit operator ToolResult<TResult>(Error error) => Failure(error);
}

public record Error(string Code, string Message);