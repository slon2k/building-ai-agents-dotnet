using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ExpenseAssistant.App;

public sealed class ToolDispatcher
{
    private readonly Dictionary<string, AIFunction> functionsByName;

    public ToolDispatcher(IReadOnlyList<AIFunction> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        Functions = Array.AsReadOnly(functions.ToArray());
        functionsByName = Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<AIFunction> Functions { get; }

    public async Task<object?> InvokeAsync(FunctionCallContent call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        cancellationToken.ThrowIfCancellationRequested();

        if (!functionsByName.TryGetValue(call.Name, out AIFunction? function))
        {
            return Failure("UNKNOWN_TOOL", "The requested tool is not available.");
        }

        try
        {
            JsonElement arguments = JsonSerializer.SerializeToElement(call.Arguments);
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                return Failure("INVALID_ARGUMENTS", "Tool arguments must be an object.");
            }

            if (function.JsonSchema.TryGetProperty("required", out JsonElement requiredProperties))
            {
                foreach (JsonElement required in requiredProperties.EnumerateArray())
                {
                    if (!arguments.TryGetProperty(required.GetString()!, out JsonElement value)
                        || value.ValueKind == JsonValueKind.Null)
                    {
                        return Failure("INVALID_ARGUMENTS", "A required tool argument is missing or null.");
                    }
                }
            }

            bool hasProperties = function.JsonSchema.TryGetProperty("properties", out JsonElement properties);
            foreach (JsonProperty argument in arguments.EnumerateObject())
            {
                if (!hasProperties || !properties.TryGetProperty(argument.Name, out _))
                {
                    return Failure("INVALID_ARGUMENTS", "The request contains an unsupported tool argument.");
                }
            }

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