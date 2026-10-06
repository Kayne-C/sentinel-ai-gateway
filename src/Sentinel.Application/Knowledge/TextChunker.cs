using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Sentinel.Application.Abstractions;
using Sentinel.Domain.Common;
using Sentinel.Domain.Knowledge;

namespace Sentinel.Application.Knowledge;

/// <summary>
/// Cuts a document into overlapping, token-bounded chunks: paragraphs first, then sentences, then (only for a
/// sentence that alone exceeds the target) words. Chunks are packed greedily and never split inside a word, so a
/// chunk never starts or ends with half a token of meaning; the overlap repeats whole trailing sentences of the
/// previous chunk so an answer spanning a boundary is still retrievable from one chunk.
/// </summary>
/// <remarks>
/// Sentence boundaries are Turkish-aware: <c>. ! ? …</c> followed by whitespace and an upper-case letter (including
/// İ, Ş, Ç, Ğ, Ö, Ü), except after common abbreviations (<c>Dr.</c>, <c>Prof.</c>, <c>vb.</c>, <c>örn.</c>,
/// <c>A.Ş.</c>, <c>e.g.</c>...), initials and ordinal numbers (<c>2. Dünya Savaşı</c>). The output depends only on
/// the input text and the options, which keeps re-ingestion reproducible.
/// </remarks>
public sealed partial class TextChunker
{
    private const int RegexTimeoutMilliseconds = 2000;

    /// <summary>Compared ordinally (case matters) so that a sentence ending in lower-case "no." still splits.</summary>
    private static readonly FrozenSet<string> Abbreviations = new[]
    {
        // Turkish
        "Dr", "Prof", "Doç", "Yrd", "Öğr", "Gör", "Av", "Sn", "Bkz", "bkz", "Bknz", "bknz", "vb", "Vb", "vd", "vs", "Vs",
        "örn", "Örn", "No", "Tel", "Faks", "Ltd", "Şti", "şti", "Cad", "Sok", "Mah", "Apt", "Blv", "Md", "Müd", "Org",
        "Alb", "Yzb", "Hz", "Üniv", "Fak",

        // English
        "Mr", "Mrs", "Ms", "St", "Jr", "Sr", "Inc", "Corp", "Co", "Fig", "fig", "Vol", "vol", "Dept", "Ave", "approx",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly ITokenCounter _tokenCounter;
    private readonly int _targetTokens;
    private readonly int _overlapTokens;
    private readonly int _maxChunks;

    public TextChunker(ITokenCounter tokenCounter, IOptions<ChunkingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(tokenCounter);
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;
        if (!ChunkingOptions.IsValid(value))
        {
            throw new ArgumentException(
                "Chunking options are invalid: TargetTokens > 0, 0 <= OverlapTokens < TargetTokens and MaxChunks > 0 are required.",
                nameof(options));
        }

        _tokenCounter = tokenCounter;
        _targetTokens = value.TargetTokens;
        _overlapTokens = value.OverlapTokens;
        _maxChunks = value.MaxChunks;
    }

    public Result<IReadOnlyList<DocumentChunk>> Chunk(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var chunks = new List<DocumentChunk>();
        var window = new List<Unit>();
        var windowTokens = 0;

        foreach (var unit in SplitUnits(content))
        {
            if (window.Count > 0 && windowTokens + unit.Tokens > _targetTokens)
            {
                if (chunks.Count == _maxChunks)
                {
                    return KnowledgeIngestionErrors.TooManyChunks(_maxChunks);
                }

                chunks.Add(BuildChunk(window, chunks.Count));

                // Keep whole trailing units of the emitted chunk as overlap, then make room for the next unit:
                // every chunk contains at least one unit the previous chunk did not, so packing always progresses.
                window.RemoveRange(0, OverlapStart(window));
                windowTokens = window.Sum(u => u.Tokens);
                while (window.Count > 0 && windowTokens + unit.Tokens > _targetTokens)
                {
                    windowTokens -= window[0].Tokens;
                    window.RemoveAt(0);
                }
            }

            window.Add(unit);
            windowTokens += unit.Tokens;
        }

        if (window.Count > 0)
        {
            if (chunks.Count == _maxChunks)
            {
                return KnowledgeIngestionErrors.TooManyChunks(_maxChunks);
            }

            chunks.Add(BuildChunk(window, chunks.Count));
        }

        return chunks;
    }

    /// <summary>Paragraphs of the text (separated by blank lines), trimmed, empty ones dropped.</summary>
    internal static IReadOnlyList<string> SplitParagraphs(string content)
    {
        var normalized = content.ReplaceLineEndings("\n");
        string[] parts;
        try
        {
            parts = ParagraphBreak().Split(normalized);
        }
        catch (RegexMatchTimeoutException)
        {
            parts = [normalized];
        }

        return [.. parts.Select(p => p.Trim()).Where(p => p.Length > 0)];
    }

    /// <summary>Sentences of one paragraph; the text between them (whitespace) is not part of any sentence.</summary>
    internal static IReadOnlyList<Segment> SplitSentences(string paragraph)
    {
        var sentences = new List<Segment>();
        var start = 0;
        var newLineBefore = false;
        try
        {
            foreach (Match match in SentenceBoundary().Matches(paragraph))
            {
                if (FollowsAbbreviation(paragraph, start, match))
                {
                    continue;
                }

                var whitespace = match.Groups["ws"];
                sentences.Add(new Segment(paragraph[start..whitespace.Index], newLineBefore));
                newLineBefore = whitespace.ValueSpan.Contains('\n');
                start = whitespace.Index + whitespace.Length;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input: fall back to word-level packing rather than failing the ingestion.
            return [new Segment(paragraph, false)];
        }

        if (start < paragraph.Length)
        {
            sentences.Add(new Segment(paragraph[start..], newLineBefore));
        }

        return sentences;
    }

    private IEnumerable<Unit> SplitUnits(string content)
    {
        var paragraphs = SplitParagraphs(content);
        for (var paragraph = 0; paragraph < paragraphs.Count; paragraph++)
        {
            foreach (var sentence in SplitSentences(paragraphs[paragraph]))
            {
                var tokens = _tokenCounter.CountTokens(sentence.Text);
                if (tokens <= _targetTokens)
                {
                    yield return new Unit(sentence.Text, tokens, paragraph, sentence.NewLineBefore);
                    continue;
                }

                foreach (var word in SplitWords(sentence))
                {
                    yield return new Unit(word.Text, _tokenCounter.CountTokens(word.Text), paragraph, word.NewLineBefore);
                }
            }
        }
    }

    private static IEnumerable<Segment> SplitWords(Segment sentence)
    {
        var text = sentence.Text;
        var first = true;
        var i = 0;
        while (i < text.Length)
        {
            var gapStart = i;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            var newLine = text.AsSpan(gapStart, i - gapStart).Contains('\n');
            var wordStart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i > wordStart)
            {
                yield return new Segment(text[wordStart..i], first ? sentence.NewLineBefore : newLine);
                first = false;
            }
        }
    }

    private static bool FollowsAbbreviation(string paragraph, int sentenceStart, Match match)
    {
        // Only a single period can belong to an abbreviation; "!", "?", "..." and a closing quote end the sentence.
        var terminator = match.Groups["end"];
        if (terminator.Length != 1 || paragraph[terminator.Index] != '.' || match.Groups["close"].Length > 0)
        {
            return false;
        }

        var wordStart = terminator.Index;
        while (wordStart > sentenceStart && !char.IsWhiteSpace(paragraph[wordStart - 1]))
        {
            wordStart--;
        }

        var word = paragraph.AsSpan(wordStart, terminator.Index - wordStart).TrimStart("(['\"‘“«");
        if (word.IsEmpty)
        {
            return false;
        }

        // Initials ("M. Kemal") and Turkish ordinals ("2. Dünya Savaşı", "19. yüzyıl").
        if ((word.Length == 1 && char.IsLetter(word[0])) || IsDigits(word))
        {
            return true;
        }

        // Dotted single letters: "A.Ş", "T.C", "e.g", "i.e".
        if (IsDottedInitials(word))
        {
            return true;
        }

        var candidate = word.ToString();
        if (Abbreviations.Contains(candidate))
        {
            return true;
        }

        // Written together: "Ltd.Şti".
        var lastDot = word.LastIndexOf('.');
        return lastDot >= 0 && lastDot < word.Length - 1 && Abbreviations.Contains(candidate[(lastDot + 1)..]);
    }

    private static bool IsDigits(ReadOnlySpan<char> word)
    {
        foreach (var c in word)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDottedInitials(ReadOnlySpan<char> word)
    {
        if (word.Length < 3 || word.Length % 2 == 0)
        {
            return false;
        }

        for (var i = 0; i < word.Length; i++)
        {
            var expectLetter = i % 2 == 0;
            if (expectLetter ? !char.IsLetter(word[i]) : word[i] != '.')
            {
                return false;
            }
        }

        return true;
    }

    private int OverlapStart(List<Unit> window)
    {
        var start = window.Count;
        var tokens = 0;
        while (start > 0 && tokens + window[start - 1].Tokens <= _overlapTokens)
        {
            start--;
            tokens += window[start].Tokens;
        }

        return start;
    }

    private DocumentChunk BuildChunk(List<Unit> window, int ordinal)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < window.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(window[i].Paragraph != window[i - 1].Paragraph ? "\n\n" : window[i].NewLineBefore ? "\n" : " ");
            }

            builder.Append(window[i].Text);
        }

        var text = builder.ToString();
        return new DocumentChunk(ordinal, text, _tokenCounter.CountTokens(text), Quarantined: false, QuarantineReason: null);
    }

    /// <summary>A blank line (only horizontal whitespace between two line breaks) and any whitespace after it.</summary>
    [GeneratedRegex(@"\n[^\S\n]*\n\s*", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex ParagraphBreak();

    /// <summary>
    /// Terminator run, optional closing quotes/brackets, whitespace, then (looking ahead) an optional opening
    /// quote/bracket and an upper-case letter. The look-behind anchors matches at the start of a terminator run and
    /// the atomic groups prevent backtracking, which keeps matching linear on hostile input.
    /// </summary>
    [GeneratedRegex(
        @"(?<![.!?…])(?<end>(?>[.!?…]+))(?<close>(?>[""'’”»)\]]*))(?<ws>(?>\s+))(?=[""'‘“«(\[]?\p{Lu})",
        RegexOptions.CultureInvariant,
        RegexTimeoutMilliseconds)]
    private static partial Regex SentenceBoundary();

    internal readonly record struct Segment(string Text, bool NewLineBefore);

    private readonly record struct Unit(string Text, int Tokens, int Paragraph, bool NewLineBefore);
}
