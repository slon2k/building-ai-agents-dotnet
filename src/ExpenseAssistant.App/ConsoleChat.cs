using Microsoft.Extensions.DependencyInjection;

namespace ExpenseAssistant.App;

public sealed class ConsoleChat(IServiceScopeFactory scopeFactory)
{
	public Guid SessionId { get; } = Guid.NewGuid();

	public async Task RunAsync(CancellationToken cancellationToken = default)
	{
		Console.WriteLine($"Session: {SessionId}");
		Console.WriteLine("Ask an expense question. Press Esc to cancel a request, type 'exit' to quit.");

		while (!cancellationToken.IsCancellationRequested)
		{
			Console.Write("You: ");
			string? input = await Console.In.ReadLineAsync(cancellationToken);

			if (input is null || input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
			{
				break;
			}

			if (string.IsNullOrWhiteSpace(input))
			{
				continue;
			}

			using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
			AgentRunner runner = scope.ServiceProvider.GetRequiredService<AgentRunner>();
			Task<AgentRunResult> runTask = runner.RunAsync(SessionId, input, cts.Token);

			// .NET has no async key read, so poll for Esc while the request runs.
			while (!runTask.IsCompleted)
			{
				if (!Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
				{
					cts.Cancel();
				}

				await Task.Delay(50);
			}

			AgentRunResult result = await runTask;

			switch (result)
			{
				case { Status: AgentRunStatus.Completed, Response: { } response }:
					response.PrintResponse();
					result.PrintMetadata();
					break;
				case { Status: AgentRunStatus.Cancelled }:
					Console.WriteLine("[cancelled]");
					break;
				default:
					Console.WriteLine($"[{result.Status}] {result.Error}");
					break;
			}

			Console.WriteLine($"Run: {result.RunId}, status={result.Status}, duration={result.Duration.TotalMilliseconds:0} ms");
		}
	}
}