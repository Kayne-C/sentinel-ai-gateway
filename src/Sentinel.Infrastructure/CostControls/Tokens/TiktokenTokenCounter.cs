using Microsoft.ML.Tokenizers;
using Sentinel.Application.Abstractions;

namespace Sentinel.Infrastructure.CostControls.Tokens;

/// <summary>
/// Counts tokens with OpenAI's <c>o200k_base</c> BPE (GPT-4o, GPT-4.1, o-series). For every other model family
/// (Qwen on Ollama, Llama, Mistral...) the count is an <b>estimate</b>: their vocabularies differ, so the real number
/// can be noticeably higher or lower, especially for Turkish text. That is acceptable because counts are only used
/// for routing and for budget <i>reservations</i>; budgets are settled with the usage the provider reports.
/// </summary>
/// <remarks>
/// The vocabulary ships in-process (Microsoft.ML.Tokenizers.Data.O200kBase): no download at runtime and nothing
/// leaves the process. Loading it costs a few hundred milliseconds and some memory, hence one instance (singleton);
/// the tokenizer is thread-safe.
/// </remarks>
internal sealed class TiktokenTokenCounter : ITokenCounter
{
    internal const string EncodingName = "o200k_base";

    private readonly TiktokenTokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding(EncodingName);

    public int CountTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 0 ? 0 : _tokenizer.CountTokens(text);
    }
}
