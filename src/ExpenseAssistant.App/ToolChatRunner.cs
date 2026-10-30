using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExpenseAssistant.App;

public sealed class ToolChatRunner
{
    public const string ActivitySourceName = "ExpenseAssistant.App.ToolChatRunner";
    private static readonly ActivitySource Activities = new(ActivitySourceName);

    private readonly IChatClient chatClient;
    private readonly ILogger<ToolChatRunner> logger;
    private readonly TimeSpan requestTimeout;
    private readonly string systemPrompt;
    private readonly int maxToolCalls;
    private readonly IReadOnlyList<AIFunction> functions;
    private readonly Dictionary<string, AIFunction> functionsByName;

    public ToolChatRunner(
        IChatClient chatClient, ExpenseTools tools, TimeSpan requestTimeout, string systemPrompt, int maxToolCalls = 4,
        ILogger<ToolChatRunner>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxToolCalls, 1);

        this.chatClient = chatClient;
        this.logger = logger ?? NullLogger<ToolChatRunner>.Instance;
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
        using IDisposable? logScope = logger.BeginScope("SessionId:{SessionId} RunId:{RunId}", sessionId, runId);
        using Activity? runActivity = StartSpan("assistant.run");
        List<UsageDetails?> modelUsages = [];
        logger.LogInformation("Run started");
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
            ChatResponse response = await RequestModelAsync(messages,
                new ChatOptions { Tools = functions.Cast<AITool>().ToList() }, "initial", runToken);
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
                    object? result = await ExecuteToolAsync(call, index >= maxToolCalls, runToken);
                    results.Add(new FunctionResultContent(call.CallId, result));
                }

                messages.Add(new ChatMessage(ChatRole.Tool, results));
                response = await RequestModelAsync(messages,
                    new ChatOptions { ToolMode = ChatToolMode.None }, "final", runToken);
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

        ChatRunResult Result(ChatRunStatus status, ChatResponse? response = null, string? error = null)
        {
            TimeSpan duration = Stopwatch.GetElapsedTime(startTimestamp);
            UsageDetails? usage = AggregateUsage(modelUsages);
            string? errorCode = status == ChatRunStatus.Completed ? null : status.ToString();
            FinishSpan(runActivity, status.ToString(), errorCode);
            runActivity?.SetTag("duration.ms", duration.TotalMilliseconds);
            SetUsageTags(runActivity, usage);
            logger.Log(status == ChatRunStatus.Completed ? LogLevel.Information : LogLevel.Warning,
                "Run finished Outcome={Outcome} ErrorCode={ErrorCode} DurationMs={DurationMs} ModelRequests={ModelRequests} InputTokens={InputTokens} OutputTokens={OutputTokens} TotalTokens={TotalTokens}",
                status, errorCode, duration.TotalMilliseconds, modelUsages.Count,
                usage?.InputTokenCount, usage?.OutputTokenCount, usage?.TotalTokenCount);
            return new(sessionId, runId, status, duration, response, error, usage);
        }

        Activity? StartSpan(string name, ActivityKind kind = ActivityKind.Internal)
        {
            Activity? activity = Activities.StartActivity(name, kind);
            activity?.SetTag("session.id", sessionId.ToString());
            activity?.SetTag("run.id", runId.ToString());
            return activity;
        }

        async Task<ChatResponse> RequestModelAsync(
            List<ChatMessage> messages, ChatOptions options, string phase, CancellationToken token)
        {
            using Activity? activity = StartSpan($"model.{phase}", ActivityKind.Client);
            long started = Stopwatch.GetTimestamp();
            int usageIndex = modelUsages.Count;
            modelUsages.Add(null);
            logger.LogInformation("Model request started Phase={Phase}", phase);
            try
            {
                ChatResponse response = await chatClient.GetResponseAsync(messages, options, token);
                modelUsages[usageIndex] = response.Usage;
                FinishSpan(activity, "Completed");
                SetUsageTags(activity, response.Usage);
                activity?.SetTag("gen_ai.response.model", response.ModelId);
                logger.LogInformation(
                    "Model request finished Phase={Phase} Outcome={Outcome} DurationMs={DurationMs} InputTokens={InputTokens} OutputTokens={OutputTokens} TotalTokens={TotalTokens}",
                    phase, "Completed", Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, response.Usage?.TotalTokenCount);
                return response;
            }
            catch (Exception exception)
            {
                string code = CancellationCode(exception);
                FinishSpan(activity, code, code);
                logger.LogWarning("Model request failed Phase={Phase} ErrorCode={ErrorCode} DurationMs={DurationMs}",
                    phase, code, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                throw;
            }
            finally
            {
                activity?.SetTag("duration.ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        async Task<object?> ExecuteToolAsync(FunctionCallContent call, bool overLimit, CancellationToken token)
        {
            string toolName = functionsByName.ContainsKey(call.Name) ? call.Name : "unrecognized";
            using IDisposable? toolScope = logger.BeginScope("ToolCallId:{ToolCallId} ToolName:{ToolName}", call.CallId, toolName);
            using Activity? activity = StartSpan("tool.execute");
            activity?.SetTag("tool.call.id", call.CallId);
            activity?.SetTag("tool.name", toolName);
            long started = Stopwatch.GetTimestamp();
            logger.LogInformation("Tool validation and execution started");
            try
            {
                object? result = overLimit
                    ? Failure("TOOL_CALL_LIMIT", "This request exceeded the tool-call limit. The tool was not executed.")
                    : await InvokeAsync(call, token);
                JsonElement envelope = JsonSerializer.SerializeToElement(result, JsonSerializerOptions.Web);
                bool success = envelope.GetProperty("isSuccess").GetBoolean();
                string? code = success ? null : envelope.GetProperty("error").GetProperty("code").GetString();
                FinishSpan(activity, success ? "Completed" : "Failed", code);
                logger.Log(success ? LogLevel.Information : LogLevel.Warning,
                    "Tool finished Outcome={Outcome} ErrorCode={ErrorCode} DurationMs={DurationMs}",
                    success ? "Completed" : "Failed", code, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return result;
            }
            catch (Exception exception)
            {
                string code = CancellationCode(exception);
                FinishSpan(activity, code, code);
                logger.LogWarning("Tool interrupted ErrorCode={ErrorCode} DurationMs={DurationMs}",
                    code, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                throw;
            }
            finally
            {
                activity?.SetTag("duration.ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        string CancellationCode(Exception exception) => exception is OperationCanceledException
            ? cancellationToken.IsCancellationRequested ? "Cancelled" : timeoutCts.IsCancellationRequested ? "TimedOut" : "Failed"
            : "Failed";
    }

    private static void FinishSpan(Activity? activity, string outcome, string? errorCode = null)
    {
        activity?.SetTag("outcome", outcome);
        activity?.SetTag("error.code", errorCode);
        activity?.SetStatus(errorCode is null ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
    }

    private static void SetUsageTags(Activity? activity, UsageDetails? usage)
    {
        activity?.SetTag("gen_ai.usage.input_tokens", usage?.InputTokenCount);
        activity?.SetTag("gen_ai.usage.output_tokens", usage?.OutputTokenCount);
        activity?.SetTag("gen_ai.usage.total_tokens", usage?.TotalTokenCount);
    }

    private static UsageDetails? AggregateUsage(List<UsageDetails?> usages)
    {
        if (usages.Count == 0 || usages.Any(usage => usage is null))
        {
            return null;
        }

        return new UsageDetails
        {
            InputTokenCount = Sum(usages.Select(usage => usage!.InputTokenCount)),
            OutputTokenCount = Sum(usages.Select(usage => usage!.OutputTokenCount)),
            TotalTokenCount = Sum(usages.Select(usage => usage!.TotalTokenCount))
        };

        static long? Sum(IEnumerable<long?> counts)
        {
            long?[] values = counts.ToArray();
            return values.Any(value => value is null) ? null : values.Sum(value => value!.Value);
        }
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
            if (!properties.TryGetProperty(argument.Name, out _))
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

    private static ToolResult<object> Failure(string code, string message) => new Error(code, message);
}