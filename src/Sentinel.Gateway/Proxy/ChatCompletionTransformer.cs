using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Features.Proxy;
using Sentinel.Application.Guardrails;
using Sentinel.Gateway.Http;
using Yarp.ReverseProxy.Forwarder;

namespace Sentinel.Gateway.Proxy;

/// <summary>
/// Forwards one vetted chat completion and guards what comes back. Requests are built from scratch (no inbound headers,
/// path or query reach the upstream; the caller's bearer token in particular never does) and responses are rebuilt from
/// a whitelist of fields, so text carried in fields the gateway does not inspect (<c>reasoning_content</c>,
/// <c>tool_calls</c>, <c>refusal</c>...) cannot get around the output guard.
/// </summary>
internal sealed partial class ChatCompletionTransformer(
    PreparedProxyCall call,
    OpenAiChatRequest request,
    int upstreamBodyLength,
    Uri upstreamUri,
    string? apiKey,
    int estimatedPromptTokens,
    IChatProxyService proxy,
    IPromptGuard promptGuard,
    ITokenCounter tokens,
    OpenAiProxyOptions options,
    ILogger logger) : HttpTransformer
{
    private int _completed;

    public bool Completed => Volatile.Read(ref _completed) == 1;

    public override ValueTask TransformRequestAsync(
        HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
    {
        proxyRequest.Method = HttpMethod.Post;
        proxyRequest.RequestUri = upstreamUri;
        proxyRequest.Headers.Host = null;

        // YARP builds the content from HttpContext.Request (the endpoint replaced its body with the vetted one) and does
        // not allow swapping it here; only its headers are ours to set, since no inbound header is copied.
        var content = proxyRequest.Content ?? throw new InvalidOperationException("The vetted request body was not attached.");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        content.Headers.ContentLength = upstreamBodyLength;
        proxyRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(request.Stream ? "text/event-stream" : "application/json"));
        if (!string.IsNullOrEmpty(apiKey))
        {
            proxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return ValueTask.CompletedTask;
    }

    public override async ValueTask<bool> TransformResponseAsync(
        HttpContext httpContext, HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
    {
        if (proxyResponse is null)
        {
            return true; // The request failed before any response: the endpoint reports it (see HandleChatCompletionsAsync).
        }

        try
        {
            if (!proxyResponse.IsSuccessStatusCode)
            {
                await RefuseUpstreamErrorAsync(httpContext, proxyResponse);
                return false;
            }

            var isEventStream = string.Equals(proxyResponse.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
            if (isEventStream)
            {
                await RelayStreamAsync(httpContext, proxyResponse, cancellationToken);
            }
            else
            {
                await RelayJsonAsync(httpContext, proxyResponse, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            await CompleteAsync(estimatedPromptTokens, 0, succeeded: false, "Request.Cancelled");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            LogRelayFailed(logger, exception.GetType().Name);
            await CompleteAsync(estimatedPromptTokens, 0, succeeded: false, "Upstream.Failed");
            if (httpContext.Response.HasStarted)
            {
                httpContext.Abort(); // a truncated stream must look truncated to the client
            }
            else
            {
                await OpenAiErrors.WriteAsync(httpContext, StatusCodes.Status502BadGateway, "api_error", "upstream_error", "The model provider failed.");
            }
        }

        return false;
    }

    /// <summary>Records the outcome once; transports have several exit paths and a double settlement would charge twice.</summary>
    public async Task CompleteAsync(int promptTokens, int completionTokens, bool succeeded, string? errorCode)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 1)
        {
            return;
        }

        await proxy.CompleteAsync(call, new ProxyCompletion(promptTokens, completionTokens, succeeded, errorCode), CancellationToken.None);
    }

    private async Task RefuseUpstreamErrorAsync(HttpContext http, HttpResponseMessage upstream)
    {
        // The provider's error body is not forwarded: it can echo parts of the (redacted) prompt and internal details.
        var status = (int)upstream.StatusCode;
        LogUpstreamRefused(logger, status);
        await CompleteAsync(0, 0, succeeded: false, $"Upstream.{status}");

        if (status == StatusCodes.Status429TooManyRequests)
        {
            var retryAfter = upstream.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
            await OpenAiErrors.WriteAsync(
                http, StatusCodes.Status429TooManyRequests, "rate_limit_error", "upstream_rate_limited",
                "The model provider is rate limiting this gateway. Retry later.", retryAfter: retryAfter);
            return;
        }

        await OpenAiErrors.WriteAsync(
            http, StatusCodes.Status502BadGateway, "api_error", "upstream_error", $"The model provider answered with status {status}.");
    }

    private async Task RelayJsonAsync(HttpContext http, HttpResponseMessage upstream, CancellationToken cancellationToken)
    {
        var bytes = await ReadLimitedAsync(upstream.Content, options.MaxResponseBytes, cancellationToken);
        var root = JsonNode.Parse(bytes);
        var choice = root?["choices"] is JsonArray { Count: > 0 } choices ? choices[0] : null;
        var content = AsString(choice?["message"]?["content"]);
        if (choice is null || content is null)
        {
            await CompleteAsync(estimatedPromptTokens, 0, succeeded: false, "Upstream.InvalidResponse");
            await OpenAiErrors.WriteAsync(http, StatusCodes.Status502BadGateway, "api_error", "upstream_error", "The model provider returned an unreadable response.");
            return;
        }

        var (prompt, completion) = ReadUsage(root?["usage"], content);
        var guarded = proxy.GuardOutput(call, content);
        var response = new JsonObject
        {
            ["id"] = AsString(root?["id"]) ?? "chatcmpl-" + Guid.NewGuid().ToString("N"),
            ["object"] = "chat.completion",
            ["created"] = AsLong(root?["created"]) ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = AsString(root?["model"]) ?? call.Route.Model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = guarded },
                ["finish_reason"] = AsString(choice["finish_reason"]) ?? "stop",
            }),
            ["usage"] = UsageNode(prompt, completion),
        };

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(GatewayJson.Serialize(response)), cancellationToken);
        await CompleteAsync(prompt, completion, succeeded: true, null);
    }

    private async Task RelayStreamAsync(HttpContext http, HttpResponseMessage upstream, CancellationToken cancellationToken)
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store, no-transform"; // answers are tenant data; no-transform keeps intermediaries from buffering the stream
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.StartAsync(cancellationToken);

        var output = promptGuard.CreateOutputStream(call.Vault);
        var produced = new StringBuilder();
        string? id = null;
        string? model = null;
        long? created = null;
        JsonNode? usage = null;
        var flushed = false;

        await using var stream = await upstream.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue; // comments, event names, blank separators: the gateway writes its own framing
            }

            var data = line.AsSpan(5).Trim();
            if (data.SequenceEqual("[DONE]"))
            {
                break;
            }

            JsonNode? chunk;
            try
            {
                chunk = JsonNode.Parse(data.ToString());
            }
            catch (JsonException)
            {
                continue;
            }

            id ??= AsString(chunk?["id"]);
            model ??= AsString(chunk?["model"]);
            created ??= AsLong(chunk?["created"]);
            if (chunk?["usage"] is JsonObject reported)
            {
                usage = reported;
            }

            if (chunk?["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice)
            {
                continue; // the usage-only chunk (empty choices) is re-emitted at the end if the client asked for it
            }

            var delta = choice["delta"] as JsonObject;
            var content = AsString(delta?["content"]);
            var role = AsString(delta?["role"]) is null ? null : "assistant";
            var finish = AsString(choice["finish_reason"]);

            produced.Append(content);
            var safe = content is null ? string.Empty : output.Push(content);
            if (finish is not null)
            {
                safe += output.Flush(); // nothing may stay held back once the answer is complete
                flushed = true;
            }

            if (safe.Length == 0 && role is null && finish is null)
            {
                continue; // everything of this delta is held back until it is clear whether it is PII
            }

            await WriteChunkAsync(http, id, model, created, role, safe, finish, cancellationToken);
        }

        if (!flushed)
        {
            var rest = output.Flush();
            if (rest.Length > 0)
            {
                await WriteChunkAsync(http, id, model, created, null, rest, null, cancellationToken);
            }
        }

        var (prompt, completion) = ReadUsage(usage, produced.ToString());
        if (request.IncludeUsage)
        {
            var usageChunk = new JsonObject
            {
                ["id"] = id ?? "chatcmpl-" + Guid.NewGuid().ToString("N"),
                ["object"] = "chat.completion.chunk",
                ["created"] = created ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = model ?? call.Route.Model,
                ["choices"] = new JsonArray(),
                ["usage"] = UsageNode(prompt, completion),
            };
            await WriteEventAsync(http, GatewayJson.Serialize(usageChunk), cancellationToken);
        }

        await WriteEventAsync(http, "[DONE]", cancellationToken);
        await CompleteAsync(prompt, completion, succeeded: true, null);
    }

    private Task WriteChunkAsync(
        HttpContext http, string? id, string? model, long? created, string? role, string content, string? finishReason, CancellationToken cancellationToken)
    {
        var delta = new JsonObject();
        if (role is not null)
        {
            delta["role"] = role;
        }

        if (content.Length > 0 || role is not null)
        {
            delta["content"] = content;
        }

        var chunk = new JsonObject
        {
            ["id"] = id ?? "chatcmpl-" + Guid.NewGuid().ToString("N"),
            ["object"] = "chat.completion.chunk",
            ["created"] = created ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model ?? call.Route.Model,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finishReason }),
        };
        return WriteEventAsync(http, GatewayJson.Serialize(chunk), cancellationToken);
    }

    private static async Task WriteEventAsync(HttpContext http, string data, CancellationToken cancellationToken)
    {
        await http.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"data: {data}\n\n"), cancellationToken);
        await http.Response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>Usage as reported by the provider; when it is missing, counted from what was sent and what came back.</summary>
    private (int Prompt, int Completion) ReadUsage(JsonNode? usage, string producedText)
    {
        var prompt = AsInt(usage?["prompt_tokens"]);
        var completion = AsInt(usage?["completion_tokens"]);
        return (prompt is > 0 ? prompt.Value : estimatedPromptTokens, completion is > 0 ? completion.Value : tokens.CountTokens(producedText));
    }

    private static JsonObject UsageNode(int prompt, int completion) => new()
    {
        ["prompt_tokens"] = prompt,
        ["completion_tokens"] = completion,
        ["total_tokens"] = prompt + completion,
    };

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new InvalidOperationException("The upstream response is larger than the configured limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string? AsString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static long? AsLong(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    private static int? AsInt(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The model provider answered with status {Status}")]
    private static partial void LogUpstreamRefused(ILogger logger, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Relaying the provider response failed ({ExceptionType})")]
    private static partial void LogRelayFailed(ILogger logger, string exceptionType);
}
