using Sentinel.Application.Abstractions.Messaging;
using Sentinel.Application.Features.Documents;
using Sentinel.Domain.Identity;
using Sentinel.Gateway.Security;

namespace Sentinel.Gateway.Demo;

public sealed record DevTokenRequest(string Persona);

/// <summary>Development-only: demo tokens and demo data. Mapped only when <c>Auth:Mode</c> is Development.</summary>
internal static class DemoEndpoints
{
    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        var demo = app.MapGroup("/dev").AllowAnonymous().WithTags("Development");

        demo.MapGet("/personas", (DemoCorpus corpus) => corpus.Personas.Select(p => new
        {
            p.Name,
            p.DisplayName,
            p.TenantId,
            p.Roles,
            Groups = p.Groups.Count,
        }));

        demo.MapPost("/token", (DevTokenRequest request, DemoCorpus corpus, DevTokenIssuer issuer) =>
            corpus.FindPersona(request.Persona ?? string.Empty) is { } persona
                ? Results.Ok(new { access_token = issuer.Issue(persona), token_type = "Bearer", persona = persona.Name })
                : Results.Problem(statusCode: 404, title: "Unknown persona", extensions: new Dictionary<string, object?> { ["code"] = "Dev.UnknownPersona" }));

        return app;
    }
}

internal static class DemoSeeder
{
    /// <summary>Ingests the demo corpus as each tenant's administrator. Unchanged documents are not re-embedded.</summary>
    public static async Task SeedAsync(IServiceProvider services, DemoCorpus corpus, ILogger logger, CancellationToken cancellationToken)
    {
        foreach (var tenant in corpus.Tenants)
        {
            var admin = corpus.Personas.FirstOrDefault(p =>
                string.Equals(p.TenantId, tenant.TenantId, StringComparison.OrdinalIgnoreCase) && p.Roles.Contains(SentinelRoles.Admin));
            if (admin is null)
            {
                continue;
            }

            var caller = new CallerIdentity(
                admin.TenantId.ToLowerInvariant(), admin.ObjectId.ToLowerInvariant(), admin.DisplayName, admin.Groups, admin.Roles, CallerKind.User);

            foreach (var document in tenant.Documents)
            {
                await using var scope = services.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(
                    new UpsertDocumentCommand(
                        caller, document.ExternalId, document.Title, document.Content, document.Classification,
                        document.Principals, SourceUri: null),
                    cancellationToken);

                if (result.IsFailure)
                {
                    logger.LogWarning("Demo document {ExternalId} was not ingested: {Code}", document.ExternalId, result.Error.Code);
                }
            }
        }
    }
}
