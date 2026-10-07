using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sentinel.Application.Guardrails;
using Sentinel.Guardrails;
using Sentinel.Guardrails.Injection;
using Sentinel.Guardrails.Pii;

namespace Sentinel.Application.Tests.Guardrails;

/// <summary>
/// The proxy streams answers (SSE) through <see cref="IOutputStream"/> and non-streamed answers through
/// <see cref="IPromptGuard.GuardOutput"/>. A client must not be able to get PII (or lose a placeholder) just by asking for
/// streaming, so both paths must produce the same text for every way the answer is cut into deltas. Real recognizers.
/// </summary>
public sealed class OutputStreamEquivalenceTests
{
    // Checksum-valid values (see the Guardrails tests): a TCKN, a Turkish IBAN and two emails.
    private const string Tckn = "20433218148";
    private const string Iban = "TR25 0006 2061 5594 0781 6184 95";

    // Never typed by the caller in these tests: must stay masked.
    private const string OtherIban = "TR94 0006 2093 1034 1316 4752 55";

    private static readonly string[] Answers =
    [
        "Merhaba [EMAIL_1], talebiniz alındı. İletişim: [PHONE_1].",
        "Yeni bir kişi: ayse.yilmaz@contoso.example ve TCKN " + Tckn + " bulundu; ilk e-posta [EMAIL_1] idi.",
        "IBAN'ınız " + Iban + " (bordro sorumlusuna ait), sizinki [IBAN_1].",
        "Placeholder bölünmesi: [EMA" + "IL_1] ve [ig" + "nore] ve [ iki ] ve [EMAIL_9] bilinmeyen.",
        "Görünmez​ karakter⁠ ve ay​se@contoso.example gizlenmiş e-posta, 05324182765 numarası.",
        "Sayılar: 2025 yılında 11 kişi, 3 kez; 20433218 ile 148 ayrı yazılmış. Sürüm 1.2.3.4.5 bir IP değil.",
        "Tek satırlık kısa yanıt.",
        "[",
        "[EMAIL_1",
        "tr25 0006 2061 5594 0781 6184 95 küçük harfle IBAN; +90 532 418 27 65 telefon; 10.42.7.15 adresi.",
    ];

    [Fact]
    public async Task Streaming_a_cut_up_answer_gives_exactly_the_text_of_the_non_streaming_path()
    {
        var guard = CreateGuard();
        var random = new Random(20261007);
        var checkedSplits = 0;

        foreach (var answer in Answers)
        {
            var expected = guard.GuardOutput(answer, await CallerVaultAsync(guard));

            for (var iteration = 0; iteration < 60; iteration++)
            {
                var vault = await CallerVaultAsync(guard);
                var stream = guard.CreateOutputStream(vault);
                var actual = new StringBuilder();
                foreach (var delta in RandomSplit(answer, random, iteration))
                {
                    actual.Append(stream.Push(delta));
                }

                actual.Append(stream.Flush());
                Assert.True(
                    expected == actual.ToString(),
                    $"answer: '{answer}'{Environment.NewLine}expected: '{expected}'{Environment.NewLine}streamed: '{actual}'");
                checkedSplits++;
            }
        }

        Assert.True(checkedSplits >= 300);
    }

    [Fact]
    public async Task A_streamed_answer_never_contains_pii_the_caller_did_not_type()
    {
        var guard = CreateGuard();
        var vault = await CallerVaultAsync(guard);
        var stream = guard.CreateOutputStream(vault);
        var output = new StringBuilder();

        // One character per delta is the worst case for a tail-holding guard.
        foreach (var character in $"Bordro: {OtherIban}, kişi {Tckn}, mail yeni.kisi@contoso.example, tel 0555 123 45 67.")
        {
            output.Append(stream.Push(character.ToString()));
        }

        output.Append(stream.Flush());
        var text = output.ToString();
        Assert.DoesNotContain("TR94", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0006 2093", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Tckn, text, StringComparison.Ordinal);
        Assert.DoesNotContain("yeni.kisi@contoso.example", text, StringComparison.Ordinal);
        Assert.DoesNotContain("123 45 67", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Values_the_caller_typed_are_restored_while_streaming()
    {
        var guard = CreateGuard();
        var vault = await CallerVaultAsync(guard);
        var stream = guard.CreateOutputStream(vault);
        var output = new StringBuilder();
        foreach (var delta in new[] { "Yanıt ", "[EMA", "IL_", "1] adresine", " gönderildi." })
        {
            output.Append(stream.Push(delta));
        }

        output.Append(stream.Flush());
        Assert.Equal("Yanıt ayse@contoso.example adresine gönderildi.", output.ToString());
    }

    private static async Task<PiiVault> CallerVaultAsync(IPromptGuard guard)
    {
        var vault = new PiiVault();
        var input = await guard.GuardInputAsync(
            [new ChatMessage(ChatRole.User, "Ben ayse@contoso.example, telefonum +90 532 418 27 65, IBAN " + Iban + ".")],
            vault,
            CancellationToken.None);
        Assert.False(input.Blocked);
        Assert.Equal(3, vault.DistinctValues);
        return vault;
    }

    private static IEnumerable<string> RandomSplit(string text, Random random, int iteration)
    {
        if (iteration == 0)
        {
            foreach (var element in StringInfoElements(text))
            {
                yield return element; // one character at a time
            }

            yield break;
        }

        var index = 0;
        while (index < text.Length)
        {
            var length = Math.Min(text.Length - index, 1 + random.Next(0, iteration % 2 == 0 ? 4 : 14));
            if (char.IsHighSurrogate(text[index + length - 1]) && index + length < text.Length)
            {
                length++; // never cut a surrogate pair: providers emit whole code points
            }

            yield return text.Substring(index, length);
            index += length;
        }
    }

    private static IEnumerable<string> StringInfoElements(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            yield return char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? text.Substring(i++, 2) : text[i].ToString();
        }
    }

    private static PromptGuard CreateGuard()
    {
        var provider = new ServiceCollection()
            .AddGuardrails(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        return new PromptGuard(
            provider.GetRequiredService<IPiiRedactor>(),
            provider.GetRequiredService<OutputGuard>(),
            provider.GetRequiredService<IPromptInjectionDetector>(),
            provider.GetRequiredService<IOptions<PiiOptions>>(),
            provider.GetRequiredService<IOptions<InjectionOptions>>());
    }
}
