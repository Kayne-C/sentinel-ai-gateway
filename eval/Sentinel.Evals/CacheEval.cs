using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Sentinel.Evals;

/// <summary>
/// Semantic cache and retrieval quality with the embedding model the gateway ships with (all-MiniLM-L6-v2 via Ollama).
/// <list type="number">
/// <item><b>Threshold sweep</b> (embeddings only, cheap): paraphrases of cached questions should hit, questions with a
/// different intent that merely share words must not. A cache that serves the wrong answer is worse than none.</item>
/// <item><b>Retrieval</b> (embeddings only): does the right document come first for a Turkish question?</item>
/// <item><b>End to end</b> through a running gateway with Redis: hit rate, wrong reuse, latency of hits vs misses and the
/// list-price cost avoided (the models here run locally, so real spend is zero; prices are those of the configured tiers).</item>
/// </list>
/// The question sets are written by hand and small: they show the shape of the trade-off, not a production hit rate.
/// </summary>
internal static class CacheEval
{
    /// <summary>Canonical question, two paraphrases, one near miss (shares words, asks something else). <c>Public</c>: answerable by anyone of the tenant.</summary>
    private sealed record Group(string Canonical, string ParaphraseA, string ParaphraseB, string NearMiss, bool Public);

    private static readonly Group[] Groups =
    [
        new("Yıllık izin kaç gün?", "Bir çalışan yılda kaç gün izin kullanabilir?", "Senelik izin hakkı kaç iş günüdür?", "Doğum izni kaç gün sürer?", true),
        new("Uzaktan çalışma haftada kaç gün yapılabilir?", "Haftada en fazla kaç gün evden çalışabilirim?", "Home office politikasında kaç gün hakkım var?", "Uzaktan çalışma için hangi ekipman veriliyor?", true),
        new("İzin talebi ne kadar önceden yapılmalı?", "İzin başvurusunu kaç hafta önce vermem gerekir?", "Yıllık izin için başvuru süresi nedir?", "İzin talebini kim onaylar?", true),
        new("Seyahat masraflarını kaç gün içinde girmeliyim?", "Seyahat dönüşü masraf girişi için süre nedir?", "Masraf fişlerini ne zamana kadar sisteme yüklemeliyim?", "Seyahatte günlük yemek harcırahı ne kadar?", true),
        new("Günlük yemek harcırahı kaç TL?", "Yurt içi seyahatte günlük yemek ödeneği ne kadar?", "Yemek için günlük ne kadar harcayabilirim?", "Konaklama için hangi oteller tercih edilir?", true),
        new("Business sınıf uçuş ne zaman mümkün?", "Hangi durumda business sınıfı bilet alınabilir?", "Uzun uçuşlarda business class kullanılabilir mi?", "Uçak biletleri hangi sınıfta alınır?", true),
        new("Kurban Bayramı arifesinde çalışılır mı?", "Bayram arifelerinde yarım gün mü çalışılıyor?", "Arife günlerinde mesai nasıl işliyor?", "Yılbaşı tatil mi?", true),
        new("1 Mayıs resmi tatil mi?", "Emek ve Dayanışma Günü tatil günü müdür?", "Mayıs ayının ilk günü çalışılır mı?", "Kurban Bayramı kaç gün tatil?", true),
        new("SEV1 olayında yanıt süresi nedir?", "SEV1 olaylarında ne kadar sürede müdahale edilir?", "Kritik üretim olayında ilk yanıt süresi kaç dakikadır?", "SEV2 olaylarında yanıt süresi nedir?", false),
        new("Nöbet devri ne zaman yapılır?", "Nöbet teslimi hangi gün ve saatte?", "On-call devir saati nedir?", "Nöbet telefonu numarası nedir?", false),
        new("Olay sonrası analiz kaç gün içinde yazılır?", "Postmortem raporu için süre nedir?", "Olay kapandıktan sonra analiz ne zaman hazırlanmalı?", "Olay sonrası analiz suçlama içerir mi?", false),
        new("Onaylı panel tedarikçileri kimler?", "Hangi firmalardan panel alınabilir?", "Panel tedarikinde onaylı şirketler hangileri?", "Onaylı bakım firmaları kimler?", false),
        new("Tedarikçi değerlendirmesi hangi ayda yapılır?", "Tedarikçiler yılın hangi ayında değerlendirilir?", "Yıllık tedarikçi değerlendirmesi ne zaman?", "Tedarikçi değerlendirme kriterleri nelerdir?", false),
        new("Fatura itirazı için kime yazmalıyım?", "Fatura itirazı hangi adrese iletilir?", "Bordro itirazı nereye yapılır?", "Bordro itirazı kaç gün içinde yapılmalı?", false),
        new("Geri alma kararını kim verir?", "Dağıtımı geri alma yetkisi kimde?", "Rollback kararı kime ait?", "Veritabanı kesintisinde önce ne yapılır?", false),
        new("Kıdemli mühendisin brüt ücret bandı nedir?", "Kıdemli mühendis maaş aralığı kaçtır?", "Senior mühendis için brüt aylık ücret ne kadar?", "Takım lideri ücret bandı nedir?", false),
        new("Performans payı yüzde kaç?", "Beklentinin üzerinde performansa ek zam oranı nedir?", "Üstün performans için ilave yüzde kaç veriliyor?", "Zam takvimi ne zaman yürürlüğe giriyor?", false),
        new("Üçüncü çeyrek gelir tahmini nedir?", "2026 Q3 konsolide gelir beklentisi ne kadar?", "Üç aylık dönemde ne kadar gelir bekleniyor?", "Üçüncü çeyrek FAVÖK marjı beklentisi nedir?", false),
        new("Kur artışı FAVÖK'ü nasıl etkiler?", "Euro kuru yükselirse FAVÖK ne kadar düşer?", "Kur riskinin FAVÖK üzerindeki etkisi nedir?", "Hedge oranı kaç olacak?", false),
        new("Kritik güvenlik bulgularının kapatılma tarihi nedir?", "Sızma testindeki kritik bulgular ne zamana kadar kapatılacak?", "Kritik açıkların düzeltme son tarihi ne?", "Sızma testi hangi ayda yapıldı?", false),
        new("How many vacation days do I get?", "What is my annual leave entitlement?", "How many days of paid leave do employees get per year?", "How many sick days do I get?", true),
        new("What is the travel expense deadline?", "By when must expenses be submitted after a trip?", "How long do I have to file my travel receipts?", "What is the daily meal allowance when travelling?", true),
        new("How do I reset my password?", "I forgot my password, what should I do?", "Steps to recover a lost password?", "How do I change my username?", true),
        new("Toplantı odası nasıl rezerve edilir?", "Toplantı salonu rezervasyonu nasıl yapılır?", "Bir toplantı odasını nasıl ayırtırım?", "Toplantı odasında projeksiyon nasıl kullanılır?", true),
        new("Fatura numarası nerede yazar?", "Faturanın numarasını nereden bulurum?", "Fatura no hangi alanda görünür?", "Fatura nasıl iptal edilir?", true),
        new("Şirket araçları kimler tarafından kullanılabilir?", "Şirket aracını hangi çalışanlar kullanabilir?", "Araç tahsisi için kimler uygun?", "Şirket aracı yakıt kartı nasıl alınır?", true),
        new("Eğitim bütçesi yılda ne kadar?", "Yıllık eğitim ödeneği kaç TL?", "Kişi başı eğitim bütçesi nedir?", "Eğitim talebi nasıl yapılır?", true),
        new("Laptop arızalanırsa ne yapmalıyım?", "Bilgisayarım bozulursa kime başvurmalıyım?", "Dizüstü arızasında destek nasıl alınır?", "Yeni laptop ne zaman verilir?", true),
        new("VPN'e nasıl bağlanırım?", "Uzaktan şirket ağına bağlanma adımları nelerdir?", "VPN bağlantısı için ne yapmam gerekir?", "VPN parolamı nasıl sıfırlarım?", true),
        new("Sağlık sigortası kimleri kapsar?", "Özel sağlık sigortasından kimler yararlanır?", "Sigorta kapsamına aile üyeleri giriyor mu?", "Sağlık sigortası poliçesi nasıl yenilenir?", true),
    ];

    /// <summary>Questions with the single document that answers them (Contoso demo corpus).</summary>
    private static readonly (string Question, string Document)[] RetrievalSet =
    [
        ("2026 yılında kıdemli mühendis için brüt ücret bandı nedir?", "ik-ucret-bantlari-2026"),
        ("Performans notu beklentinin üzerinde olanlara kaç puan ek zam uygulanır?", "ik-ucret-bantlari-2026"),
        ("Bordro itirazları kaç gün içinde yapılmalı?", "ik-ucret-bantlari-2026"),
        ("Direktör maaş aralığı nedir?", "ik-ucret-bantlari-2026"),
        ("Zam ne zaman yürürlüğe girer?", "ik-ucret-bantlari-2026"),
        ("SEV1 olayında nöbetçi mühendis kaç dakika içinde olay kanalını açar?", "muhendislik-olay-mudahale"),
        ("Veritabanı kaynaklı kesintide ilk adım nedir?", "muhendislik-olay-mudahale"),
        ("Nöbet devri hangi gün yapılır?", "muhendislik-olay-mudahale"),
        ("Olay sonrası analiz kaç iş günü içinde yazılır?", "muhendislik-olay-mudahale"),
        ("Geri alma kararı kime aittir?", "muhendislik-olay-mudahale"),
        ("2026 üçüncü çeyrek konsolide gelir tahmini nedir?", "finans-2026-q3-tahmin"),
        ("Güneş enerjisi segmenti gelirin yüzde kaçını oluşturur?", "finans-2026-q3-tahmin"),
        ("Yüzde 5'lik kur artışı FAVÖK'ü ne kadar azaltır?", "finans-2026-q3-tahmin"),
        ("Hedge oranı yüzde kaça çıkarılacak?", "finans-2026-q3-tahmin"),
        ("Tahmin yönetim kuruluna ne zaman sunulacak?", "finans-2026-q3-tahmin"),
        ("İlk beş yıl için yıllık izin kaç gündür?", "izin-politikasi"),
        ("Ofis çalışanları haftada kaç gün uzaktan çalışabilir?", "izin-politikasi"),
        ("İzin talebi ne kadar önceden yapılmalıdır?", "izin-politikasi"),
        ("Bayram arifelerinde çalışma düzeni nasıl?", "izin-politikasi"),
        ("Saha ekipleri uzaktan çalışabilir mi?", "izin-politikasi"),
        ("Haziran sızma testinde kaç kritik bulgu tespit edildi?", "guvenlik-sizma-testi-2026"),
        ("SCADA arayüzündeki sorun nedir?", "guvenlik-sizma-testi-2026"),
        ("Eski VPN ağ geçidinde hangi açık var?", "guvenlik-sizma-testi-2026"),
        ("Kritik bulguların kapatılma tarihi nedir?", "guvenlik-sizma-testi-2026"),
        ("Saha tabletlerinde disk şifrelemesi açık mı?", "guvenlik-sizma-testi-2026"),
        ("Onaylı panel tedarikçileri hangileri?", "tedarikci-listesi"),
        ("Hangi bakım firmalarıyla çalışılıyor?", "tedarikci-listesi"),
        ("Tedarikçi değerlendirmesi hangi ayda yapılır?", "tedarikci-listesi"),
        ("Tedarikçi değerlendirme kriterleri nelerdir?", "tedarikci-listesi"),
        ("Uçak biletleri hangi sınıfta alınır?", "seyahat-masraf-politikasi"),
        ("Yurt içi günlük yemek harcırahı kaç TL?", "seyahat-masraf-politikasi"),
        ("Masraflar seyahat dönüşünden sonra kaç iş günü içinde girilmeli?", "seyahat-masraf-politikasi"),
        ("Konaklamada kaç yıldızlı otel tercih edilir?", "seyahat-masraf-politikasi"),
        ("Kişisel kredi kartıyla yapılan harcamalar ne zaman ödenir?", "seyahat-masraf-politikasi"),
    ];

    public static async Task RunAsync(string gatewayUrl, string ollamaUrl, string? corpusPath, bool endToEnd)
    {
        using var http = new HttpClient { BaseAddress = new Uri(ollamaUrl), Timeout = TimeSpan.FromMinutes(5) };

        var sweep = await ThresholdSweepAsync(http);
        var retrieval = await RetrievalAsync(http, Corpus.Load(corpusPath ?? Corpus.DefaultPath()));
        object? e2e = null;
        if (endToEnd)
        {
            e2e = await EndToEndAsync(gatewayUrl);
        }

        Report.Save("cache", new { thresholdSweep = sweep, retrieval, endToEnd = e2e });
    }

    private static async Task<object> ThresholdSweepAsync(HttpClient http)
    {
        var cached = await EmbedAsync(http, Groups.Select(g => g.Canonical).ToList());
        var paraphrases = Groups.SelectMany((g, i) => new[] { (i, g.ParaphraseA), (i, g.ParaphraseB) }).ToList();
        var nearMisses = Groups.Select((g, i) => (i, g.NearMiss)).ToList();
        var paraphraseVectors = await EmbedAsync(http, paraphrases.Select(p => p.Item2).ToList());
        var nearMissVectors = await EmbedAsync(http, nearMisses.Select(p => p.Item2).ToList());

        (double Best, int Group) Nearest(float[] vector)
        {
            var scores = cached.Select(c => Cosine(c, vector)).ToList();
            var best = scores.Max();
            return (best, scores.IndexOf(best));
        }

        var paraphraseHits = paraphrases.Select((p, i) => (Nearest(paraphraseVectors[i]), p.i)).ToList();
        var nearMissHits = nearMisses.Select((p, i) => (Nearest(nearMissVectors[i]), p.i)).ToList();

        Console.WriteLine($"Semantic cache threshold sweep ({Groups.Length} cached questions; {paraphrases.Count} paraphrases, {nearMisses.Count} near misses)");
        Console.WriteLine("  threshold | paraphrase served correctly | paraphrase served WRONG | near miss served (wrong reuse)");
        var rows = new List<object>();
        foreach (var threshold in new[] { 0.70, 0.75, 0.80, 0.85, 0.90, 0.92, 0.95 })
        {
            var correct = paraphraseHits.Count(h => h.Item1.Best >= threshold && h.Item1.Group == h.i);
            var wrong = paraphraseHits.Count(h => h.Item1.Best >= threshold && h.Item1.Group != h.i);
            var reused = nearMissHits.Count(h => h.Item1.Best >= threshold);
            Console.WriteLine($"  {threshold,9:0.00} | {Report.Pct((double)correct / paraphrases.Count),27} | {Report.Pct((double)wrong / paraphrases.Count),23} | {Report.Pct((double)reused / nearMisses.Count),10}");
            rows.Add(new { threshold, paraphraseCorrect = correct, paraphraseWrong = wrong, paraphraseTotal = paraphrases.Count, nearMissServed = reused, nearMissTotal = nearMisses.Count });
        }

        var paraphraseSimilarities = paraphraseHits.Select(h => h.Item1.Best).Order().ToList();
        var nearMissSimilarities = nearMissHits.Select(h => h.Item1.Best).Order().ToList();
        Console.WriteLine($"  similarity to the nearest cached question: paraphrases median {Report.Percentile(paraphraseSimilarities, 0.5):0.000} (p10 {Report.Percentile(paraphraseSimilarities, 0.1):0.000}), near misses median {Report.Percentile(nearMissSimilarities, 0.5):0.000} (p90 {Report.Percentile(nearMissSimilarities, 0.9):0.000})");

        return new
        {
            rows,
            paraphraseMedian = Report.Percentile(paraphraseSimilarities, 0.5),
            nearMissMedian = Report.Percentile(nearMissSimilarities, 0.5),
            nearMissP90 = Report.Percentile(nearMissSimilarities, 0.9),
        };
    }

    private static async Task<object> RetrievalAsync(HttpClient http, Corpus corpus)
    {
        // The unit that is embedded at ingestion is "title, blank line, chunk"; documents here are short, so chunk = paragraph.
        var contoso = corpus.Documents.Where(d => d.TenantId == corpus.Documents.First().TenantId).ToList();
        var units = contoso.SelectMany(d => d.Content.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(p => (d.ExternalId, Text: $"{d.Title}\n\n{p.Trim()}"))).ToList();
        var unitVectors = await EmbedAsync(http, units.Select(u => u.Text).ToList());
        var questionVectors = await EmbedAsync(http, RetrievalSet.Select(q => q.Question).ToList());

        int hitAt1 = 0, hitAt3 = 0;
        double reciprocalRank = 0;
        var gaps = new List<double>();
        for (var i = 0; i < RetrievalSet.Length; i++)
        {
            var ranking = contoso.Select(d => (d.ExternalId, Score: units.Select((u, k) => (u.ExternalId, S: Cosine(unitVectors[k], questionVectors[i])))
                    .Where(u => u.ExternalId == d.ExternalId).Max(u => u.S)))
                .OrderByDescending(r => r.Score).ToList();
            var rank = ranking.FindIndex(r => r.ExternalId == RetrievalSet[i].Document) + 1;
            hitAt1 += rank == 1 ? 1 : 0;
            hitAt3 += rank <= 3 ? 1 : 0;
            reciprocalRank += 1.0 / rank;
            gaps.Add(ranking.First(r => r.ExternalId == RetrievalSet[i].Document).Score);
        }

        var n = RetrievalSet.Length;
        gaps.Sort();
        Console.WriteLine($"Retrieval over the {contoso.Count} documents of the Contoso demo tenant, {n} Turkish questions (ACL ignored: ranking quality only)");
        Console.WriteLine($"  hit@1 {Report.Interval(hitAt1, n)}");
        Console.WriteLine($"  hit@3 {Report.Interval(hitAt3, n)}");
        Console.WriteLine($"  MRR {reciprocalRank / n:0.000}; similarity of the right document: median {Report.Percentile(gaps, 0.5):0.000}, p10 {Report.Percentile(gaps, 0.1):0.000}");
        return new { questions = n, hitAt1, hitAt3, mrr = reciprocalRank / n, medianSimilarity = Report.Percentile(gaps, 0.5), p10Similarity = Report.Percentile(gaps, 0.1) };
    }

    private static async Task<object> EndToEndAsync(string gatewayUrl)
    {
        using var gateway = new GatewayClient(gatewayUrl);
        const string persona = "deniz"; // no group memberships: only the tenant's public documents are visible
        var misses = new List<double>();
        var hits = new List<double>();
        var missCosts = new List<double>();
        var avoided = 0.0;
        int paraphraseHit = 0, paraphraseTotal = 0, nearMissHit = 0, nearMissTotal = 0, blocked = 0, answered = 0;

        async Task<(bool Hit, double Ms, double Cost, bool Blocked)> Ask(string question)
        {
            var (status, body, ms) = await gateway.PostAsync(persona, "/api/v1/ask", new { question });
            if (status == 422)
            {
                return (false, ms, 0, true);
            }

            if (status != 200)
            {
                // Model unavailable (a local CPU model can stall): not a cache outcome, counted as refused so it cannot skew hit rates.
                Console.WriteLine($"  request failed with HTTP {status}; excluded from the cache statistics");
                return (false, ms, 0, true);
            }

            var json = JsonNode.Parse(body)!;
            return ((bool)json["cacheHit"]!, ms, (double)json["usage"]!["estimatedCostUsd"]!, false);
        }

        var publicGroups = Groups.Where(g => g.Public).ToList();
        Console.WriteLine($"End to end through {gatewayUrl} as '{persona}' (public documents only): {publicGroups.Count} groups, {publicGroups.Count * 4} requests");
        foreach (var group in publicGroups)
        {
            var first = await Ask(group.Canonical);
            blocked += first.Blocked ? 1 : 0;
            if (!first.Blocked)
            {
                answered++;
                (first.Hit ? hits : misses).Add(first.Ms);
                if (!first.Hit)
                {
                    missCosts.Add(first.Cost);
                }
            }

            foreach (var paraphrase in new[] { group.ParaphraseA, group.ParaphraseB })
            {
                var result = await Ask(paraphrase);
                blocked += result.Blocked ? 1 : 0;
                if (result.Blocked)
                {
                    continue;
                }

                answered++;
                paraphraseTotal++;
                paraphraseHit += result.Hit ? 1 : 0;
                (result.Hit ? hits : misses).Add(result.Ms);
                if (result.Hit)
                {
                    avoided += missCosts.Count > 0 ? missCosts.Average() : 0;
                }
                else
                {
                    missCosts.Add(result.Cost);
                }
            }

            var near = await Ask(group.NearMiss);
            blocked += near.Blocked ? 1 : 0;
            if (!near.Blocked)
            {
                answered++;
                nearMissTotal++;
                nearMissHit += near.Hit ? 1 : 0;
                (near.Hit ? hits : misses).Add(near.Ms);
                if (!near.Hit)
                {
                    missCosts.Add(near.Cost);
                }
            }
        }

        misses.Sort();
        hits.Sort();
        Console.WriteLine($"  paraphrases served from the cache : {Report.Interval(paraphraseHit, paraphraseTotal)}");
        Console.WriteLine($"  near misses wrongly served        : {Report.Interval(nearMissHit, nearMissTotal)}");
        Console.WriteLine($"  refused by the injection guardrail (benign questions): {Report.Interval(blocked, blocked + answered)}");
        Console.WriteLine($"  latency  miss p50 {Report.Percentile(misses, 0.5):0} ms / p95 {Report.Percentile(misses, 0.95):0} ms   hit p50 {Report.Percentile(hits, 0.5):0} ms / p95 {Report.Percentile(hits, 0.95):0} ms");
        Console.WriteLine($"  list-price cost avoided by hits: ${avoided:0.0000} of ${avoided + missCosts.Sum():0.0000} that would have been spent");

        return new
        {
            persona,
            paraphraseHit,
            paraphraseTotal,
            nearMissHit,
            nearMissTotal,
            benignQuestionsRefused = blocked,
            benignQuestionsAnswered = answered,
            missLatencyMs = new { p50 = Report.Percentile(misses, 0.5), p95 = Report.Percentile(misses, 0.95), n = misses.Count },
            hitLatencyMs = new { p50 = Report.Percentile(hits, 0.5), p95 = Report.Percentile(hits, 0.95), n = hits.Count },
            costAvoidedUsd = avoided,
            costSpentUsd = missCosts.Sum(),
        };
    }

    private static async Task<List<float[]>> EmbedAsync(HttpClient http, IReadOnlyList<string> texts)
    {
        var vectors = new List<float[]>();
        for (var offset = 0; offset < texts.Count; offset += 32)
        {
            var batch = texts.Skip(offset).Take(32).ToArray();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings")
            {
                Content = JsonContent.Create(new { model = "all-minilm:l6-v2", input = batch }),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "ollama");
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            vectors.AddRange(json["data"]!.AsArray().OrderBy(d => (int)d!["index"]!).Select(d => d!["embedding"]!.AsArray().Select(v => (float)v!).ToArray()));
        }

        return vectors;
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return dot / Math.Sqrt(na * nb);
    }
}
