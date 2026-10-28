using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ExpenseAssistant.Core;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.App;

public sealed class ToolChatRunner
{
    private readonly IChatClient chatClient;
    private readonly TimeSpan requestTimeout;
    private readonly string systemPrompt;
    private readonly int maxToolCalls;
    private readonly IReadOnlyList<AIFunction> functions;
    private readonly Dictionary<string, AIFunction> functionsByName;

    public ToolChatRunner(
        IChatClient chatClient, ExpenseTools tools, TimeSpan requestTimeout, string systemPrompt, int maxToolCalls = 4)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxToolCalls, 1);

        this.chatClient = chatClient;
        this.requestTimeout = requestTimeout;
        this.systemPrompt = systemPrompt;
        this.maxToolCalls = maxToolCalls;
        functions = tools.CreateFunctions();
        functionsByName = functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    }

    public async Task<ChatRunResult> RunAsync(Guid sessionId, string userInput, CancellationToken cancellationToken)
    {
        Guid runId = Guid.NewGuid();
        long startTimestamp = Stopwatch.GetTimestamp();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(requestTimeout);

        try
        {
            CancellationToken runToken = timeoutCts.Token;
            runToken.ThrowIfCancellationRequested();
            List<ChatMessage> messages =
            [
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userInput)
            ];
            ChatResponse response = await chatClient.GetResponseAsync(messages,
                new ChatOptions { Tools = functions.Cast<AITool>().ToList() }, runToken);
            runToken.ThrowIfCancellationRequested();

            FunctionCallContent[] calls = response.Messages
                .SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();

            if (calls.Length > 0)
            {
                if (calls.Any(call => string.IsNullOrWhiteSpace(call.CallId))
                    || calls.Select(call => call.CallId).Distinct(StringComparer.Ordinal).Count() != calls.Length)
                {
                    return Result(ChatRunStatus.Failed, error: "The model returned invalid tool-call identifiers.");
                }

                messages.AddRange(response.Messages);
                List<AIContent> results = [];

                for (int index = 0; index < calls.Length; index++)
                {
                    runToken.ThrowIfCancellationRequested();
                    FunctionCallContent call = calls[index];
                    object? result = index < maxToolCalls
                        ? await InvokeAsync(call, runToken)
                        : Failure("TOOL_CALL_LIMIT", "This request exceeded the tool-call limit. The tool was not executed.");
                    results.Add(new FunctionResultContent(call.CallId, result));
                }

                messages.Add(new ChatMessage(ChatRole.Tool, results));
                response = await chatClient.GetResponseAsync(messages,
                    new ChatOptions { ToolMode = ChatToolMode.None }, runToken);
                runToken.ThrowIfCancellationRequested();

                if (response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Any())
                {
                    return Result(ChatRunStatus.Failed, error: "The model requested additional tools after the bounded tool round.");
                }
            }

            return Result(ChatRunStatus.Completed, response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(ChatRunStatus.Cancelled);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return Result(ChatRunStatus.TimedOut, error: $"Request exceeded the {requestTimeout.TotalSeconds:0.#}s timeout.");
        }
        catch (Exception)
        {
            return Result(ChatRunStatus.Failed, error: "The assistant request could not be completed.");
        }

        ChatRunResult Result(ChatRunStatus status, ChatResponse? response = null, string? error = null) =>
            new(sessionId, runId, status, Stopwatch.GetElapsedTime(startTimestamp), response, error);
    }

    private async Task<object?> InvokeAsync(FunctionCallContent call, CancellationToken cancellationToken)
    {
        if (!functionsByName.TryGetValue(call.Name, out AIFunction? function))
        {
            return Failure("UNKNOWN_TOOL", "The requested tool is not available.");
        }

        JsonElement arguments = JsonSerializer.SerializeToElement(call.Arguments);
        JsonElement properties = function.JsonSchema.GetProperty("properties");
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return Failure("INVALID_ARGUMENTS", "Tool arguments must be an object containing the required dates.");
        }

        foreach (JsonElement required in function.JsonSchema.GetProperty("required").EnumerateArray())
        {
            if (!arguments.TryGetProperty(required.GetString()!, out JsonElement value)
                || value.ValueKind == JsonValueKind.Null)
            {
                return Failure("INVALID_ARGUMENTS", "Both startDate and endDate are required.");
            }
        }

        foreach (JsonProperty argument in arguments.EnumerateObject())
        {
            if (!properties.TryGetProperty(argument.Name, out _) || !IsValidArgument(argument.Name, argument.Value))
            {
                return Failure("INVALID_ARGUMENTS", "Use supported arguments, YYYY-MM-DD dates, string filters, and an integer limit.");
            }
        }

        try
        {
            return await function.InvokeAsync(new AIFunctionArguments(call.Arguments!), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or FormatException or OverflowException)
        {
            return Failure("INVALID_ARGUMENTS", "The tool arguments could not be read. Check the required fields and their types.");
        }
        catch (Exception)
        {
            return Failure("TOOL_FAILED", "The tool could not be executed. No result is available.");
        }
    }

    private static bool IsValidArgument(string name, JsonElement value) => name switch
    {
        "startDate" or "endDate" => value.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "categoryId" or "merchantText" => value.ValueKind is JsonValueKind.String or JsonValueKind.Null,
        "limit" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
        _ => false
    };

    private static ToolResult<object> Failure(string code, string message) => new Error(code, message);
}