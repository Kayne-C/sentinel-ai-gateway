using FluentValidation;
using Microsoft.Extensions.Options;
using Sentinel.Application.Common;

namespace Sentinel.Application.Features.Ask;

internal sealed class AskQuestionCommandValidator : AbstractValidator<AskQuestionCommand>
{
    /// <summary>Upper bound for retrieved chunks per question; more only dilutes the context and the budget.</summary>
    public const int MaxTopK = 20;

    public AskQuestionCommandValidator(IOptions<RagOptions> ragOptions)
    {
        ArgumentNullException.ThrowIfNull(ragOptions);
        var maxQuestionLength = ragOptions.Value.MaxQuestionLength;

        RuleFor(c => c.Caller).NotNull();

        RuleFor(c => c.Question)
            .NotEmpty().WithMessage("A question is required.")
            .MaximumLength(maxQuestionLength).WithMessage($"The question must not exceed {maxQuestionLength} characters.");

        RuleFor(c => c.TopK)
            .InclusiveBetween(1, MaxTopK).WithMessage($"TopK must be between 1 and {MaxTopK}.")
            .When(c => c.TopK.HasValue);
    }
}
