using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Guardrails;
using Sentinel.Guardrails.Pii;

namespace Sentinel.Evals;

/// <summary>
/// PII redaction measured on a labelled synthetic set: checksum-valid Turkish identifiers (TCKN, VKN, IBAN), payment
/// cards, phones, e-mail and IP addresses embedded in Turkish and English sentences, plus hard negatives (look-alike
/// numbers that fail their checksum, order numbers, dates, versions). The generators below are independent of the
/// recognizers' own checksum code. The data is synthetic and written by the same author as the recognizers: it measures
/// regressions and format coverage, not performance on real traffic.
/// </summary>
internal static class PiiEval
{
    private sealed record Span(PiiType Type, int Start, int Length);

    private sealed record Example(string Text, List<Span> Truth, string Group);

    public static void Run(int positives, int negatives, int seed)
    {
        var provider = new ServiceCollection().AddGuardrails(new ConfigurationBuilder().Build()).BuildServiceProvider();
        var redactor = provider.GetRequiredService<IPiiRedactor>();
        var examples = Generate(positives, negatives, seed);

        var perType = Enum.GetValues<PiiType>().ToDictionary(t => t, _ => new Confusion(0, 0, 0));
        var perGroup = new Dictionary<string, Confusion>();
        var missed = new List<string>();
        var falseAlarms = new List<string>();
        var negativeFalsePositives = 0;
        var negativeExamples = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        foreach (var example in examples)
        {
            var result = redactor.Redact(example.Text, new PiiVault(), PiiOrigin.Caller);
            var matched = new HashSet<int>();
            var exampleConfusion = new Confusion(0, 0, 0);

            foreach (var finding in result.Findings)
            {
                var index = example.Truth.FindIndex(s => s.Type == finding.Type && Overlap(s, finding.Start, finding.Length) >= 0.5);
                if (index >= 0 && matched.Add(index))
                {
                    perType[finding.Type] += new Confusion(1, 0, 0);
                    exampleConfusion += new Confusion(1, 0, 0);
                }
                else
                {
                    perType[finding.Type] += new Confusion(0, 1, 0);
                    exampleConfusion += new Confusion(0, 1, 0);
                    if (falseAlarms.Count < 15)
                    {
                        falseAlarms.Add($"{finding.Type}: \"{example.Text.Substring(finding.Start, finding.Length)}\" in \"{Trim(example.Text)}\"");
                    }
                }
            }

            for (var i = 0; i < example.Truth.Count; i++)
            {
                if (!matched.Contains(i))
                {
                    perType[example.Truth[i].Type] += new Confusion(0, 0, 1);
                    exampleConfusion += new Confusion(0, 0, 1);
                    if (missed.Count < 25)
                    {
                        missed.Add($"{example.Truth[i].Type}: \"{example.Text.Substring(example.Truth[i].Start, example.Truth[i].Length)}\" in \"{Trim(example.Text)}\"");
                    }
                }
            }

            perGroup[example.Group] = perGroup.GetValueOrDefault(example.Group, new Confusion(0, 0, 0)) + exampleConfusion;
            if (example.Truth.Count == 0)
            {
                negativeExamples++;
                if (result.Findings.Count > 0)
                {
                    negativeFalsePositives++;
                }
            }
        }

        watch.Stop();
        var total = perType.Values.Aggregate((a, b) => a + b);

        Console.WriteLine($"PII redaction: {examples.Count} examples ({positives} with PII, {negatives} hard negatives), seed {seed}");
        Console.WriteLine($"  all entities   precision {Report.Pct(total.Precision)}  recall {Report.Pct(total.Recall)}  F1 {Report.Pct(total.F1)}  (TP {total.TruePositives}, FP {total.FalsePositives}, FN {total.FalseNegatives})");
        foreach (var (type, confusion) in perType.Where(p => p.Value.TruePositives + p.Value.FalseNegatives + p.Value.FalsePositives > 0))
        {
            Console.WriteLine($"  {type,-13} precision {Report.Pct(confusion.Precision),7}  recall {Report.Pct(confusion.Recall),7}  (TP {confusion.TruePositives}, FP {confusion.FalsePositives}, FN {confusion.FalseNegatives})");
        }

        foreach (var (group, confusion) in perGroup.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  [{group}] recall {Report.Pct(confusion.Recall)} precision {Report.Pct(confusion.Precision)}");
        }

        Console.WriteLine($"  negatives with at least one false alarm: {Report.Interval(negativeFalsePositives, negativeExamples)}");
        Console.WriteLine($"  speed: {watch.Elapsed.TotalMilliseconds / examples.Count:0.000} ms per example ({examples.Count / watch.Elapsed.TotalSeconds:0} examples/s, single thread)");

        Report.Save("pii", new
        {
            seed,
            positives,
            negatives,
            overall = Summarise(total),
            perType = perType.Where(p => p.Value.TruePositives + p.Value.FalseNegatives + p.Value.FalsePositives > 0)
                .ToDictionary(p => p.Key.ToString(), p => Summarise(p.Value)),
            perGroup = perGroup.ToDictionary(p => p.Key, p => Summarise(p.Value)),
            negativesWithFalseAlarm = negativeFalsePositives,
            negativeExamples,
            millisecondsPerExample = watch.Elapsed.TotalMilliseconds / examples.Count,
            missedSamples = missed,
            falseAlarmSamples = falseAlarms,
        });
    }

    private static object Summarise(Confusion c) => new { c.TruePositives, c.FalsePositives, c.FalseNegatives, c.Precision, c.Recall, c.F1 };

    private static string Trim(string text) => text.Length <= 110 ? text : text[..107] + "...";

    private static double Overlap(Span span, int start, int length)
    {
        var overlap = Math.Min(span.Start + span.Length, start + length) - Math.Max(span.Start, start);
        return overlap <= 0 ? 0 : (double)overlap / Math.Max(span.Length, length);
    }

    // ----------------------------------------------------------------------------------------------------------
    // Data generation
    // ----------------------------------------------------------------------------------------------------------

    private static List<Example> Generate(int positives, int negatives, int seed)
    {
        var random = new Random(seed);
        var examples = new List<Example>(positives + negatives);
        var types = new[]
        {
            PiiType.NationalId, PiiType.TaxNumber, PiiType.Iban, PiiType.PaymentCard, PiiType.PhoneNumber, PiiType.Email, PiiType.IpAddress,
        };

        for (var i = 0; i < positives; i++)
        {
            var type = types[i % types.Length];
            var (text, spans, group) = Compose(random, type, twoEntities: i % 5 == 0);
            examples.Add(new Example(text, spans, group));
        }

        for (var i = 0; i < negatives; i++)
        {
            examples.Add(new Example(Negative(random, i), [], "negative"));
        }

        return examples.OrderBy(_ => random.Next()).ToList();
    }

    private static readonly string[] TurkishFrames =
    [
        "Müşterinin kaydı için {0} bilgisini not alın.",
        "Merhaba, talebinizi işleme almak için {0} değerine ihtiyacımız var.",
        "Lütfen şu bilgiyi doğrulayın: {0}",
        "Fatura sahibi {0} üzerinden kayıtlı görünüyor, kontrol eder misiniz?",
        "Ödeme yapılacak hesap/kimlik: {0}. Teşekkürler.",
        "Bordro sorumlusuna iletilen bilgi: {0}",
        "Ek açıklama olarak {0} verilmiştir, dosyaya eklendi.",
    ];

    private static readonly string[] EnglishFrames =
    [
        "Please record this detail for the customer: {0}.",
        "To process your request we need {0} from you.",
        "Can you double-check the following value? {0}",
        "The account on file shows {0}; could you verify it?",
        "Payment details provided: {0}. Thanks!",
        "Forwarded to payroll: {0}",
        "Note added to the ticket: {0}",
    ];

    private static (string Text, List<Span> Spans, string Group) Compose(Random random, PiiType type, bool twoEntities)
    {
        var (value, label, group) = Value(random, type);
        var turkish = random.Next(2) == 0;
        var frames = turkish ? TurkishFrames : EnglishFrames;
        var spans = new List<Span>();
        var builder = new StringBuilder();

        void Append(PiiType t, string v, string l)
        {
            var frame = frames[random.Next(frames.Length)];
            var rendered = string.Format(CultureInfo.InvariantCulture, frame, l.Length > 0 ? $"{l} {v}" : v);
            var offset = rendered.IndexOf(v, StringComparison.Ordinal);
            spans.Add(new Span(t, builder.Length + offset, v.Length));
            builder.Append(rendered);
        }

        Append(type, value, label);
        if (twoEntities)
        {
            var other = (PiiType)(((int)type + 1 + random.Next(6)) % 7);
            var (v2, l2, _) = Value(random, other);
            builder.Append(' ');
            Append(other, v2, l2);
        }

        return (builder.ToString(), spans, group);
    }

    /// <returns>The value, a context label to put before it (the VKN needs one), and the format group.</returns>
    private static (string Value, string Label, string Group) Value(Random random, PiiType type)
    {
        switch (type)
        {
            case PiiType.NationalId:
                return (random.Next(5) == 0 ? SpaceEvery(Tckn(random), 3, ' ') : Tckn(random), TcknLabel(random), random.Next(5) == 0 ? "variant" : "standard");
            case PiiType.TaxNumber:
                return (Vkn(random), random.Next(2) == 0 ? "Vergi kimlik numarası:" : "VKN:", "standard");
            case PiiType.Iban:
                {
                    var raw = IbanOf(random);
                    var form = random.Next(4);
                    return form switch
                    {
                        0 => (raw, "IBAN:", "standard"),
                        1 => (SpaceEvery(raw, 4, ' '), "IBAN:", "standard"),
                        2 => (SpaceEvery(raw, 4, ' ').ToLowerInvariant(), "iban", "variant"),
                        _ => (raw.ToLowerInvariant(), string.Empty, "variant"),
                    };
                }

            case PiiType.PaymentCard:
                {
                    var number = Card(random);
                    var form = random.Next(4);
                    return form switch
                    {
                        0 => (number, "Kart no:", "standard"),
                        1 => (SpaceEvery(number, 4, ' '), "card", "standard"),
                        2 => (SpaceEvery(number, 4, '-'), "kart", "variant"),
                        _ => (number, string.Empty, "standard"),
                    };
                }

            case PiiType.PhoneNumber:
                {
                    var digits = $"5{random.Next(30, 60)}{random.Next(1000000, 9999999)}"; // 5xx xxx xx xx
                    var forms = new[]
                    {
                        $"0{digits[..3]} {digits[3..6]} {digits[6..8]} {digits[8..]}",
                        $"+90 {digits[..3]} {digits[3..6]} {digits[6..8]} {digits[8..]}",
                        $"0{digits}",
                        $"0{digits[..3]}-{digits[3..6]}-{digits[6..8]}-{digits[8..]}",
                        $"+90{digits}",
                        $"(0212) {random.Next(200, 499)} {random.Next(10, 99)} {random.Next(10, 99)}",
                        $"+44 20 7946 {random.Next(1000, 9999)}",
                        $"+1 202-555-{random.Next(1000, 9999)}",
                    };
                    var index = random.Next(forms.Length);
                    return (forms[index], random.Next(2) == 0 ? "Tel:" : "phone", index is 0 or 1 or 2 ? "standard" : "variant");
                }

            case PiiType.Email:
                {
                    var locals = new[] { "ali.veli", "ayse_yilmaz", "m.demir+fatura", "info", "destek.ekibi", "kaan.cemre", "j.smith", "o.kaya" };
                    var domains = new[] { "contoso.com", "firma.com.tr", "mail.example.org", "sirket.net", "ornek.gov.tr" };
                    return ($"{locals[random.Next(locals.Length)]}{random.Next(1, 99)}@{domains[random.Next(domains.Length)]}", string.Empty, "standard");
                }

            default:
                {
                    if (random.Next(4) == 0)
                    {
                        var v6 = new[] { "2001:db8::ff00:42:8329", "fe80::1ff:fe23:4567:890a", "2001:0db8:85a3:0000:0000:8a2e:0370:7334", "::ffff:192.0.2.128" };
                        return (v6[random.Next(v6.Length)], "IP", "variant");
                    }

                    return ($"{random.Next(1, 223)}.{random.Next(0, 255)}.{random.Next(0, 255)}.{random.Next(1, 254)}", "IP", "standard");
                }
        }
    }

    private static string TcknLabel(Random random) => random.Next(3) switch { 0 => "TCKN:", 1 => "T.C. kimlik no:", _ => string.Empty };

    private static string Negative(Random random, int index)
    {
        // Look-alikes that a careless regex would take for personal data.
        var items = new Func<string>[]
        {
            () => $"Sipariş numaranız {random.NextInt64(1_000_000_000L, 9_999_999_999L)} olarak oluşturuldu.", // 10 digits, no tax context
            () => $"Takip kodu: {random.NextInt64(10_000_000_000L, 99_999_999_999L)}", // 11 digits; checksum almost surely invalid
            () => $"Toplantı {random.Next(1, 28):00}.{random.Next(1, 12):00}.202{random.Next(5, 9)} saat {random.Next(8, 18)}:{random.Next(0, 59):00} için ayarlandı.",
            () => $"Toplam tutar {random.Next(1, 999)}.{random.Next(100, 999)}.{random.Next(100, 999)},{random.Next(10, 99)} TL olarak hesaplandı.",
            () => $"Sürüm {random.Next(1, 9)}.{random.Next(0, 9)}.{random.Next(0, 99)}.{random.Next(0, 999)}.{random.Next(0, 9)} yayınlandı.",
            () => $"Hata kodu {random.Next(100, 999)}.{random.Next(100, 999)}.{random.Next(100, 999)}.{random.Next(100, 999)} döndü.", // IP-shaped, octets > 255 usually
            () => $"Ürün kodu TR{random.Next(10, 99)} {random.Next(1000, 9999)} {random.Next(1000, 9999)} {random.Next(1000, 9999)} {random.Next(1000, 9999)} {random.Next(10, 99)}", // IBAN-shaped, checksum invalid
            () => $"Referans: {random.NextInt64(1_000_000_000_000_000L, 9_999_999_999_999_999L)}", // 16 digits, Luhn almost surely invalid
            () => $"Müşteri no {random.Next(100000, 999999)} için {random.Next(1, 30)} gün içinde dönüş yapılacaktır.",
            () => $"Ofis içi hat {random.Next(1000, 9999)} ve {random.Next(100, 999)} numaralı oda.",
            () => $"Please see ticket #{random.Next(10000, 99999)} and build 2026.{random.Next(1, 12)}.{random.Next(1, 28)} for details.",
            () => $"Write to support at {random.Next(1, 9)}@@ or user@localhost (not a real address).",
            () => $"The coordinates are {random.Next(30, 45)}.{random.Next(1000, 9999)}, {random.Next(25, 44)}.{random.Next(1000, 9999)} near the harbour.",
            () => $"Fatura {random.Next(2020, 2026)}/{random.Next(1000, 9999)} tarihli ve {random.NextInt64(100_000_000L, 999_999_999L)} no'lu irsaliyeye bağlıdır.",
        };

        return items[index % items.Length]();
    }

    // ----------------------------------------------------------------------------------------------------------
    // Independent identifier generators (do not share code with the recognizers)
    // ----------------------------------------------------------------------------------------------------------

    private static string SpaceEvery(string value, int group, char separator) =>
        string.Join(separator, Enumerable.Range(0, (value.Length + group - 1) / group).Select(i => value.Substring(i * group, Math.Min(group, value.Length - i * group))));

    private static string Tckn(Random random)
    {
        var d = new int[11];
        d[0] = random.Next(1, 10);
        for (var i = 1; i < 9; i++)
        {
            d[i] = random.Next(0, 10);
        }

        d[9] = (((d[0] + d[2] + d[4] + d[6] + d[8]) * 7 - (d[1] + d[3] + d[5] + d[7])) % 10 + 10) % 10;
        d[10] = d[..10].Sum() % 10;
        return string.Concat(d);
    }

    private static string Vkn(Random random)
    {
        var d = new int[10];
        for (var i = 0; i < 9; i++)
        {
            d[i] = random.Next(0, 10);
        }

        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var tmp = (d[i] + (9 - i)) % 10;
            var tmp2 = (tmp * (int)Math.Pow(2, 9 - i)) % 9;
            if (tmp != 0 && tmp2 == 0)
            {
                tmp2 = 9;
            }

            sum += tmp2;
        }

        d[9] = (10 - sum % 10) % 10;
        return string.Concat(d);
    }

    private static string IbanOf(Random random)
    {
        if (random.Next(5) == 0)
        {
            var known = new[] { "DE89370400440532013000", "GB29NWBK60161331926819", "FR1420041010050500013M02606", "NL91ABNA0417164300" };
            return known[random.Next(known.Length)];
        }

        var bban = "00062" + "0" + string.Concat(Enumerable.Range(0, 16).Select(_ => random.Next(0, 10)));
        var rearranged = bban + "TR00";
        var numeric = string.Concat(rearranged.Select(c => char.IsLetter(c) ? (c - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString()));
        var remainder = 0;
        foreach (var c in numeric)
        {
            remainder = (remainder * 10 + (c - '0')) % 97;
        }

        return $"TR{98 - remainder:00}{bban}";
    }

    private static string Card(Random random)
    {
        var (prefix, length) = random.Next(5) switch
        {
            0 => ("4", 16),
            1 => ("5" + random.Next(1, 6), 16),
            2 => ("37", 15),
            3 => ("9792", 16),
            _ => ("4", 16),
        };

        var digits = new List<int>(prefix.Select(c => c - '0'));
        while (digits.Count < length - 1)
        {
            digits.Add(random.Next(0, 10));
        }

        var sum = 0;
        for (var i = 0; i < digits.Count; i++)
        {
            var digit = digits[digits.Count - 1 - i];
            if (i % 2 == 0)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
        }

        digits.Add((10 - sum % 10) % 10);
        return string.Concat(digits);
    }
}
