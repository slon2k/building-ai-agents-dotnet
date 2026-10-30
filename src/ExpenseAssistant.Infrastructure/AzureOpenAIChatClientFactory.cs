using System.Diagnostics;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace ExpenseAssistant.Infrastructure;

public static class AzureOpenAIChatClientFactory
{
    public const int MaxProviderRetries = 3;
    public const string ActivitySourceName = "ExpenseAssistant.Infrastructure.AzureOpenAI";
    private static readonly ActivitySource Activities = new(ActivitySourceName);

    public static IChatClient Create(AzureOpenAIOptions options) => Create(options, null);

    public static IChatClient Create(AzureOpenAIOptions options, PipelineTransport? transport)
    {
        var endpoint = new Uri($"{options.Endpoint.TrimEnd('/')}/openai/v1/");
        var credential = new ApiKeyCredential(options.ApiKey);
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            RetryPolicy = new ClientRetryPolicy(MaxProviderRetries)
        };
        clientOptions.AddPolicy(new RetryTelemetryPolicy(), PipelinePosition.PerTry);
        if (transport is not null)
        {
            clientOptions.Transport = transport;
        }

        return new ChatClient(
            model: options.Deployment,
            credential: credential,
            options: clientOptions)
            .AsIChatClient();
    }

    private sealed class RetryTelemetryPolicy : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            using Activity? activity = StartAttempt(message);
            long started = Stopwatch.GetTimestamp();
            try
            {
                ProcessNext(message, pipeline, currentIndex);
                FinishAttempt(activity, message);
            }
            catch (Exception exception)
            {
                FinishAttempt(activity, message, exception);
                throw;
            }
            finally
            {
                activity?.SetTag("duration.ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            using Activity? activity = StartAttempt(message);
            long started = Stopwatch.GetTimestamp();
            try
            {
                await ProcessNextAsync(message, pipeline, currentIndex);
                FinishAttempt(activity, message);
            }
            catch (Exception exception)
            {
                FinishAttempt(activity, message, exception);
                throw;
            }
            finally
            {
                activity?.SetTag("duration.ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        private static Activity? StartAttempt(PipelineMessage message)
        {
            if (!message.TryGetProperty(typeof(AttemptState), out object? property))
            {
                property = new AttemptState();
                message.SetProperty(typeof(AttemptState), property);
            }
            int retryCount = ((AttemptState)property!).Attempts++;
            Activity? modelActivity = Activity.Current;
            while (modelActivity is not null && modelActivity.OperationName != "model.step")
            {
                modelActivity = modelActivity.Parent;
            }
            modelActivity?.SetTag("provider.retry.count", retryCount);

            Activity? activity = Activities.StartActivity("provider.attempt", ActivityKind.Client);
            activity?.SetTag("retry.count", retryCount);
            activity?.SetTag("session.id", modelActivity?.GetTagItem("session.id"));
            activity?.SetTag("run.id", modelActivity?.GetTagItem("run.id"));
            activity?.SetTag("model.step", modelActivity?.GetTagItem("model.step"));
            return activity;
        }

        private static void FinishAttempt(Activity? activity, PipelineMessage message, Exception? exception = null)
        {
            int? status = message.Response?.Status;
            string? errorCode = exception is OperationCanceledException ? "Cancelled"
                : exception is not null ? "Failed"
                : status >= 400 ? $"HTTP_{status}" : null;
            activity?.SetTag("http.response.status_code", status);
            activity?.SetTag("outcome", errorCode is null ? "Completed" : errorCode == "Cancelled" ? "Cancelled" : "Failed");
            activity?.SetTag("error.code", errorCode);
            activity?.SetStatus(errorCode is null ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        }

        private sealed class AttemptState
        {
            public int Attempts { get; set; }
        }
    }
}