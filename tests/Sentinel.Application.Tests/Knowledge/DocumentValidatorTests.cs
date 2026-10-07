using Sentinel.Application.Features.Documents;
using Sentinel.Domain.Identity;
using Sentinel.Domain.Knowledge;

namespace Sentinel.Application.Tests.Knowledge;

public sealed class DocumentValidatorTests
{
    private static readonly CallerIdentity Admin = new("tenant-a", "admin-oid", null, [], [SentinelRoles.Admin], CallerKind.User);

    private readonly UpsertDocumentCommandValidator _validator = new();

    [Fact]
    public void A_well_formed_command_is_valid()
    {
        Assert.True(_validator.Validate(Command()).IsValid);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("user:")]
    [InlineData("group:a b")]
    [InlineData("role:x")]
    [InlineData("")]
    public void Malformed_principals_are_rejected(string principal)
    {
        var result = _validator.Validate(Command(principals: ["everyone", principal]));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void At_least_one_principal_is_required()
    {
        Assert.False(_validator.Validate(Command(principals: [])).IsValid);
    }

    [Fact]
    public void More_principals_than_the_domain_allows_are_rejected()
    {
        var principals = Enumerable.Range(0, KnowledgeDocument.MaxPrincipals + 1).Select(i => $"group:g{i}").ToList();

        Assert.False(_validator.Validate(Command(principals: principals)).IsValid);
    }

    [Fact]
    public void Undefined_classifications_are_rejected()
    {
        Assert.False(_validator.Validate(Command() with { Classification = (Classification)42 }).IsValid);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("/relative/path")]
    [InlineData("file:///etc/passwd")]
    public void Source_uris_must_be_absolute_http_links(string sourceUri)
    {
        Assert.False(_validator.Validate(Command() with { SourceUri = sourceUri }).IsValid);
    }

    [Fact]
    public void A_missing_source_uri_is_allowed()
    {
        Assert.True(_validator.Validate(Command() with { SourceUri = null }).IsValid);
    }

    [Theory]
    [InlineData("Title\nwith a fake chunk boundary")]
    [InlineData("Bell\u0007")]
    public void Titles_with_control_characters_are_rejected(string title)
    {
        Assert.False(_validator.Validate(Command() with { Title = title }).IsValid);
    }

    [Fact]
    public void Overlong_fields_are_rejected()
    {
        Assert.False(_validator.Validate(Command() with { Title = new string('t', KnowledgeDocument.MaxTitleLength + 1) }).IsValid);
        Assert.False(_validator.Validate(Command() with { ExternalId = new string('e', KnowledgeDocument.MaxExternalIdLength + 1) }).IsValid);
        Assert.False(_validator.Validate(Command() with { Content = "   " }).IsValid);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 201)]
    public void List_paging_is_bounded(int page, int pageSize)
    {
        Assert.False(new ListDocumentsQueryValidator().Validate(new ListDocumentsQuery(Admin, page, pageSize)).IsValid);
    }

    private static UpsertDocumentCommand Command(IReadOnlyList<string>? principals = null) => new(
        Admin, "hr/leave", "Leave policy", "Yıllık izin her yıl yenilenir.", Classification.Internal,
        principals ?? ["everyone", "group:hr", "USER:Alice"], "https://intranet.example/hr/leave");
}
