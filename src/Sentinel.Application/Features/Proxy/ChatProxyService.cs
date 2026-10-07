using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Application.Common;
using Sentinel.Application.Features.Ask;
using Sentinel.Application.Guardrails;
using Sentinel.Domain.Audit;
using Sentinel.Domain.Common;
using Sentinel.Domain.Identity;
using Sentinel.Guardrails.Pii;
using RoutingContext = Sentinel.Application.Abstractions.RoutingContext;

namespace Sentinel.Application.Features.Proxy;

/// <summary>
/// Policy for the OpenAI-compatible proxy: the transport forwards only <see cref="PreparedProxyCall.SanitizedMessages"/>,
/// with an output cap no larger than the tier allows, after the worst case has been reserved against the budget.
/// Completion is idempotent because transports have several exit paths (normal end, client abort, provider error) and
/// a double settlement would refund or charge twice.
/// </summary>
internal sealed partial class ChatProxyService(
    IPromptGuard promptGuard,
    ITokenBudget tokenBudget,
    ITokenCounter tokenCounter,
    IModelRouter modelRouter,
    IAuditLog auditLog,
    IOptions<ModelCatalogOptions> modelCatalog,
    IOptions<AuditPolicyOptions> auditPolicy,
    TimeProvider timeProvider,
    ILogger<ChatProxyService> logger) : IChatProxyService
{
    /// <summary>
    /// Per-call state that the public <see cref="PreparedProxyCall"/> contract has no room for. Keyed weakly by the call
    /// itself, so it lives exactly as long as the call and works even if completion happens in another DI scope.
    /// </summary>
    private static readonly ConditionalWeakTable<PreparedProxyCall, CallState> States = new();

    public async Task<Result<PreparedProxyCall>> PrepareAsync(ProxyChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Caller);
        ArgumentNullException.ThrowIfNull(request.Messages);

        var caller = request.Caller;
        var vault = new PiiVault();
        var state = new CallState(caller, timeProvider.GetTimestamp());
        ModelRoute? route = null;
        try
        {
            var input = await promptGuard.GuardInputAsync(request.Messages, vault, cancellationToken);
            state.Guarded(PromptText(input.Messages), input.RedactedPii, input.Injection.Rules);
            if (input.Blocked)
            {
                LogRequestBlocked(logger, input.Injection.Detector, string.Join(' ', input.Injection.Rules));
                await AuditAsync(state, AuditOutcome.Blocked, AskErrors.PromptInjection, route: null, model: null, 0, 0, 0m);
                return Error.PolicyViolation(AskErrors.PromptInjection, "The request was rejected by the prompt-injection guardrail.");
            }

            // The router sees the latest user turn as "the prompt" and everything else as context; together they are
            // the whole input. The requested model is only a hint: the router enforces the allow-list.
            var lastUser = input.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
            var lastUserTokens = tokenCounter.CountTokens(lastUser);
            var inputTokens = input.Messages.Sum(m => tokenCounter.CountTokens(m.Text));
            route = modelRouter.Route(new RoutingContext(
                caller.TenantId, lastUser, lastUserTokens, Math.Max(0, inputTokens - lastUserTokens), request.RequestedModel));
            var tier = ModelCallAccounting.ResolveTier(modelCatalog.Value, route);
            var model = ModelCallAccounting.ModelFor(route, tier);

            var tierMaximum = Math.Max(1, tier.MaxOutputTokens);
            var maxOutputTokens = request.MaxOutputTokens is > 0 and var requested ? Math.Min(requested, tierMaximum) : tierMaximum;

            BudgetLease lease;
            try
            {
                lease = await tokenBudget.ReserveAsync(
                    new BudgetRequest(caller.TenantId, caller.SubjectId, inputTokens + maxOutputTokens), cancellationToken);
            }
            catch (Exception exception) when (!ModelCallAccounting.IsCallerCancellation(exception, cancellationToken))
            {
                LogBudgetFailed(logger, exception.GetType().Name);
                await AuditAsync(state, AuditOutcome.Failed, ModelCallAccounting.BudgetUnavailable, route, model, 0, 0, 0m);
                return Error.Unavailable(ModelCallAccounting.BudgetUnavailable, "The token budget service is unavailable.");
            }

            if (!lease.Granted)
            {
                ModelCallAccounting.RecordBudgetRejection(lease);
                await AuditAsync(state, AuditOutcome.Throttled, AskErrors.BudgetExceeded, route, model, 0, 0, 0m);
                return new RateLimitedError(AskErrors.BudgetExceeded, "The token budget for this period is exhausted.", lease.RetryAfter);
            }

            var call = new PreparedProxyCall
            {
                Request = request,
                SanitizedMessages = input.Messages,
                Route = route,
                MaxOutputTokens = maxOutputTokens,
                Lease = lease,
                Vault = vault,
                RedactedPii = input.RedactedPii,
                StartedAtTimestamp = state.StartedAt,
            };

            States.AddOrUpdate(call, state);
            return call;
        }
        catch (Exception exception) when (!state.AuditAttempted)
        {
            // Unexpected failures are audited too; the original exception still propagates to the host.
            var cancelled = ModelCallAccounting.IsCallerCancellation(exception, cancellationToken);
            if (!cancelled)
            {
                LogUnexpectedFailure(logger, exception.GetType().Name);
            }

            try
            {
                await AuditAsync(
                    state, AuditOutcome.Failed, cancelled ? ModelCallAccounting.RequestCancelled : ModelCallAccounting.InternalError, route, route?.Model, 0, 0, 0m);
            }
            catch (Exception auditException)
            {
                LogAuditFailed(logger, auditException.GetType().Name);
            }

            throw;
        }
    }

    public string GuardOutput(PreparedProxyCall call, string modelText)
    {
        ArgumentNullException.ThrowIfNull(call);
        return promptGuard.GuardOutput(modelText, call.Vault);
    }

    public async Task CompleteAsync(PreparedProxyCall call, ProxyCompletion completion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(completion);

        // A call this service did not prepare (constructed by hand) still gets a complete audit entry.
        var state = States.GetValue(call, static c =>
        {
            var fallback = new CallState(c.Request.Caller, c.StartedAtTimestamp);
            fallback.Guarded(PromptText(c.SanitizedMessages), c.RedactedPii, []);
            return fallback;
        });

        if (Interlocked.Exchange(ref state.Completed, 1) == 1)
        {
            LogDuplicateCompletion(logger);
            return;
        }

        var promptTokens = Math.Max(0, completion.PromptTokens);
        var completionTokens = Math.Max(0, completion.CompletionTokens);
        var billed = promptTokens + completionTokens;
        if (completion.Succeeded && billed == 0)
        {
            // A successful call always consumed input tokens, so zero means "not reported" (e.g. a stream without a
            // usage chunk). Keep the reservation rather than let unreported usage bypass the budget.
            LogUsageNotReported(logger, call.Route.Tier);
            billed = call.Lease.ReservedTokens;
        }

        if (call.Lease.Granted)
        {
            try
            {
                // Bookkeeping must complete even when the client has disconnected, hence no request token.
                await tokenBudget.SettleAsync(call.Lease, billed, CancellationToken.None);
            }
            catch (Exception exception)
            {
                LogSettlementFailed(logger, exception.GetType().Name);
            }
        }

        var tier = ModelCallAccounting.ResolveTier(modelCatalog.Value, call.Route);
        var cost = tier.EstimateCost(promptTokens, completionTokens);
        var outcome = completion.Succeeded ? AuditOutcome.Allowed : AuditOutcome.Failed;
        var errorCode = completion.Succeeded ? null : completion.ErrorCode ?? AskErrors.ModelUnavailable;

        await AuditAsync(state, outcome, errorCode, call.Route, ModelCallAccounting.ModelFor(call.Route, tier), promptTokens, completionTokens, cost);
        ModelCallAccounting.RecordUsage(call.Route.Tier, promptTokens, completionTokens, cost);
    }

    private Task<AuditEntry> AuditAsync(
        CallState state, AuditOutcome outcome, string? errorCode, ModelRoute? route, string? model, int promptTokens, int completionTokens, decimal cost)
    {
        state.AuditAttempted = true;
        var auditEvent = new AuditEvent
        {
            TenantId = state.Caller.TenantId,
            SubjectId = state.Caller.SubjectId,
            Operation = AuditOperation.ChatCompletion,
            Outcome = outcome,
            OccurredAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            Model = model,
            ModelTier = route?.Tier,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            EstimatedCostUsd = cost,
            RedactedPii = ModelCallAccounting.PiiTypeCounts(state.RedactedPii),
            GuardrailFindings = state.Findings,
            PromptDigest = state.RedactedPrompt is null ? null : ModelCallAccounting.PromptDigest(state.RedactedPrompt),
            RedactedPrompt = auditPolicy.Value.StoreRedactedPrompts ? state.RedactedPrompt : null,
            ErrorCode = errorCode,
            LatencyMs = timeProvider.GetElapsedTime(state.StartedAt).TotalMilliseconds,
            TraceId = ModelCallAccounting.CurrentTraceId(),
        };

        // Not cancellable: an outcome that happened must be recorded even if the caller disconnected.
        return auditLog.AppendAsync(auditEvent, CancellationToken.None);
    }

    /// <summary>Canonical text of the sanitised conversation; its SHA-256 is the audit's prompt digest.</summary>
    private static string PromptText(IReadOnlyList<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append('[').Append(message.Role.Value).Append("]\n").Append(message.Text);
        }

        return builder.ToString();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Proxy request blocked by the prompt-injection guardrail ({Detector}: {Rules})")]
    private static partial void LogRequestBlocked(ILogger logger, string detector, string rules);

    [LoggerMessage(Level = LogLevel.Error, Message = "Token budget reservation failed ({ExceptionType})")]
    private static partial void LogBudgetFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Token budget settlement failed ({ExceptionType}); the reservation stays charged")]
    private static partial void LogSettlementFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provider usage was not reported for a successful call on tier {Tier}; the reservation is kept")]
    private static partial void LogUsageNotReported(ILogger logger, string tier);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CompleteAsync was called more than once for the same proxy call; ignoring the repeat")]
    private static partial void LogDuplicateCompletion(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Proxy preparation failed unexpectedly ({ExceptionType})")]
    private static partial void LogUnexpectedFailure(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Audit append failed ({ExceptionType}) while recording a failed request")]
    private static partial void LogAuditFailed(ILogger logger, string exceptionType);

    private sealed class CallState(CallerIdentity caller, long startedAt)
    {
        private static readonly IReadOnlyDictionary<PiiType, int> NoPii = new Dictionary<PiiType, int>();

        /// <summary>0 until the first <see cref="CompleteAsync"/>; flipped atomically.</summary>
        public int Completed;

        public CallerIdentity Caller { get; } = caller;

        public long StartedAt { get; } = startedAt;

        /// <summary>Null until the input guard has produced the sanitised conversation.</summary>
        public string? RedactedPrompt { get; private set; }

        public IReadOnlyDictionary<PiiType, int> RedactedPii { get; private set; } = NoPii;

        public IReadOnlyList<string> Findings { get; private set; } = [];

        public bool AuditAttempted { get; set; }

        public void Guarded(string redactedPrompt, IReadOnlyDictionary<PiiType, int> redactedPii, IEnumerable<string> findings)
        {
            RedactedPrompt = redactedPrompt;
            RedactedPii = redactedPii;
            Findings = [.. findings.Select(f => f.Replace(',', '_')).Distinct(StringComparer.Ordinal)];
        }
    }
}
