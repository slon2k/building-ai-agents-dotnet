using ExpenseAssistant.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ExpenseAssistant.App;

public sealed class ConsoleChat(IServiceScopeFactory scopeFactory)
{
	public async Task RunAsync(CancellationToken cancellationToken = default)
	{
		Session activeSession = await GetInitialSessionAsync(cancellationToken);

		PrintActiveSession(activeSession, "Session");
		Console.WriteLine("Ask an expense question. Commands: /new [name], /sessions, /use <session-id>, /history. Type 'exit' to quit.");

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

			await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
			ISessionStore sessionStore = scope.ServiceProvider.GetRequiredService<ISessionStore>();
			if (input.TrimStart().StartsWith('/'))
			{
				activeSession = await HandleCommandAsync(input, activeSession, sessionStore, cancellationToken);
				continue;
			}

			using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			AgentRunner runner = scope.ServiceProvider.GetRequiredService<AgentRunner>();
			Task<AgentRunResult> runTask = runner.RunAsync(activeSession.SessionId, input, cts.Token);

			// .NET has no async key read, so poll for Esc while the request runs.
			while (!runTask.IsCompleted)
			{
				if (!Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
				{
					cts.Cancel();
				}

				await Task.Delay(50, cancellationToken);
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

	private async Task<Session> GetInitialSessionAsync(CancellationToken cancellationToken)
	{
		await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
		ISessionStore sessionStore = scope.ServiceProvider.GetRequiredService<ISessionStore>();
		IReadOnlyList<Session> sessions = await sessionStore.ListSessionsAsync(cancellationToken);
		if (sessions.Count > 0)
		{
			return sessions[0];
		}

		return await sessionStore.CreateSessionAsync("New session", cancellationToken);
	}

	private static async Task<Session> HandleCommandAsync(
		string input, Session activeSession, ISessionStore sessionStore, CancellationToken cancellationToken)
	{
		string[] parts = input.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
		string command = parts[0].ToLowerInvariant();
		string argument = parts.Length > 1 ? parts[1] : string.Empty;

		switch (command)
		{
			case "/new":
				activeSession = await sessionStore.CreateSessionAsync(
					string.IsNullOrWhiteSpace(argument) ? "New session" : argument, cancellationToken);
				PrintActiveSession(activeSession, "Created and selected session");
				break;
			case "/sessions":
				IReadOnlyList<Session> sessions = await sessionStore.ListSessionsAsync(cancellationToken);
				foreach (Session session in sessions)
				{
					string marker = session.SessionId == activeSession.SessionId ? " (active)" : string.Empty;
					Console.WriteLine($"{session.DisplayName} ({session.SessionId}){marker}");
				}
				if (sessions.Count == 0)
				{
					Console.WriteLine("No sessions.");
				}
				break;
			case "/use":
				if (!Guid.TryParse(argument, out Guid sessionId))
				{
					Console.WriteLine("Usage: /use <session-id>");
					break;
				}
				Session? selectedSession = await sessionStore.GetSessionAsync(sessionId, cancellationToken);
				if (selectedSession is null)
				{
					Console.WriteLine($"Session not found: {sessionId}");
					break;
				}
				activeSession = selectedSession;
				PrintActiveSession(activeSession, "Selected session");
				break;
			case "/history":
				IReadOnlyList<ConversationMessage> messages = await sessionStore.GetMessagesAsync(
					activeSession.SessionId, cancellationToken: cancellationToken);
				if (messages.Count == 0)
				{
					Console.WriteLine("No saved messages in this session.");
					break;
				}
				foreach (ConversationMessage message in messages)
				{
					Console.WriteLine($"{message.SequenceNumber} {message.Role}:");
					foreach (ConversationMessagePart part in message.Parts.OrderBy(part => part.Ordinal))
					{
						string? content = part.Kind switch
						{
							ConversationMessagePartKind.Text => part.Text,
							ConversationMessagePartKind.ToolCall => $"Tool call {part.ToolName} ({part.CallId}): {part.ArgumentsJson}",
							ConversationMessagePartKind.ToolResult => $"Tool result ({part.CallId}): {part.ResultJson}",
							_ => null
						};
						if (content is not null)
						{
							Console.WriteLine($"  {content}");
						}
					}
				}
				break;
			default:
				Console.WriteLine("Unknown command. Use /new [name], /sessions, /use <session-id>, or /history.");
				break;
		}

		return activeSession;
	}

	private static void PrintActiveSession(Session session, string label) =>
		Console.WriteLine($"{label}: {session.DisplayName} ({session.SessionId})");
}