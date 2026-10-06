using FluentValidation;
using Sentinel.Application.Knowledge;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Knowledge;

namespace Sentinel.Application.Features.Documents;

internal sealed class UpsertDocumentCommandValidator : AbstractValidator<UpsertDocumentCommand>
{
    public UpsertDocumentCommandValidator()
    {
        RuleFor(c => c.Caller).NotNull();

        RuleFor(c => c.ExternalId)
            .NotEmpty()
            .MaximumLength(KnowledgeDocument.MaxExternalIdLength)
            .Must(DocumentValidation.HasNoControlCharacters).WithMessage("External id must not contain control characters.");

        // The title is embedded with every chunk ("{title}\n\n{chunk}") and shown to the model; control characters
        // would let a title fake chunk structure.
        RuleFor(c => c.Title)
            .NotEmpty()
            .MaximumLength(KnowledgeDocument.MaxTitleLength)
            .Must(DocumentValidation.HasNoControlCharacters).WithMessage("Title must not contain control characters.");

        RuleFor(c => c.Content)
            .NotEmpty()
            .MaximumLength(KnowledgeDocument.MaxContentLength);

        RuleFor(c => c.Classification).IsInEnum();

        RuleFor(c => c.Principals)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .Must(p => p is { Count: > 0 }).WithMessage("At least one principal (everyone, user:{id} or group:{id}) is required.")
            .Must(p => p is null || p.Count <= KnowledgeDocument.MaxPrincipals)
            .WithMessage($"A document can have at most {KnowledgeDocument.MaxPrincipals} principals.");

        RuleForEach(c => c.Principals)
            .Must(p => AccessPrincipal.TryParse(p, out _))
            .WithMessage("'{PropertyValue}' is not a valid principal (everyone, user:{id} or group:{id}).");

        RuleFor(c => c.SourceUri)
            .MaximumLength(KnowledgeLimits.SourceUriMaxLength)
            .Must(DocumentValidation.IsSafeSourceUri).When(c => c.SourceUri is not null)
            .WithMessage("Source URI must be an absolute http(s) URI.");
    }
}

internal sealed class DeleteDocumentCommandValidator : AbstractValidator<DeleteDocumentCommand>
{
    public DeleteDocumentCommandValidator()
    {
        RuleFor(c => c.Caller).NotNull();
        RuleFor(c => c.ExternalId).NotEmpty().MaximumLength(KnowledgeDocument.MaxExternalIdLength);
    }
}

internal sealed class ListDocumentsQueryValidator : AbstractValidator<ListDocumentsQuery>
{
    public const int MaxPageSize = 200;
    public const int MaxPage = 100_000;

    public ListDocumentsQueryValidator()
    {
        RuleFor(q => q.Caller).NotNull();
        RuleFor(q => q.Page).InclusiveBetween(1, MaxPage);
        RuleFor(q => q.PageSize).InclusiveBetween(1, MaxPageSize);
    }
}

internal static class DocumentValidation
{
    public static bool HasNoControlCharacters(string? value) => value is null || !value.Any(char.IsControl);

    /// <summary>
    /// Source links are rendered by clients next to citations; only http(s) is allowed so a document can never carry a
    /// <c>javascript:</c> or <c>data:</c> link into a UI.
    /// </summary>
    public static bool IsSafeSourceUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
