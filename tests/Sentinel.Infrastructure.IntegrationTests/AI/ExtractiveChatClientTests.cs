using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Sentinel.Infrastructure.AI.Offline;

namespace Sentinel.Infrastructure.IntegrationTests.AI;

public sealed class ExtractiveChatClientTests
{
    private const string SystemRules = "Yalnızca <document> blokları içindeki belgelere dayanarak yanıt ver. Answer only from the <document> blocks.";

    private readonly ExtractiveChatClient _client = new("offline-fast", new FakeTimeProvider(DateTimeOffset.UnixEpoch));

    [Fact]
    public async Task Answers_in_turkish_from_the_most_relevant_sentences_and_cites_the_source()
    {
        var response = await _client.GetResponseAsync(Prompt(
            "Yıllık izin kaç gün?",
            ("hr/leave", "Tam zamanlı çalışanlar yılda 14 gün yıllık izin kullanabilir. Yemekhane saat 12'de açılır."),
            ("it/vpn", "VPN şifresi her 90 günde bir yenilenir.")));

        Assert.Equal("Belgelere göre: Tam zamanlı çalışanlar yılda 14 gün yıllık izin kullanabilir. (Kaynak: hr/leave)", response.Text);
        Assert.Equal("offline-fast", response.ModelId);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
    }

    [Fact]
    public async Task Answers_in_english_when_the_question_is_english()
    {
        var response = await _client.GetResponseAsync(Prompt(
            "How do I reset my VPN password?",
            ("it/vpn", "To reset your VPN password, open the self-service portal.\nThe portal is available around the clock."),
            ("hr/leave", "Employees get 14 days of annual leave.")));

        Assert.Equal("According to the documents: To reset your VPN password, open the self-service portal. (Source: it/vpn)", response.Text);
    }

    [Theory]
    [InlineData("Yıllık izin kaç gün?", ExtractiveChatClient.NoDocumentsTurkish)]
    [InlineData("izin politikasi nedir", ExtractiveChatClient.NoDocumentsTurkish)]
    [InlineData("How many days of leave do I get?", ExtractiveChatClient.NoDocumentsEnglish)]
    public async Task Without_documents_it_says_so_in_the_question_language(string question, string expected)
    {
        var response = await _client.GetResponseAsync(Prompt(question));

        Assert.Equal(expected, response.Text);
    }

    [Fact]
    public async Task Documents_that_do_not_answer_the_question_give_an_explicit_not_found()
    {
        var response = await _client.GetResponseAsync(Prompt("Yıllık izin kaç gün?", ("ops/backup", "Sunucular her pazar gecesi yedeklenir.")));

        Assert.Equal(ExtractiveChatClient.NotInDocumentsTurkish, response.Text);
    }

    [Fact]
    public async Task Placeholders_are_quoted_verbatim()
    {
        var response = await _client.GetResponseAsync(Prompt(
            "Who approves leave requests?",
            ("hr/contacts", "Leave requests are approved by [EMAIL_2] within two days.")));

        Assert.Equal("According to the documents: Leave requests are approved by [EMAIL_2] within two days. (Source: hr/contacts)", response.Text);
    }

    [Fact]
    public async Task Escaped_source_labels_are_decoded_for_the_citation()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "<documents>\n<document source=\"a&amp;b &quot;x&quot;\" title=\"t\">\nLeave is 14 days.\n</document>\n</documents>\n\n<question>\nHow long is leave?\n</question>"),
        };

        var response = await _client.GetResponseAsync(messages);

        Assert.EndsWith("(Source: a&b \"x\")", response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Usage_is_reported_with_a_whitespace_token_estimate()
    {
        var messages = Prompt("How long is leave?", ("hr/leave", "Leave is 14 days."));

        var response = await _client.GetResponseAsync(messages);

        var expectedInput = messages.Sum(m => m.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        var expectedOutput = response.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.NotNull(response.Usage);
        Assert.Equal(expectedInput, response.Usage.InputTokenCount);
        Assert.Equal(expectedOutput, response.Usage.OutputTokenCount);
        Assert.Equal(expectedInput + expectedOutput, response.Usage.TotalTokenCount);
    }

    [Fact]
    public async Task The_output_is_cut_at_max_output_tokens()
    {
        var response = await _client.GetResponseAsync(
            Prompt("How long is leave?", ("hr/leave", "Leave is 14 days.")),
            new ChatOptions { MaxOutputTokens = 3 });

        Assert.Equal("According to the", response.Text);
        Assert.Equal(ChatFinishReason.Length, response.FinishReason);
    }

    [Fact]
    public async Task Streaming_yields_word_sized_updates_that_add_up_to_the_answer_and_ends_with_usage()
    {
        var messages = Prompt("How long is leave?", ("hr/leave", "Leave is 14 days."));
        var expected = await _client.GetResponseAsync(messages);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in _client.GetStreamingResponseAsync(messages))
        {
            updates.Add(update);
        }

        var textUpdates = updates.Where(u => !string.IsNullOrEmpty(u.Text)).ToList();
        Assert.True(textUpdates.Count > 3);
        Assert.All(textUpdates, u => Assert.Single(u.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        Assert.Equal(expected.Text, string.Concat(textUpdates.Select(u => u.Text)));

        var last = updates[^1];
        var usage = Assert.IsType<UsageContent>(Assert.Single(last.Contents));
        Assert.Equal(expected.Usage!.TotalTokenCount, usage.Details.TotalTokenCount);
        Assert.Equal(ChatFinishReason.Stop, last.FinishReason);

        var aggregated = updates.ToChatResponse();
        Assert.Equal(expected.Text, aggregated.Text);
        Assert.Equal(expected.Usage.OutputTokenCount, aggregated.Usage?.OutputTokenCount);
    }

    [Fact]
    public async Task The_same_prompt_always_gets_the_same_answer()
    {
        var messages = Prompt("How long is leave?", ("hr/leave", "Leave is 14 days."), ("hr/other", "Leave requests need approval."));

        var first = await _client.GetResponseAsync(messages);
        var second = await _client.GetResponseAsync(messages);

        Assert.Equal(first.Text, second.Text);
        Assert.Equal(first.ResponseId, second.ResponseId);
    }

    [Fact]
    public void Metadata_names_the_offline_provider_and_model()
    {
        var metadata = _client.GetService<ChatClientMetadata>();

        Assert.NotNull(metadata);
        Assert.Equal(ExtractiveChatClient.ProviderName, metadata.ProviderName);
        Assert.Equal("offline-fast", metadata.DefaultModelId);
    }

    /// <summary>The same layout the RAG handler produces: rules in the system message, documents and question in the user message.</summary>
    internal static List<ChatMessage> Prompt(string question, params (string Source, string Text)[] documents)
    {
        var body = string.Concat(documents.Select(d => $"<document source=\"{d.Source}\" title=\"{d.Source}\">\n{d.Text}\n</document>\n"));
        return
        [
            new ChatMessage(ChatRole.System, SystemRules),
            new ChatMessage(ChatRole.User, $"<documents>\n{body}</documents>\n\n<question>\n{question}\n</question>"),
        ];
    }
}
