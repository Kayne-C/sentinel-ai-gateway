using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Application.Features.Proxy;
using Sentinel.Application.Guardrails;
using Sentinel.Domain.Common;
using Sentinel.Gateway.Http;
using Sentinel.Gateway.Security;
using Sentinel.Infrastructure.AI;
using Yarp.ReverseProxy.Forwarder;

namespace Sentinel.Gateway.Proxy;

/// <summary>
/// The OpenAI-compatible surface (<c>/v1/chat/completions</c>, <c>/v1/models</c>): existing OpenAI SDKs and tools point
/// their base URL at the gateway and get PII redaction, injection blocking, token budgets, routing and an audit entry
/// without any code change. The data plane is YARP's <see cref="IHttpForwarder"/>.
/// </summary>
internal static partial class OpenAiProxyEndpoints
{
    public static IEndpointRouteBuilder MapOpenAiProxy(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1").RequireAuthorization(GatewayPolicies.Proxy).WithTags("OpenAI-compatible proxy");
        v1.MapPost("/chat/completions", HandleChatCompletionsAsync).WithName("ChatCompletions");
        v1.MapGet("/models", HandleModels).WithName("Models");
        return app;
    }

    private static IResult HandleModels(IOptions<ModelCatalogOptions> catalog)
    {
        var data = new JsonArray();
        foreach (var (tier, options) in catalog.Value.Tiers.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            data.Add(new JsonObject { ["id"] = tier, ["object"] = "model", ["created"] = 0, ["owned_by"] = "sentinel", ["root"] = options.Model });
        }

        return Results.Json(new JsonObject { ["object"] = "list", ["data"] = data });
    }

    private static async Task HandleChatCompletionsAsync(
        HttpContext http,
        IChatProxyService proxy,
        IPromptGuard promptGuard,
        ITokenCounter tokens,
        IHttpForwarder forwarder,
        IUpstreamInvoker upstream,
        IOptions<AiOptions> ai,
        IOptions<OpenAiProxyOptions> proxyOptions,
        ILoggerFactory loggerFactory)
    {
        var options = proxyOptions.Value;
        var cancellationToken = http.RequestAborted;

        GatewayCaller caller;
        try
        {
            caller = GatewayCaller.Resolve(http);
        }
        catch (GatewayAuthException exception)
        {
            await OpenAiErrors.WriteAsync(
                http, ProblemResults.StatusFor(exception.Error), "invalid_request_error", exception.Error.Code, exception.Error.Description);
            return;
        }

        var upstreamUri = OpenAiUpstream.ChatCompletionsUri(ai.Value);
        if (upstreamUri is null)
        {
            await OpenAiErrors.WriteAsync(
                http, StatusCodes.Status503ServiceUnavailable, "api_error", "no_upstream",
                "No model provider is configured for the proxy (Ai:Provider is Offline).");
            return;
        }

        var body = await ReadBodyAsync(http.Request, options.MaxRequestBytes, cancellationToken);
        if (body is null)
        {
            await OpenAiErrors.WriteAsync(
                http, StatusCodes.Status413PayloadTooLarge, "invalid_request_error", "request_too_large",
                $"The request body must not exceed {options.MaxRequestBytes} bytes.");
            return;
        }

        var (parsed, parseError) = OpenAiChatRequestParser.Parse(body.Value);
        if (parsed is null)
        {
            await OpenAiErrors.InvalidRequestAsync(http, parseError!.Code, parseError.Message, parseError.Param);
            return;
        }

        var prepared = await proxy.PrepareAsync(
            new ProxyChatRequest(caller.Identity, parsed.Messages, parsed.Model, parsed.MaxOutputTokens, parsed.Stream), cancellationToken);
        if (prepared.IsFailure)
        {
            await WriteRefusalAsync(http, prepared.Error);
            return;
        }

        var call = prepared.Value;
        var estimatedPromptTokens = call.SanitizedMessages.Sum(m => tokens.CountTokens(m.Text));
        var upstreamBody = BuildUpstreamBody(call, parsed, options);
        var transformer = new ChatCompletionTransformer(
            call, parsed, upstreamBody.Length, upstreamUri, ai.Value.ApiKey, estimatedPromptTokens,
            proxy, promptGuard, tokens, options, loggerFactory.CreateLogger<ChatCompletionTransformer>());

        // What the forwarder sends upstream is built from the request: replace the caller's body with the vetted one.
        http.Request.Body = new MemoryStream(upstreamBody, writable: false);
        http.Request.ContentLength = upstreamBody.Length;

        ForwarderError error;
        try
        {
            error = await forwarder.SendAsync(
                http,
                upstreamUri.GetLeftPart(UriPartial.Authority),
                upstream.Invoker,
                new ForwarderRequestConfig
                {
                    ActivityTimeout = options.UpstreamIdleTimeout,
                    Version = upstreamUri.Scheme == Uri.UriSchemeHttps ? System.Net.HttpVersion.Version20 : System.Net.HttpVersion.Version11,
                    VersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower,
                },
                transformer,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await transformer.CompleteAsync(estimatedPromptTokens, 0, succeeded: false, "Request.Cancelled");
            throw;
        }

        if (error != ForwarderError.None && !transformer.Completed)
        {
            var failure = http.GetForwarderErrorFeature();
            loggerFactory.CreateLogger("Sentinel.Gateway.Proxy").LogWarning(
                "Forwarding to the model provider failed: {Error} ({ExceptionType}: {Message})",
                error, failure?.Exception?.GetType().Name, failure?.Exception?.InnerException?.Message ?? failure?.Exception?.Message);

            var status = error == ForwarderError.RequestTimedOut ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status502BadGateway;
            var code = error == ForwarderError.RequestCanceled ? "Request.Cancelled" : "Upstream.Unreachable";
            await transformer.CompleteAsync(estimatedPromptTokens, 0, succeeded: false, code);
            await OpenAiErrors.WriteAsync(http, status, "api_error", "upstream_error", "The model provider could not be reached.");
        }
        else if (!transformer.Completed)
        {
            await transformer.CompleteAsync(estimatedPromptTokens, 0, succeeded: false, "Upstream.Failed");
        }
    }

    /// <summary>The only body that ever leaves the gateway: sanitised messages, the routed model and clamped parameters.</summary>
    internal static byte[] BuildUpstreamBody(PreparedProxyCall call, OpenAiChatRequest request, OpenAiProxyOptions options)
    {
        var messages = new JsonArray();
        foreach (var message in call.SanitizedMessages)
        {
            var role = message.Role == Microsoft.Extensions.AI.ChatRole.System ? "system"
                : message.Role == Microsoft.Extensions.AI.ChatRole.Assistant ? "assistant"
                : "user";
            messages.Add(new JsonObject { ["role"] = role, ["content"] = message.Text });
        }

        var body = new JsonObject
        {
            ["model"] = call.Route.Model,
            ["messages"] = messages,
            [options.MaxTokensParameter] = call.MaxOutputTokens,
            ["stream"] = request.Stream,
        };

        if (request.Stream)
        {
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.TopP is { } topP)
        {
            body["top_p"] = topP;
        }

        if (request.PresencePenalty is { } presence)
        {
            body["presence_penalty"] = presence;
        }

        if (request.FrequencyPenalty is { } frequency)
        {
            body["frequency_penalty"] = frequency;
        }

        if (request.Seed is { } seed)
        {
            body["seed"] = seed;
        }

        if (request.Stop is { Count: > 0 } stop)
        {
            body["stop"] = new JsonArray(stop.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        }

        return Encoding.UTF8.GetBytes(body.ToJsonString());
    }

    private static Task WriteRefusalAsync(HttpContext http, Error error)
    {
        var retryAfter = (error as RateLimitedError)?.RetryAfter;
        return error.Type switch
        {
            ErrorType.PolicyViolation => OpenAiErrors.WriteAsync(
                http, StatusCodes.Status400BadRequest, "invalid_request_error", "prompt_injection_detected", error.Description),
            ErrorType.RateLimited => OpenAiErrors.WriteAsync(
                http, StatusCodes.Status429TooManyRequests, "insufficient_quota", "token_budget_exceeded", error.Description, retryAfter: retryAfter),
            ErrorType.Unavailable => OpenAiErrors.WriteAsync(
                http, StatusCodes.Status503ServiceUnavailable, "api_error", "service_unavailable", error.Description),
            _ => OpenAiErrors.WriteAsync(
                http, ProblemResults.StatusFor(error), "invalid_request_error", error.Code, error.Description),
        };
    }

    /// <summary>Reads at most <paramref name="limit"/> bytes; <c>null</c> when the body is larger.</summary>
    private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(HttpRequest request, int limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit)
        {
            return null;
        }

        var writer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var result = await request.BodyReader.ReadAsync(cancellationToken);
            var buffer = result.Buffer;
            if (writer.WrittenCount + buffer.Length > limit)
            {
                request.BodyReader.AdvanceTo(buffer.End);
                return null;
            }

            foreach (var segment in buffer)
            {
                writer.Write(segment.Span);
            }

            request.BodyReader.AdvanceTo(buffer.End);
            if (result.IsCompleted)
            {
                return writer.WrittenMemory;
            }
        }
    }
}
