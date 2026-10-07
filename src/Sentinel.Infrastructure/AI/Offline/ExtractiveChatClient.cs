using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Sentinel.Infrastructure.AI.Offline;

/// <summary>
/// A deterministic, grounded stand-in for a language model (development, tests, demos without a GPU). It reads the
/// <c>&lt;document source="..."&gt;</c> blocks of the prompt, scores their sentences by word overlap with the question
/// and answers with the best ones, quoted verbatim (so placeholders such as <c>[EMAIL_1]</c> survive untouched) and
/// cited by source. It never invents anything: no documents, or no overlapping sentence, gives an explicit "not found"
/// answer in the question's language. It also ignores instructions in the documents for the simplest of reasons: it
/// does not follow instructions at all.
/// </summary>
internal sealed partial class ExtractiveChatClient(string modelId, TimeProvider timeProvider) : IChatClient
{
    public const string ProviderName = "sentinel-offline";

    internal const string NoDocumentsTurkish = "Bu soruyu yanıtlayabilecek yetkili bir belge bulunamadı.";
    internal const string NoDocumentsEnglish = "No authorised document that could answer this question was found.";
    internal const string NotInDocumentsTurkish = "Sağlanan belgelerde bu soruyu yanıtlayan bir bilgi bulunamadı.";
    internal const string NotInDocumentsEnglish = "The provided documents do not contain information that answers this question.";
    internal const string AnswerPrefixTurkish = "Belgelere göre: ";
    internal const string AnswerPrefixEnglish = "According to the documents: ";

    private const int MaxAnswerSentences = 3;
    private const int MaxSentencesScanned = 2000;
    private const int MinPrefixMatchLength = 4;

    private readonly ChatClientMetadata _metadata = new(ProviderName, null, modelId);

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = Complete([.. messages], options);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, completion.Text) { MessageId = completion.ResponseId })
        {
            ResponseId = completion.ResponseId,
            ModelId = completion.ModelId,
            CreatedAt = timeProvider.GetUtcNow(),
            FinishReason = completion.FinishReason,
            Usage = completion.Usage,
        };

        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        await Task.Yield();

        var completion = Complete([.. messages], options);
        var createdAt = timeProvider.GetUtcNow();
        foreach (Match piece in WordPiece().Matches(completion.Text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, piece.Value)
            {
                ResponseId = completion.ResponseId,
                MessageId = completion.ResponseId,
                ModelId = completion.ModelId,
                CreatedAt = createdAt,
            };
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(completion.Usage)])
        {
            ResponseId = completion.ResponseId,
            MessageId = completion.ResponseId,
            ModelId = completion.ModelId,
            CreatedAt = createdAt,
            FinishReason = completion.FinishReason,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(ChatClientMetadata) ? _metadata : serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    private Completion Complete(List<ChatMessage> messages, ChatOptions? options)
    {
        var documents = new List<SourceDocument>();
        foreach (var message in messages)
        {
            documents.AddRange(ParseDocuments(message.Text));
        }

        var question = ExtractQuestion(messages);
        var text = Answer(question, documents);
        var finishReason = ChatFinishReason.Stop;

        if (options?.MaxOutputTokens is > 0 and var maxOutputTokens)
        {
            var words = WordPiece().Matches(text);
            if (words.Count > maxOutputTokens)
            {
                text = string.Concat(words.Take(maxOutputTokens).Select(m => m.Value)).TrimEnd();
                finishReason = ChatFinishReason.Length;
            }
        }

        var inputTokens = messages.Sum(m => CountWords(m.Text));
        var outputTokens = CountWords(text);
        var model = string.IsNullOrWhiteSpace(options?.ModelId) ? modelId : options.ModelId;
        return new Completion(
            text,
            model,
            ResponseId(model, messages, text),
            finishReason,
            new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens, TotalTokenCount = inputTokens + outputTokens });
    }

    private static string Answer(string question, List<SourceDocument> documents)
    {
        var turkish = OfflineText.LooksTurkish(question);
        if (documents.Count == 0)
        {
            return turkish ? NoDocumentsTurkish : NoDocumentsEnglish;
        }

        var terms = ContentTerms(question);
        var candidates = new List<(int Score, int Document, int Position, string Sentence)>();
        var scanned = 0;
        for (var d = 0; d < documents.Count && scanned < MaxSentencesScanned; d++)
        {
            var position = 0;
            foreach (var sentence in Sentences(documents[d].Body))
            {
                if (++scanned > MaxSentencesScanned)
                {
                    break;
                }

                var score = Overlap(terms, ContentTerms(sentence));
                if (score > 0)
                {
                    candidates.Add((score, d, position, sentence));
                }

                position++;
            }
        }

        if (candidates.Count == 0)
        {
            return turkish ? NotInDocumentsTurkish : NotInDocumentsEnglish;
        }

        // Keep only sentences at least half as relevant as the best one, best first, ties in document order.
        var best = candidates.Max(c => c.Score);
        var chosen = candidates
            .Where(c => c.Score * 2 >= best)
            .OrderByDescending(c => c.Score).ThenBy(c => c.Document).ThenBy(c => c.Position)
            .Take(MaxAnswerSentences)
            .ToList();

        var sources = string.Join(", ", chosen.Select(c => documents[c.Document].Source).Distinct(StringComparer.Ordinal));
        var body = string.Join(" ", chosen.Select(c => c.Sentence));
        return turkish
            ? $"{AnswerPrefixTurkish}{body} (Kaynak: {sources})"
            : $"{AnswerPrefixEnglish}{body} (Source: {sources})";
    }

    private static IEnumerable<SourceDocument> ParseDocuments(string text)
    {
        foreach (Match match in DocumentBlock().Matches(text))
        {
            var source = SourceAttribute().Match(match.Groups["attributes"].Value);
            var label = source.Success ? WebUtility.HtmlDecode(source.Groups["value"].Value) : "document";
            yield return new SourceDocument(label, match.Groups["body"].Value);
        }
    }

    /// <summary>The last user turn: its &lt;question&gt; element if present, else its text without document blocks.</summary>
    private static string ExtractQuestion(List<ChatMessage> messages)
    {
        var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var tagged = QuestionBlock().Match(lastUser);
        if (tagged.Success)
        {
            return tagged.Groups["question"].Value.Trim();
        }

        return DocumentsWrapper().Replace(DocumentBlock().Replace(lastUser, " "), " ").Trim();
    }

    private static IEnumerable<string> Sentences(string body)
    {
        foreach (var raw in SentenceBoundary().Split(body))
        {
            var sentence = Whitespace().Replace(raw, " ").Trim();
            if (sentence.Length > 0)
            {
                yield return sentence;
            }
        }
    }

    private static HashSet<string> ContentTerms(string text)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in OfflineText.Words(OfflineText.Fold(OfflineText.StripPlaceholders(text))))
        {
            if (word.Length >= 2 && !OfflineText.IsStopWord(word))
            {
                terms.Add(word);
            }
        }

        return terms;
    }

    /// <summary>Question terms found in the sentence; a shared prefix of 4+ letters counts (izin ~ izinler).</summary>
    private static int Overlap(HashSet<string> questionTerms, HashSet<string> sentenceTerms)
    {
        var score = 0;
        foreach (var term in questionTerms)
        {
            if (sentenceTerms.Contains(term) || sentenceTerms.Any(other => SharesStem(term, other)))
            {
                score++;
            }
        }

        return score;
    }

    private static bool SharesStem(string a, string b)
    {
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= MinPrefixMatchLength && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    private static int CountWords(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string ResponseId(string model, List<ChatMessage> messages, string answer)
    {
        var builder = new StringBuilder(model);
        foreach (var message in messages)
        {
            builder.Append('\n').Append(message.Role.Value).Append('\n').Append(message.Text);
        }

        builder.Append('\n').Append(answer);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return "extractive-" + Convert.ToHexStringLower(hash.AsSpan(0, 12));
    }

    [GeneratedRegex(@"<document\b(?<attributes>[^>]*)>(?<body>.*?)</document\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DocumentBlock();

    [GeneratedRegex(@"\bsource\s*=\s*""(?<value>[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SourceAttribute();

    [GeneratedRegex(@"<question>(?<question>.*?)</question>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex QuestionBlock();

    [GeneratedRegex(@"</?documents\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DocumentsWrapper();

    /// <summary>After sentence punctuation, and at every line break (knowledge bases are full of one-fact lines).</summary>
    [GeneratedRegex(@"(?<=[.!?…])\s+|\r?\n", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SentenceBoundary();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();

    /// <summary>A word with its trailing whitespace: concatenating the pieces reproduces the text exactly.</summary>
    [GeneratedRegex(@"\s*\S+\s*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WordPiece();

    private sealed record SourceDocument(string Source, string Body);

    private sealed record Completion(string Text, string ModelId, string ResponseId, ChatFinishReason FinishReason, UsageDetails Usage);
}
