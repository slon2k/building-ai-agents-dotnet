using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using ExpenseAssistant.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Embeddings;

namespace ExpenseAssistant.Infrastructure;

public sealed class AzureOpenAIEmbeddings : IEmbeddings, IDisposable
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> generator;
    private readonly ILogger<AzureOpenAIEmbeddings> logger;

    public AzureOpenAIEmbeddings(
        AzureOpenAIOptions options,
        ILogger<AzureOpenAIEmbeddings> logger,
        PipelineTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        options.ValidateEmbeddings();
        this.logger = logger;
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri($"{options.Endpoint.TrimEnd('/')}/openai/v1/"),
            RetryPolicy = new ClientRetryPolicy(AzureOpenAIChatClientFactory.MaxProviderRetries)
        };
        if (transport is not null)
        {
            clientOptions.Transport = transport;
        }

        generator = new EmbeddingClient(options.EmbeddingDeployment, new ApiKeyCredential(options.ApiKey), clientOptions)
            .AsIEmbeddingGenerator();
    }

    public async Task<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        try
        {
            GeneratedEmbeddings<Embedding<float>> embeddings = await generator.GenerateAsync(
                [text], cancellationToken: cancellationToken);
            if (embeddings.Count != 1)
            {
                throw new InvalidOperationException("The embedding provider must return exactly one vector.");
            }

            ReadOnlyMemory<float> vector = embeddings[0].Vector;
            EmbeddingVector.Validate(vector.Span);
            logger.LogInformation(
                "Embedding generation completed Dimensions={Dimensions} DurationMs={DurationMs} TotalTokens={TotalTokens}",
                vector.Length, Stopwatch.GetElapsedTime(started).TotalMilliseconds, embeddings.Usage?.TotalTokenCount);
            return vector;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Embedding generation cancelled DurationMs={DurationMs}",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        catch (Exception)
        {
            logger.LogError("Embedding generation failed ErrorCode={ErrorCode} DurationMs={DurationMs}",
                "EMBEDDING_FAILED", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    public void Dispose() => generator.Dispose();

}
