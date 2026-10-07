using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Sentinel.Gateway.Proxy;

/// <summary>A chat completion request after strict validation. Only what the gateway understands survives parsing.</summary>
internal sealed record OpenAiChatRequest(
    IReadOnlyList<ChatMessage> Messages,
    string? Model,
    int? MaxOutputTokens,
    bool Stream,
    bool IncludeUsage,
    double? Temperature,
    double? TopP,
    double? PresencePenalty,
    double? FrequencyPenalty,
    long? Seed,
    IReadOnlyList<string>? Stop);

internal sealed record OpenAiParseError(string Code, string Message, string? Param);

/// <summary>
/// Parses an OpenAI <c>/v1/chat/completions</c> body with an allow-list. Anything the gateway cannot inspect — tools,
/// images, audio, response formats, extra metadata fields — is refused instead of being forwarded blindly: a field that
/// is not scanned for PII and injections is a field that bypasses the guardrails.
/// </summary>
internal static class OpenAiChatRequestParser
{
    private const int MaxMessages = 200;
    private const int MaxStopSequences = 4;
    private const int MaxStopLength = 64;

    private static readonly HashSet<string> AllowedTopLevel = new(StringComparer.Ordinal)
    {
        "model", "messages", "max_tokens", "max_completion_tokens", "temperature", "top_p", "stream", "stream_options",
        "stop", "seed", "presence_penalty", "frequency_penalty", "n", "user",
    };

    public static (OpenAiChatRequest? Request, OpenAiParseError? Error) Parse(ReadOnlyMemory<byte> body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body.Span, documentOptions: new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            return Fail("invalid_json", "The request body is not valid JSON.");
        }

        if (root is not JsonObject json)
        {
            return Fail("invalid_json", "The request body must be a JSON object.");
        }

        foreach (var property in json)
        {
            if (!AllowedTopLevel.Contains(property.Key))
            {
                return Fail("unsupported_parameter", $"The parameter '{property.Key}' is not supported by this gateway.", property.Key);
            }
        }

        if (json["n"] is { } n && !(n is JsonValue nv && nv.TryGetValue<int>(out var count) && count == 1))
        {
            return Fail("unsupported_parameter", "Only n = 1 is supported.", "n");
        }

        var messages = ParseMessages(json["messages"], out var messagesError);
        if (messages is null)
        {
            return (null, messagesError);
        }

        if (!TryString(json, "model", out var model, out var error)
            || !TryBool(json, "stream", out var stream, out error)
            || !TryInt(json, "max_tokens", out var maxTokens, out error)
            || !TryInt(json, "max_completion_tokens", out var maxCompletionTokens, out error)
            || !TryDouble(json, "temperature", 0, 2, out var temperature, out error)
            || !TryDouble(json, "top_p", 0, 1, out var topP, out error)
            || !TryDouble(json, "presence_penalty", -2, 2, out var presence, out error)
            || !TryDouble(json, "frequency_penalty", -2, 2, out var frequency, out error)
            || !TryLong(json, "seed", out var seed, out error)
            || !TryStop(json, out var stop, out error)
            || !TryStreamOptions(json, out var includeUsage, out error))
        {
            return (null, error);
        }

        var limit = maxCompletionTokens ?? maxTokens;
        if (limit is <= 0)
        {
            return Fail("invalid_value", "The output token limit must be positive.", maxCompletionTokens is not null ? "max_completion_tokens" : "max_tokens");
        }

        return (new OpenAiChatRequest(messages, model, limit, stream ?? false, includeUsage, temperature, topP, presence, frequency, seed, stop), null);
    }

    private static List<ChatMessage>? ParseMessages(JsonNode? node, out OpenAiParseError? error)
    {
        error = null;
        if (node is not JsonArray array || array.Count == 0 || array.Count > MaxMessages)
        {
            error = new OpenAiParseError("invalid_messages", $"'messages' must be an array of 1 to {MaxMessages} messages.", "messages");
            return null;
        }

        var messages = new List<ChatMessage>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject message)
            {
                error = new OpenAiParseError("invalid_messages", $"messages[{i}] must be an object.", $"messages[{i}]");
                return null;
            }

            foreach (var property in message)
            {
                if (property.Key is not ("role" or "content" or "name"))
                {
                    error = new OpenAiParseError(
                        "unsupported_parameter", $"messages[{i}].{property.Key} is not supported by this gateway.", $"messages[{i}].{property.Key}");
                    return null;
                }
            }

            var role = message["role"] is JsonValue roleValue && roleValue.TryGetValue<string>(out var roleText) ? roleText : null;
            ChatRole? chatRole = role switch
            {
                "system" or "developer" => ChatRole.System,
                "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                _ => null,
            };

            if (chatRole is null)
            {
                error = new OpenAiParseError("invalid_role", $"messages[{i}].role must be system, developer, user or assistant.", $"messages[{i}].role");
                return null;
            }

            var text = ParseContent(message["content"]);
            if (text is null)
            {
                error = new OpenAiParseError(
                    "invalid_content", $"messages[{i}].content must be a string or an array of text parts.", $"messages[{i}].content");
                return null;
            }

            messages.Add(new ChatMessage(chatRole.Value, text));
        }

        return messages;
    }

    /// <summary>A string, or an array of <c>{"type":"text","text":"..."}</c> parts joined with newlines. Nothing else.</summary>
    private static string? ParseContent(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                return text;
            case JsonArray parts:
                var builder = new StringBuilder();
                foreach (var part in parts)
                {
                    if (part is not JsonObject obj
                        || obj.Count != 2
                        || !(obj["type"] is JsonValue type && type.TryGetValue<string>(out var typeName) && typeName == "text")
                        || !(obj["text"] is JsonValue textValue && textValue.TryGetValue<string>(out var partText)))
                    {
                        return null;
                    }

                    if (builder.Length > 0)
                    {
                        builder.Append('\n');
                    }

                    builder.Append(partText);
                }

                return builder.ToString();
            default:
                return null;
        }
    }

    private static (OpenAiChatRequest?, OpenAiParseError?) Fail(string code, string message, string? param = null) =>
        (null, new OpenAiParseError(code, message, param));

    private static bool TryString(JsonObject json, string name, out string? value, out OpenAiParseError? error)
    {
        value = null;
        error = null;
        if (json[name] is null)
        {
            return true;
        }

        if (json[name] is JsonValue node && node.TryGetValue<string>(out var text) && text.Length is > 0 and <= 128)
        {
            value = text;
            return true;
        }

        error = new OpenAiParseError("invalid_value", $"'{name}' must be a short string.", name);
        return false;
    }

    private static bool TryBool(JsonObject json, string name, out bool? value, out OpenAiParseError? error)
    {
        value = null;
        error = null;
        if (json[name] is null)
        {
            return true;
        }

        if (json[name] is JsonValue node && node.TryGetValue<bool>(out var flag))
        {
            value = flag;
            return true;
        }

        error = new OpenAiParseError("invalid_value", $"'{name}' must be a boolean.", name);
        return false;
    }

    private static bool TryInt(JsonObject json, string name, out int? value, out OpenAiParseError? error)
    {
        value = null;
        error = null;
        if (json[name] is null)
        {
            return true;
        }

        if (json[name] is JsonValue node && node.TryGetValue<int>(out var number))
        {
            value = number;
            return true;
        }

        error = new OpenAiParseError("invalid_value", $"'{name}' must be an integer.", name);
        return false;
    }

    private static bool TryLong(JsonObject json, string name, out long? value, out OpenAiParseError? error)
    {
        value = null;
        error = null;
        if (json[name] is null)
        {
            return true;
        }

        if (json[name] is JsonValue node && node.TryGetValue<long>(out var number))
        {
            value = number;
            return true;
        }

        error = new OpenAiParseError("invalid_value", $"'{name}' must be an integer.", name);
        return false;
    }

    private static bool TryDouble(JsonObject json, string name, double min, double max, out double? value, out OpenAiParseError? error)
    {
        value = null;
        error = null;
        if (json[name] is null)
        {
            return true;
        }

        if (json[name] is JsonValue node && node.TryGetValue<double>(out var number) && double.IsFinite(number) && number >= min && number <= max)
        {
            value = number;
            return true;
        }

        error = new OpenAiParseError("invalid_value", $"'{name}' must be a number between {min} and {max}.", name);
        return false;
    }

    private static bool TryStop(JsonObject json, out IReadOnlyList<string>? stop, out OpenAiParseError? error)
    {
        stop = null;
        error = null;
        var node = json["stop"];
        if (node is null)
        {
            return true;
        }

        var values = new List<string>();
        if (node is JsonValue single && single.TryGetValue<string>(out var one))
        {
            values.Add(one);
        }
        else if (node is JsonArray array && array.Count is > 0 and <= MaxStopSequences)
        {
            foreach (var item in array)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    values.Add(text);
                }
                else
                {
                    values.Clear();
                    break;
                }
            }
        }

        if (values.Count == 0 || values.Any(v => v.Length is 0 or > MaxStopLength))
        {
            error = new OpenAiParseError("invalid_value", $"'stop' must be 1 to {MaxStopSequences} strings of up to {MaxStopLength} characters.", "stop");
            return false;
        }

        stop = values;
        return true;
    }

    private static bool TryStreamOptions(JsonObject json, out bool includeUsage, out OpenAiParseError? error)
    {
        includeUsage = false;
        error = null;
        if (json["stream_options"] is null)
        {
            return true;
        }

        if (json["stream_options"] is JsonObject options)
        {
            foreach (var property in options)
            {
                if (property.Key != "include_usage")
                {
                    error = new OpenAiParseError("unsupported_parameter", $"stream_options.{property.Key} is not supported.", "stream_options");
                    return false;
                }
            }

            if (options["include_usage"] is null)
            {
                return true;
            }

            if (options["include_usage"] is JsonValue flag && flag.TryGetValue<bool>(out var value))
            {
                includeUsage = value;
                return true;
            }
        }

        error = new OpenAiParseError("invalid_value", "'stream_options' must be {\"include_usage\": boolean}.", "stream_options");
        return false;
    }
}
