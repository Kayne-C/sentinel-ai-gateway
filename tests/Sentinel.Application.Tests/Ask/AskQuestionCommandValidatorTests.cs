using Microsoft.Extensions.Options;
using Sentinel.Application.Common;
using Sentinel.Application.Features.Ask;
using Sentinel.Application.Tests.Fakes;

namespace Sentinel.Application.Tests.Ask;

public sealed class AskQuestionCommandValidatorTests
{
    private readonly AskQuestionCommandValidator _validator = new(Options.Create(new RagOptions { MaxQuestionLength = 10 }));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("01234567890")]
    public void Questions_must_be_present_and_within_the_configured_length(string question)
    {
        var result = _validator.Validate(new AskQuestionCommand(AskHarness.Caller, question));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AskQuestionCommand.Question));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public void Top_k_must_be_between_one_and_twenty(int topK)
    {
        var result = _validator.Validate(new AskQuestionCommand(AskHarness.Caller, "Leave?", topK));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(AskQuestionCommand.TopK));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(20)]
    public void A_short_question_with_a_valid_or_default_top_k_is_accepted(int? topK)
    {
        Assert.True(_validator.Validate(new AskQuestionCommand(AskHarness.Caller, "Leave?", topK)).IsValid);
    }
}
