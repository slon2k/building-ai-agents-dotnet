using System.Text.Json;
using ExpenseAssistant.Core;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.App;

internal static class ConversationMessageMapper
{
	private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

	public static IReadOnlyList<ChatMessage> ToChatMessages(IReadOnlyList<ConversationMessage> storedMessages)
		=> ToChatMessages(storedMessages, out _);

	public static IReadOnlyList<ChatMessage> ToChatMessages(
		IReadOnlyList<ConversationMessage> storedMessages, out IReadOnlyList<long> includedSequenceNumbers)
	{
		var messages = new List<ChatMessage>(storedMessages.Count);
		var sequences = new List<long>(storedMessages.Count);
		for (int index = 0; index < storedMessages.Count; index++)
		{
			ConversationMessage message = storedMessages[index];
			if (message.Role == ConversationMessageRole.Tool)
			{
				continue;
			}

			ConversationMessagePart[] calls = [.. message.Parts
				.Where(part => part.Kind == ConversationMessagePartKind.ToolCall)
				.OrderBy(part => part.Ordinal)];

			if (calls.Length > 0)
			{
				if (index + 1 >= storedMessages.Count || !IsCompleteToolExchange(calls, storedMessages[index + 1]))
				{
					continue;
				}

				ChatMessage? assistantMessage = ToChatMessage(message);
				ChatMessage? toolMessage = ToChatMessage(storedMessages[++index]);
				if (assistantMessage is not null && toolMessage is not null)
				{
					messages.Add(assistantMessage);
					messages.Add(toolMessage);
					sequences.Add(message.SequenceNumber);
					sequences.Add(storedMessages[index].SequenceNumber);
				}
				continue;
			}

			if (message.Parts.Any(part => part.Kind == ConversationMessagePartKind.ToolResult))
			{
				continue;
			}

			ChatMessage? chatMessage = ToChatMessage(message);
			if (chatMessage is not null)
			{
				messages.Add(chatMessage);
				sequences.Add(message.SequenceNumber);
			}
		}

		includedSequenceNumbers = sequences.AsReadOnly();
		return messages;
	}

	public static IReadOnlyList<ConversationMessageDraft> ToDrafts(
		IReadOnlyList<ChatMessage> messages, Guid runId)
	{
		var drafts = new List<ConversationMessageDraft>(messages.Count);
		foreach (ChatMessage message in messages)
		{
			ConversationMessageRole? role = ToStoredRole(message.Role);
			if (role is null)
			{
				continue;
			}

			var parts = new List<ConversationMessagePartDraft>();
			foreach (AIContent content in message.Contents)
			{
				switch (content)
				{
					case TextContent text:
						parts.Add(new TextMessagePartDraft(text.Text ?? string.Empty));
						break;
					case FunctionCallContent call:
						parts.Add(new ToolCallMessagePartDraft(
							call.CallId,
							call.Name,
							SerializeArguments(call.Arguments)));
						break;
					case FunctionResultContent result:
						parts.Add(new ToolResultMessagePartDraft(
							result.CallId,
							JsonSerializer.Serialize(result.Result, JsonOptions)));
						break;
				}
			}

			if (parts.Count > 0)
			{
				drafts.Add(new ConversationMessageDraft(role.Value, parts, runId));
			}
		}

		return drafts;
	}

	private static ChatMessage? ToChatMessage(ConversationMessage message)
	{
		ChatRole? role = ToChatRole(message.Role);
		if (role is null)
		{
			return null;
		}

		var contents = new List<AIContent>(message.Parts.Count);
		foreach (ConversationMessagePart part in message.Parts.OrderBy(part => part.Ordinal))
		{
			switch (part.Kind)
			{
				case ConversationMessagePartKind.Text:
					contents.Add(new TextContent(part.Text ?? string.Empty));
					break;
				case ConversationMessagePartKind.ToolCall:
					using (JsonDocument arguments = JsonDocument.Parse(part.ArgumentsJson!))
					{
						Dictionary<string, object?> values = arguments.RootElement.EnumerateObject()
							.ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
						contents.Add(new FunctionCallContent(part.CallId!, part.ToolName!, values));
					}
					break;
				case ConversationMessagePartKind.ToolResult:
					using (JsonDocument result = JsonDocument.Parse(part.ResultJson!))
					{
						contents.Add(new FunctionResultContent(part.CallId!, result.RootElement.Clone()));
					}
					break;
			}
		}

		return contents.Count == 0 ? null : new ChatMessage(role.Value, contents);
	}

	private static bool IsCompleteToolExchange(
		IReadOnlyList<ConversationMessagePart> calls, ConversationMessage toolMessage)
	{
		if (toolMessage.Role != ConversationMessageRole.Tool)
		{
			return false;
		}

		string[] callIds = [.. calls.Select(part => part.CallId!)];
		string[] resultIds = toolMessage.Parts
			.Where(part => part.Kind == ConversationMessagePartKind.ToolResult)
			.OrderBy(part => part.Ordinal)
			.Select(part => part.CallId!)
			.ToArray();
		return callIds.SequenceEqual(resultIds, StringComparer.Ordinal);
	}

	private static string SerializeArguments(object? arguments)
	{
		string json = JsonSerializer.Serialize(arguments ?? new Dictionary<string, object?>(), JsonOptions);
		using JsonDocument document = JsonDocument.Parse(json);
		return document.RootElement.ValueKind == JsonValueKind.Object ? json : "{}";
	}

	private static ConversationMessageRole? ToStoredRole(ChatRole role) =>
		role == ChatRole.User ? ConversationMessageRole.User
		: role == ChatRole.Assistant ? ConversationMessageRole.Assistant
		: role == ChatRole.Tool ? ConversationMessageRole.Tool
		: null;

	private static ChatRole? ToChatRole(ConversationMessageRole role) =>
		role == ConversationMessageRole.User ? ChatRole.User
		: role == ConversationMessageRole.Assistant ? ChatRole.Assistant
		: role == ConversationMessageRole.Tool ? ChatRole.Tool
		: null;
}