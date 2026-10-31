using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using ExpenseAssistant.App;
using ExpenseAssistant.Core;
using ExpenseAssistant.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExpenseAssistant.Tests;

public class AzureOpenAIEmbeddingsTests
{
    [Fact]
    public async Task GenerateAsync_UsesEmbeddingDeploymentAndReturnsVector()
    {
        string? body = null;
        Uri? uri = null;
        using var handler = new StubHandler(async (request, token) =>
        {
            uri = request.RequestUri;
            body = await request.Content!.ReadAsStringAsync(token);
            return Reply(200, SuccessResponse("[0.6,0.8]"));
        });
        using var http = new HttpClient(handler);
        using var embeddings = CreateEmbeddings(http);

        ReadOnlyMemory<float> vector = await embeddings.GenerateAsync("Merchant: DeskSupply; Description: Laptop stand");

        Assert.Equal([0.6f, 0.8f], vector.ToArray());
        Assert.Equal("https://expense-assistant.invalid/openai/v1/embeddings", uri!.AbsoluteUri);
        using JsonDocument request = JsonDocument.Parse(body!);
        Assert.Equal("fake-embedding-model", request.RootElement.GetProperty("model").GetString());
        Assert.Equal("Merchant: DeskSupply; Description: Laptop stand",
            request.RootElement.GetProperty("input")[0].GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task GenerateAsync_RejectsBlankInputWithoutHttpRequest(string? text)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Reply(200, SuccessResponse("[1,0]"))));
        using var http = new HttpClient(handler);
        using var embeddings = CreateEmbeddings(http);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => embeddings.GenerateAsync(text!));

        Assert.Equal(0, handler.Attempts);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[0,0]")]
    [InlineData("[1e100,0]")]
    public async Task GenerateAsync_RejectsInvalidVector(string vector)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Reply(200, SuccessResponse(vector))));
        using var http = new HttpClient(handler);
        using var embeddings = CreateEmbeddings(http);

        await Assert.ThrowsAsync<InvalidOperationException>(() => embeddings.GenerateAsync("Laptop stand"));
    }

    [Fact]
    public async Task GenerateAsync_RejectsMissingEmbedding()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Reply(200,
            """{"object":"list","model":"fake-embedding-model","data":[],"usage":{"prompt_tokens":3,"total_tokens":3}}""")));
        using var http = new HttpClient(handler);
        using var embeddings = CreateEmbeddings(http);

        await Assert.ThrowsAsync<InvalidOperationException>(() => embeddings.GenerateAsync("Laptop stand"));
    }

    [Fact]
    public async Task GenerateAsync_PropagatesProviderFailure()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Reply(401,
            """{"error":{"message":"test error","type":"invalid_request_error","code":"invalid_api_key"}}""")));
        using var http = new HttpClient(handler);
        using var embeddings = CreateEmbeddings(http);

        await Assert.ThrowsAsync<ClientResultException>(() => embeddings.GenerateAsync("Laptop stand"));

        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task GenerateAsync_RetriesTransientProviderFailure()
    {
        int attempts = 0;
        using var handler = new StubHandler((_, _) => Task.FromResult(++attempts == 1
            ? Reply(503, """{"error":{"message":"test error","type":"server_error","code":"unavailable"}}""")
            : Reply(200, SuccessResponse("[1,0]"))));
        using var http = new HttpClient(handler);
        using var embeddings = CreateEmbeddings(http);

        ReadOnlyMemory<float> vector = await embeddings.GenerateAsync("Laptop stand");

        Assert.Equal([1, 0], vector.ToArray());
        Assert.Equal(2, handler.Attempts);
    }

    [Fact]
    public async Task GenerateAsync_PropagatesCancellationDuringRequest()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHandler(async (_, token) =>
        {
            received.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Reply(200, SuccessResponse("[1,0]"));
        });
        using var http = new HttpClient(handler);
        using var embeddings = CreateEmbeddings(http);
        using var cancellation = new CancellationTokenSource();

        Task<ReadOnlyMemory<float>> operation = embeddings.GenerateAsync("Laptop stand", cancellation.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task GenerateAsync_WithCancelledToken_DoesNotSendRequest()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(Reply(200, SuccessResponse("[1,0]"))));
        using var http = new HttpClient(handler);
        using var embeddings = CreateEmbeddings(http);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            embeddings.GenerateAsync("Laptop stand", new CancellationToken(true)));

        Assert.Equal(0, handler.Attempts);
    }

    [Fact]
    public void AddEmbeddings_ValidatesLazilyAndDoesNotRequireChatDeployment()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://expense-assistant.invalid",
                ["AzureOpenAI:ApiKey"] = "test-only-key",
                ["AzureOpenAI:EmbeddingDeployment"] = "fake-embedding-model"
            }).Build());
        services.AddEmbeddings();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<AzureOpenAIEmbeddings>(provider.GetRequiredService<IEmbeddings>());
        Assert.Same(provider.GetRequiredService<IEmbeddings>(), provider.GetRequiredService<IEmbeddings>());
    }

    [Fact]
    public void AddEmbeddings_MissingConfigurationFailsOnlyWhenResolved()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddEmbeddings();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IEmbeddings>());
    }

    [Fact]
    public void ValidateEmbeddings_RequiresEmbeddingDeploymentButChatDoesNot()
    {
        var options = Options();
        options.Deployment = "chat-model";
        options.EmbeddingDeployment = "";

        options.Validate();
        Assert.Throws<InvalidOperationException>(options.ValidateEmbeddings);
    }

    private static AzureOpenAIOptions Options() => new()
    {
        Endpoint = "https://expense-assistant.invalid",
        ApiKey = "test-only-key",
        EmbeddingDeployment = "fake-embedding-model"
    };

    private static AzureOpenAIEmbeddings CreateEmbeddings(HttpClient client) =>
        new(Options(), NullLogger<AzureOpenAIEmbeddings>.Instance, new HttpClientPipelineTransport(client));

    private static string SuccessResponse(string vector) =>
        $$$"""{"object":"list","model":"fake-embedding-model","data":[{"object":"embedding","index":0,"embedding":{{{vector}}} }],"usage":{"prompt_tokens":3,"total_tokens":3}}""";

    private static HttpResponseMessage Reply(int status, string content) => new((HttpStatusCode)status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return handler(request, cancellationToken);
        }
    }
}
