using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sentinel.Infrastructure.CostControls.Routing;

/// <summary>A complexity score and the features that produced it. <see cref="Points"/> are hundredths (0–100).</summary>
internal sealed record ComplexityAssessment(int Points, IReadOnlyList<string> Reasons)
{
    public double Score => Points / 100d;
}

/// <summary>
/// Deterministic, explainable prompt-complexity heuristic for model routing.
/// </summary>
/// <remarks>
/// Deliberately not a model call: routing runs on every request, must be cheap, must not send the prompt anywhere and
/// must give every gateway instance the same answer for the same input. Scores are integer points, so threshold
/// comparisons cannot drift with floating-point rounding. Reasons name features and canonical keyword ids only, never
/// prompt text, so they are safe to log and to audit.
/// Turkish is matched after folding (İ/I/ı → i, ş → s, ğ → g, ç → c, ö → o, ü → u), which also catches prompts typed
/// on keyboards without Turkish letters ("karsilastir", "adim adim") and upper-case text, which culture-invariant
/// lower-casing alone gets wrong for İ and I.
/// </remarks>
internal static partial class PromptComplexity
{
    /// <summary>Signals live in the instructions rather than in pasted bulk text; bounding the scan bounds the cost.</summary>
    internal const int MaxAnalyzedCharacters = 32_000;

    internal const int LongPromptPoints = 30;
    internal const int ContextHeavyPoints = 20;
    internal const int CodeOrMathPoints = 20;
    internal const int LongOutputPoints = 15;
    internal const int TwoSubQuestionsPoints = 15;
    internal const int ManySubQuestionsPoints = 20;

    private const int RegexTimeoutMilliseconds = 250;

    /// <summary>Points for the first, second and third distinct reasoning concept; further concepts add nothing.</summary>
    private static readonly int[] KeywordPoints = [35, 15, 10];

    /// <summary>Regex group → canonical concept id (English and Turkish spellings map to the same concept).</summary>
    private static readonly (string Group, string Label)[] Concepts =
    [
        ("why", "why"),
        ("compare", "compare"),
        ("analyze", "analyze"),
        ("evaluate", "evaluate"),
        ("stepbystep", "step-by-step"),
        ("tradeoff", "trade-off"),
        ("proscons", "pros-and-cons"),
        ("strategy", "strategy"),
        ("design", "design"),
        ("plan", "plan"),
    ];

    private const string ReasoningKeywordPattern =
        @"\b(?:" +
        @"(?<why>why|neden\w*|nicin)" +
        @"|(?<compare>compar(?:e|es|ed|ing|ison|isons)|karsilastir\w*)" +
        @"|(?<analyze>analy[sz](?:e|es|ed|ing|is)|analiz\s+(?:et|ed|eder|yap)\w*)" +
        @"|(?<evaluate>evaluat(?:e|es|ed|ing|ion|ions)|degerlendir\w*)" +
        @"|(?<stepbystep>step[\s-]+by[\s-]+step|adim\s+adim)" +
        @"|(?<tradeoff>trade[\s-]?offs?)" +
        @"|(?<proscons>pros\s+(?:and|&)\s+cons|(?:dis)?advantages?|(?:dez)?avantaj\w*)" +
        @"|(?<strategy>strateg(?:y|ies|ic|ically)|strateji\w*)" +
        @"|(?<design>design(?:s|ed|ing)?|tasarla\w*|tasarim\w*)" +
        @"|(?<plan>plan(?:s|ned|ning|la\w*|lar\w*|i\w*)?)" +
        @")\b";

    private const string MathPattern =
        @"\$(?=\S)[^$\r\n]{1,200}(?<=\S)\$" +
        @"|\\(?:frac|sqrt|sum|int|lim|prod|cdot|times|leq|geq|infty|partial)\b" +
        @"|[∑∫√∂∞≤≥≠±×÷∆∏]" +
        @"|\b\d+(?:[.,]\d+)?\s*\^\s*\d+" +
        @"|\b[a-z]\s*\^\s*\d" +
        @"|\b\d+(?:[.,]\d+)?\s*[-+*/×÷]\s*\d+(?:[.,]\d+)?\s*=" +
        @"|\b(?:integrals?|derivatives?|equations?|theorem|proof|prove|turev\w*|denklem\w*|ispat\w*|teorem\w*)\b";

    // Identifier runs are anchored (\b) and bounded: an unanchored \w+ would retry at every position of a long word
    // and turn a 32k-character prompt into quadratic work (each request burning the full regex timeout).
    private const string CodeSyntaxPattern =
        @"\b(?:def|function|func|fn)\s+\w{1,64}\s*\(" +
        @"|#include\s*<" +
        @"|\b\w{1,64}\s*\([^()\r\n]{0,200}\)\s*\{" +
        @"|==|!=|&&|\|\||=>";

    private const string LongOutputPattern =
        @"\b(?:in\s+detail|detailed|comprehensive(?:ly)?|thorough(?:ly)?|in[\s-]depth|elaborate|essay" +
        @"|long\s+(?:answer|response|explanation|report)|at\s+least\s+\d+\s+(?:words|paragraphs|pages|sentences)" +
        @"|\d{3,}\s+words|full\s+report" +
        @"|ayrintili|detayli|kapsamli|derinlemesine|uzun\s+(?:bir\s+)?(?:cevap|yanit|aciklama|metin|rapor)" +
        @"|en\s+az\s+\d+\s+(?:kelime|paragraf|sayfa|cumle)\w*|\d{3,}\s+kelime\w*|makale\w*)\b";

    public static ComplexityAssessment Assess(string prompt, int promptTokens, int contextTokens, RoutingOptions options)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);

        var reasons = new List<string>();
        var points = 0;

        points += SizeFeature(Math.Max(0, promptTokens), options.LongPromptTokens, LongPromptPoints, "long prompt", "moderately long prompt", reasons);
        points += SizeFeature(Math.Max(0, contextTokens), options.ContextHeavyTokens, ContextHeavyPoints, "heavy context", "moderate context", reasons);

        var text = Fold(prompt.AsSpan(0, Math.Min(prompt.Length, MaxAnalyzedCharacters)));

        var concepts = MatchConcepts(text, reasons);
        if (concepts.Count > 0)
        {
            var keywordPoints = KeywordPoints.Take(concepts.Count).Sum();
            points += keywordPoints;
            reasons.Add($"reasoning keywords: {string.Join(", ", concepts)} (+{Format(keywordPoints)})");
        }

        var questions = CountQuestions(text);
        if (questions >= 3)
        {
            points += ManySubQuestionsPoints;
            reasons.Add($"{questions} sub-questions (+{Format(ManySubQuestionsPoints)})");
        }
        else if (questions == 2)
        {
            points += TwoSubQuestionsPoints;
            reasons.Add($"2 sub-questions (+{Format(TwoSubQuestionsPoints)})");
        }

        var code = HasCode(text, reasons);
        var math = Matches(MathSyntax(), text, "math", reasons);
        if (code || math)
        {
            points += CodeOrMathPoints;
            var what = code && math ? "code and math" : code ? "code" : "math";
            reasons.Add($"{what} in prompt (+{Format(CodeOrMathPoints)})");
        }

        if (Matches(LongOutputRequest(), text, "long-output", reasons))
        {
            points += LongOutputPoints;
            reasons.Add($"explicit long-output request (+{Format(LongOutputPoints)})");
        }

        return new ComplexityAssessment(Math.Clamp(points, 0, 100), reasons);
    }

    /// <summary>
    /// Lower-cases and folds Turkish letters to ASCII. Mapped explicitly because invariant lower-casing leaves
    /// <c>İ</c> unchanged and maps <c>I</c> to <c>i</c> rather than <c>ı</c>.
    /// </summary>
    internal static string Fold(ReadOnlySpan<char> text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case 'İ' or 'I' or 'ı' or 'Î' or 'î':
                    builder.Append('i');
                    break;
                case 'Ş' or 'ş':
                    builder.Append('s');
                    break;
                case 'Ğ' or 'ğ':
                    builder.Append('g');
                    break;
                case 'Ç' or 'ç':
                    builder.Append('c');
                    break;
                case 'Ö' or 'ö':
                    builder.Append('o');
                    break;
                case 'Ü' or 'ü' or 'Û' or 'û':
                    builder.Append('u');
                    break;
                case 'Â' or 'â':
                    builder.Append('a');
                    break;
                case '\u0307':
                    // Combining dot above: what remains of a decomposed "İ".
                    break;
                default:
                    builder.Append(char.ToLowerInvariant(c));
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Question marks that end a question (followed by whitespace, a closing mark or the end), so "??" counts once.</summary>
    internal static int CountQuestions(ReadOnlySpan<char> text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('?' or '？' or '؟'))
            {
                continue;
            }

            if (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]) || text[i + 1] is '"' or '\'' or ')' or ']' or '»' or '”' or '*')
            {
                count++;
            }
        }

        return count;
    }

    private static int SizeFeature(int tokens, int threshold, int weight, string label, string halfLabel, List<string> reasons)
    {
        if (threshold <= 0)
        {
            return 0;
        }

        if (tokens >= threshold)
        {
            reasons.Add($"{label}: {tokens} tokens >= {threshold} (+{Format(weight)})");
            return weight;
        }

        if ((long)tokens * 2 >= threshold)
        {
            var half = weight / 2;
            reasons.Add($"{halfLabel}: {tokens} tokens >= {(threshold + 1) / 2} (+{Format(half)})");
            return half;
        }

        return 0;
    }

    private static List<string> MatchConcepts(string text, List<string> reasons)
    {
        var found = new bool[Concepts.Length];
        try
        {
            for (var match = ReasoningKeywords().Match(text); match.Success; match = match.NextMatch())
            {
                for (var i = 0; i < Concepts.Length; i++)
                {
                    if (match.Groups[Concepts[i].Group].Success)
                    {
                        found[i] = true;
                        break;
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            reasons.Add("keyword analysis timed out; keywords ignored");
        }

        var labels = new List<string>();
        for (var i = 0; i < Concepts.Length; i++)
        {
            if (found[i])
            {
                labels.Add(Concepts[i].Label);
            }
        }

        return labels;
    }

    private static bool HasCode(string text, List<string> reasons)
    {
        if (text.Contains("```", StringComparison.Ordinal) || text.Contains("~~~", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return CodeSyntax().IsMatch(text) || CodeLineEnding().Count(text) >= 2;
        }
        catch (RegexMatchTimeoutException)
        {
            reasons.Add("code analysis timed out; ignored");
            return false;
        }
    }

    private static bool Matches(Regex regex, string text, string feature, List<string> reasons)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            reasons.Add($"{feature} analysis timed out; ignored");
            return false;
        }
    }

    private static string Format(int points) => (points / 100d).ToString("0.00", CultureInfo.InvariantCulture);

    [GeneratedRegex(ReasoningKeywordPattern, RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex ReasoningKeywords();

    [GeneratedRegex(MathPattern, RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex MathSyntax();

    [GeneratedRegex(CodeSyntaxPattern, RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex CodeSyntax();

    /// <summary>Lines ending in <c>;</c>, <c>{</c> or <c>}</c>: two of them look like source code, not prose.</summary>
    [GeneratedRegex(@"[;{}][ \t\r]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex CodeLineEnding();

    [GeneratedRegex(LongOutputPattern, RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex LongOutputRequest();
}
