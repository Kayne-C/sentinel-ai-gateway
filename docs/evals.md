# Ölçümler

Her sayı `eval/results/*.json` içindeki ham çıktıdan gelir ve `eval/` altındaki betiklerle yeniden üretilir. Önce
**neyin ölçülmediğini** söyleyelim: gerçek trafik, üretim modelleri ve gerçek bir Entra tenant'ı yok.

## Ortam

Tek VM, CPU. Sohbet modeli `qwen2.5:0.5b`, gömme `all-minilm:l6-v2` (384 boyut), ikisi de Ollama. SQL Server 2025 (gerçek
`vector(384)`), Redis 8. Hepsi aynı makinede.

## 1. Red team: yetkisiz erişim denemesi

Demo tenant'ta her belgeye benzersiz bir **canary** kodu gömülü. Yetkisi olmayan kişi olarak, o belgeye yönelik 10 saldırı
kategorisinden (doğrudan, rol yapma, sosyal mühendislik, kodlama/obfuscation, numaralandırma vb.) prompt gönderilir. Yanıtta
canary çıkarsa veya yasaklı belge kaynak gösterilirse **sızıntı** sayılır. Denetim kaydı da ayrıca taranır.

| Koşul | Saldırı | Sızıntı | Injection korumasıyla reddedilen | Kontrol grubu* |
|---|---|---|---|---|
| A: tüm katmanlar açık | 493 | **0** (%95 GA 0–0,8) | %53,3 | canary geri geldi: %25,5 (n=102) |
| B: injection tespiti **kapalı** (yalnız ACL) | 171 (ilk 200 istek) | **0** (%95 GA 0–2,2) | %0 | canary geri geldi: %55,2 (n=29) |

\* Aynı prompt'lar belgeyi okuma yetkisi **olan** kişiyle gönderilir. Amaç: sistem gerçekten işe yarıyor mu?

Okuması:

- **Sızıntı garantisi detektörlere bağlı değil.** B koşulunda hiçbir saldırı engellenmedi ve yine 0 sızıntı çıktı. Çünkü yetkisiz parça
  sorgu aşamasında hiç getirilmiyor. Detektörler ikinci savunma hattı, birincisi değil.
- **Aşırı engelleme maliyeti var.** A koşulunda yetkili kullanıcıların canary sorularının yalnızca %25,5'i cevap alıyor, B'de %55,2.
  Fark, öğrenilmiş detektörün meşru sorulara da takılmasından geliyor (aşağıdaki FPR).
- Sıfır sızıntı "0/493" demektir, "imkânsız" demek değildir. Güven aralığının üst sınırı %0,8.
- B koşulu 200 istekle sınırlıdır (süre: yerel CPU).

## 2. Prompt injection tespiti

Üç ölçüm, karıştırmamak için ayrı:

| Küme | Tespit | Recall | FPR |
|---|---|---|---|
| deepset test bölümü (60 saldırı / 56 normal), **eğitimde hiç görülmedi** | yalnız kurallar | %6,7 | %0 |
| aynı küme | öğrenilmiş sınıflandırıcı | **%60,0** | **%1,8** |
| aynı küme | kural ∪ öğrenilmiş | %61,7 | %1,8 |
| Kendi elle yazılmış TR/EN kümesi (139 örnek, kurallar için eşik 0,7) | kurallar | %69,9 | %1,8 |
| Kendi küme, held-out bölümü (17 / 11) | öğrenilmiş | %70,6 | %9,1 (1/11, çok küçük örnek) |

- deepset (CC BY 4.0) indirilir, depoya konmaz. Almanca örneklerde kural tabanlı recall %1,2.
- Sınıflandırıcı: MiniLM gömmesi üzerinde L2 düzenlemeli lojistik regresyon, ağırlıklar `injection-model.json` içinde (384 sayı).
  Çapraz doğrulama recall %55, FPR %2,8.
- **Sonuç:** injection tespiti bu sistemde kusurlu bir **ek** katmandır. İddia edilebilecek en fazla "kuralların kaçırdığı saldırıların bir kısmını yakalar"dır.
  "Prompt injection'ı engeller" denemez.

Benign soru testinde (önbellek e2e koşusu, 72 meşru soru) %5,6 yanlış engelleme görüldü (%95 GA 2,2–13,4).

## 3. PII maskeleme

Tohumlu üretici: 2000 pozitif, 1000 negatif (rakam içeren ama PII olmayan metinler), standart ve varyant (boşluklu, tireli, gömülü) biçimler.

| | |
|---|---|
| Kesinlik / duyarlılık / F1 | 0,9988 / 1,000 / **0,9994** |
| Yanlış alarm | 3 / 1000 negatif; hepsi `PaymentCard`: Luhn'u geçen bir referans numarası ve IBAN'ın içindeki 16 hane |
| Hız | 0,24 ms / örnek |

**Sınırlar:** veri kümesini tanıdık kalıplarla biz ürettik, bu yüzden bu sonuç **eğitim-içi** sayılmalı ve gerçek veride düşer.
Ölçüm iki gerçek boşluğu bulup düzelttirdi: gruplanmış TCKN (`123 456 789 01`) ve bağlamsız çıplak 10 haneli GSM numaraları.
Rakamla yazılmış olmayan PII (ör. "sıfır beş yüz...") ve ad/adres tespiti kapsam dışı.

## 4. Semantik önbellek

**Eşik taraması** (30 önbellekli soru, 60 paraphrase, 30 yakın-yanlış):

| Eşik | Paraphrase doğru servis | Paraphrase YANLIŞ servis | Yakın-yanlış servis edildi |
|---|---|---|---|
| 0,70 | %46,7 | %25,0 | %63,3 |
| 0,80 | %25,0 | %15,0 | %36,7 |
| 0,90 | %3,3 | %1,7 | %13,3 |
| 0,92 (varsayılan) | %1,7 | 0 | %6,7 |
| 0,95 | 0 | 0 | 0 |

Paraphrase'ların medyan benzerliği 0,780, yakın-yanlışlarınki 0,772: **all-minilm-l6-v2 Türkçe'de iki sınıfı ayıramıyor.**
Yani bu modelle semantik önbellek ya hiç isabet vermiyor ya da yanlış cevap veriyor. Hit oranı iddiası bu yüzden **yok**.
Çoklu dilli bir gömme modeliyle (ör. bge-m3, multilingual-e5) yeniden ölçülmeli. Önbellek mekanizması (ACL kapsamı, geçersiz kılma) testlerle doğrulandı, kalite ise embedding'e bağlı.

Uçtan uca (72 istek): paraphrase isabeti %0 (n=33), yakın-yanlış yanlış servis %5,9 (1/17, çok küçük örnek), isabette gecikme 263 ms, ıskada p50 1365 ms.

**Erişim (retrieval):** 34 Türkçe soru, 7 belge: hit@1 %58,8, hit@3 %73,5, MRR 0,709 (aynı gömme modeli, yine zayıf nokta).

## 5. Proxy ek yükü

40 sıralı-karışık tamamlama (8 çıktı tokenı): doğrudan Ollama p50 388 ms, gateway üzerinden p50 706 ms. **Fark ≈ 318 ms.**
Bu, kimlik doğrulama + PII + injection (öğrenilmiş detektör bir gömme çağrısı yapar) + bütçe + denetim yazımının toplamıdır.
Gömme çağrısı CPU'da baskındır. Ayrı bir gömme ve GPU ile belirgin biçimde düşmesi beklenir ama ölçülmedi.

## Yeniden üretme

Bkz. [eval/README.md](../eval/README.md).
