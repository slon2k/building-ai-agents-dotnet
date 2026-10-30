using System.Diagnostics;
using System.Text.Json;
using ExpenseAssistant.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExpenseAssistant.App;

public sealed class AgentRunner
{
	public const string ActivitySourceName = "ExpenseAssistant.App.AgentRunner";
	private static readonly ActivitySource Activities = new(ActivitySourceName);

	private readonly IChatClient chatClient;
	private readonly ToolDispatcher dispatcher;
	private readonly TimeSpan requestTimeout;
	private readonly string systemPrompt;
	private readonly int maxToolCalls;
	private readonly ILogger<AgentRunner> logger;

	public AgentRunner(
		IChatClient chatClient, ToolDispatcher dispatcher, TimeSpan requestTimeout, string systemPrompt,
		int maxToolCalls = 4, ILogger<AgentRunner>? logger = null)
	{
		ArgumentNullException.ThrowIfNull(chatClient);
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestTimeout, TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(requestTimeout.TotalMilliseconds, uint.MaxValue - 1d);
		ArgumentOutOfRangeException.ThrowIfLessThan(maxToolCalls, 1);

		this.chatClient = chatClient;
		this.dispatcher = dispatcher;
		this.requestTimeout = requestTimeout;
		this.systemPrompt = systemPrompt;
		this.maxToolCalls = maxToolCalls;
		this.logger = logger ?? NullLogger<AgentRunner>.Instance;
	}

	public async Task<ChatRunResult> RunAsync(Guid sessionId, string userInput, CancellationToken cancellationToken)
	{
		Guid runId = Guid.NewGuid();
		long startTimestamp = Stopwatch.GetTimestamp();
		using IDisposable? logScope = logger.BeginScope("SessionId:{SessionId} RunId:{RunId}", sessionId, runId);
		using Activity? runActivity = StartSpan("assistant.run");
		List<UsageDetails?> modelUsages = [];
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(requestTimeout);
		logger.LogInformation("Run started");

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
				new ChatOptions { Tools = dispatcher.Functions.Cast<AITool>().ToList() }, "initial", runToken);
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
			string toolName = dispatcher.Functions.Any(function => function.Name == call.Name) ? call.Name : "unrecognized";
			using IDisposable? toolScope = logger.BeginScope("ToolCallId:{ToolCallId} ToolName:{ToolName}", call.CallId, toolName);
			using Activity? activity = StartSpan("tool.execute");
			activity?.SetTag("tool.call.id", call.CallId);
			activity?.SetTag("tool.name", toolName);
			long started = Stopwatch.GetTimestamp();
			logger.LogInformation("Tool validation and execution started");
			try
			{
				object? result = overLimit
					? ToolResult<object>.Failure(new Error("TOOL_CALL_LIMIT", "This request exceeded the tool-call limit. The tool was not executed."))
					: await dispatcher.InvokeAsync(call, token);
				JsonElement envelope = JsonSerializer.SerializeToElement(result, JsonSerializerOptions.Web);
				bool success = envelope.ValueKind != JsonValueKind.Object
					|| !envelope.TryGetProperty("isSuccess", out JsonElement isSuccess) || isSuccess.GetBoolean();
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
}