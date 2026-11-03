using System.Text.Json;
using Microsoft.Extensions.AI;
using SharpToken;

namespace ExpenseAssistant.App;

public interface IInputTokenEstimator
{
	long Estimate(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AIFunction> tools);
}

public sealed class InputTokenEstimator : IInputTokenEstimator
{
	// Chat framing isn't plain text, so include fixed per-message/tool allowances alongside tokenizer counts.
	private const int MessageFramingOverhead = 4;
	private const int ToolFramingOverhead = 8;
	private const int RequestFramingOverhead = 3;
	private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;
	private readonly GptEncoding encoding;

	public InputTokenEstimator(string encodingName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(encodingName);
		encoding = GptEncoding.GetEncoding(encodingName);
	}

	public long Estimate(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AIFunction> tools)
	{
		ArgumentNullException.ThrowIfNull(messages);
		ArgumentNullException.ThrowIfNull(tools);
		long tokenCount = RequestFramingOverhead;

		foreach (ChatMessage message in messages)
		{
			tokenCount += MessageFramingOverhead + Count(message.Role.ToString());
			foreach (AIContent content in message.Contents)
			{
				tokenCount += content switch
				{
					TextContent text => Count(text.Text),
					FunctionCallContent call => Count(call.Name) + Count(JsonSerializer.Serialize(call.Arguments, JsonOptions)),
					FunctionResultContent result => Count(result.CallId) + Count(JsonSerializer.Serialize(result.Result, JsonOptions)),
					_ => throw new NotSupportedException($"Input token estimation does not support {content.GetType().Name} content.")
				};
			}
		}

		foreach (AIFunction tool in tools)
		{
			tokenCount += ToolFramingOverhead
				+ Count(tool.Name)
				+ Count(tool.Description)
				+ Count(tool.JsonSchema.GetRawText());
		}

		return tokenCount;
	}

	private int Count(string text) => encoding.CountTokens(text);
}