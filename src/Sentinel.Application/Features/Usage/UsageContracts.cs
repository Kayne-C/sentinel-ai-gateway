using Sentinel.Application.Abstractions;
using Sentinel.Application.Abstractions.Messaging;
using Sentinel.Domain.Identity;

namespace Sentinel.Application.Features.Usage;

/// <summary>Remaining token budget of the caller's tenant and of the caller.</summary>
public sealed record GetUsageQuery(CallerIdentity Caller) : IQuery<BudgetStatus>;
