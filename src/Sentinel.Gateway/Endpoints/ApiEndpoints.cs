using Sentinel.Application.Abstractions.Messaging;
using Sentinel.Application.Features.Ask;
using Sentinel.Application.Features.Audit;
using Sentinel.Application.Features.Documents;
using Sentinel.Application.Features.Usage;
using Sentinel.Domain.Audit;
using Sentinel.Domain.Knowledge;
using Sentinel.Gateway.Http;
using Sentinel.Gateway.Security;

namespace Sentinel.Gateway.Endpoints;

public sealed record AskRequest(string Question, int? TopK = null);

public sealed record UpsertDocumentRequest(
    string Title,
    string Content,
    Classification Classification,
    IReadOnlyList<string> Principals,
    string? SourceUri = null);

public static class ApiEndpoints
{
    public const int AskBodyLimit = 64 * 1024;
    public const int DocumentBodyLimit = 8 * 1024 * 1024;

    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapPost("/ask", async (AskRequest request, GatewayCaller caller, ISender sender, CancellationToken cancellationToken) =>
                (await sender.Send(new AskQuestionCommand(caller.Identity, request.Question, request.TopK), cancellationToken)).ToHttp())
            .RequireAuthorization(GatewayPolicies.User)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(AskBodyLimit))
            .WithName("Ask")
            .WithSummary("Permission-aware question answering over the tenant's knowledge base.");

        api.MapGet("/usage", async (GatewayCaller caller, ISender sender, CancellationToken cancellationToken) =>
                (await sender.Send(new GetUsageQuery(caller.Identity), cancellationToken)).ToHttp())
            .RequireAuthorization(GatewayPolicies.AnyRole)
            .WithName("Usage")
            .WithSummary("Token budget of the caller's tenant and of the caller.");

        var documents = api.MapGroup("/documents").RequireAuthorization(GatewayPolicies.Admin).WithTags("Knowledge base");

        documents.MapGet("/", async (GatewayCaller caller, ISender sender, CancellationToken cancellationToken, int page = 1, int pageSize = 50) =>
                (await sender.Send(new ListDocumentsQuery(caller.Identity, page, pageSize), cancellationToken)).ToHttp())
            .WithName("ListDocuments");

        documents.MapPut("/{externalId}", async (
                string externalId, UpsertDocumentRequest request, GatewayCaller caller, ISender sender, CancellationToken cancellationToken) =>
                (await sender.Send(
                    new UpsertDocumentCommand(
                        caller.Identity, externalId, request.Title, request.Content, request.Classification,
                        request.Principals ?? [], request.SourceUri),
                    cancellationToken)).ToHttp())
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(DocumentBodyLimit))
            .WithName("UpsertDocument")
            .WithSummary("Creates or revises a document and its access control list.");

        documents.MapDelete("/{externalId}", async (string externalId, GatewayCaller caller, ISender sender, CancellationToken cancellationToken) =>
                (await sender.Send(new DeleteDocumentCommand(caller.Identity, externalId), cancellationToken)).ToHttp())
            .WithName("DeleteDocument");

        var audit = api.MapGroup("/audit").RequireAuthorization(GatewayPolicies.Admin).WithTags("Audit");

        audit.MapGet("/", async (
                GatewayCaller caller, ISender sender, CancellationToken cancellationToken,
                DateTime? from = null, DateTime? to = null, string? subject = null, AuditOutcome? outcome = null,
                int page = 1, int pageSize = 50) =>
                (await sender.Send(new ListAuditEntriesQuery(caller.Identity, from, to, subject, outcome, page, pageSize), cancellationToken)).ToHttp())
            .WithName("ListAudit");

        audit.MapGet("/verify", async (GatewayCaller caller, ISender sender, CancellationToken cancellationToken) =>
                (await sender.Send(new VerifyAuditChainQuery(caller.Identity), cancellationToken)).ToHttp())
            .WithName("VerifyAudit")
            .WithSummary("Recomputes the tenant's hash chain and reports the first break, if any.");

        return app;
    }
}
