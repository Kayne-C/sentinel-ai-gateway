# Sentinel AI Gateway

Kurumsal belgeler üzerinde soru-cevap yapan bir yapay zekâ uygulamasının **önüne konan güvenlik ve maliyet katmanı**.
.NET 10, Clean Architecture. Yerel modellerle (Ollama) ve gerçek SQL Server 2025 + Redis 8 ile uçtan uca çalışır ve ölçülür.

Çözdüğü sorun: bir şirket belgelerini LLM'e bağlayınca üç şey ters gidebilir. Yetkisi olmayan kişi başkasının belgesinden
cevap alabilir, kişisel veri (TCKN, IBAN, telefon) modele ve loglara sızabilir, kontrolsüz kullanım faturayı şişirebilir.
Sentinel bu üçünü tek bir geçit (gateway) arkasında toplar.

## Neler yapıyor

| Alan | Yaklaşım | Kanıt |
|---|---|---|
| **İzin duyarlı RAG** | Belge ACL'si, vektör sorgusunun **içinde** filtrelenir (tam KNN, ANN yok). Yetkisiz parça hiç okunmaz, sıralanmaz | Red team: 493 saldırı, **0 sızıntı** (detektörler kapalıyken de 0/171) |
| **PII maskeleme** | TCKN (algoritma doğrulamalı), VKN, IBAN (mod-97), kart (Luhn), telefon, e-posta, IP. Yer tutucu + kasa, yanıtta yalnızca çağıranın kendi değerleri geri konur | Sentetik sette F1 0,999 ([sınırlar](docs/evals.md)) |
| **Prompt injection koruması** | Kurallar + gömme tabanlı öğrenilmiş sınıflandırıcı; getirilen belge parçaları da taranır; çıktı da süzülür | Held-out deepset: recall %60 (yalnız kurallar %6,7), FPR %1,8 |
| **Tahrif belirgin denetim** | Tenant başına hash zinciri + SQL Server ledger tablosu, `/audit/verify` | Entegrasyon testi, bozulma tespiti |
| **Semantik önbellek** | Redis 8 HNSW, tenant + ACL kapsamlı, belge/ACL değişince geçersiz kılma | Dürüst sonuç: MiniLM Türkçe'de eşik ayrımı zayıf, bkz. [evals](docs/evals.md) |
| **Maliyet kontrolü** | Tenant aylık / kullanıcı günlük token bütçesi (atomik rezervasyon), karmaşıklığa göre model yönlendirme | Testler |
| **OpenAI uyumlu proxy** | YARP `IHttpForwarder` ile veri düzlemi; katı izin listesi, istek ve yanıt yeniden kurulur | Entegrasyon testleri |
| **Kimlik** | Entra ID JWT (tid/oid/groups/roles), grup taşması (overage) durumunda **kapalı başarısız** | Birim testli, gerçek tenant ile **denenmedi** |
| **Gözlemlenebilirlik** | OpenTelemetry, Aspire Dashboard | docker compose |

Semantic Kernel entegrasyonu: `Sentinel.SemanticKernel` ile prompt ve araç-sonucu filtreleri, izin duyarlı `KnowledgePlugin`.

## Hızlı başlangıç

```bash
docker compose up -d --build        # SQL Server 2025, Redis 8, Ollama (modeller çekilir), migrator, gateway, Aspire
curl localhost:5100/demo/personas   # demo kişileri (yalnız Development)
TOKEN=$(curl -s localhost:5100/demo/token -H 'content-type: application/json' -d '{"persona":"deniz"}' | jq -r .token)
curl localhost:5100/api/v1/ask -H "authorization: Bearer $TOKEN" -H 'content-type: application/json' \
     -d '{"question":"Yıllık izin kaç gün?"}'
```

Hafif mod: `Database__Provider=Sqlite` ile Docker'sız çalışır (vektör aramasında SQL Server yolunun yerine yavaş bir yol kullanılır).
Kubernetes: `deploy/helm/sentinel` (kubeconform ile doğrulandı).

## Testler

```bash
dotnet test Sentinel.slnx            # Testcontainers için Docker gerekir
```

Birim, mimari, Testcontainers (SQL Server 2025 vektör + ledger, Redis 8) ve gateway entegrasyon testleri. Docker yoksa
testler **sessizce atlanmaz, gürültülü başarısız olur**.

## Belgeler

- [Mimari](docs/architecture.md)
- [Tehdit modeli](docs/threat-model.md)
- [Ölçümler ve sınırları](docs/evals.md)
- Mimari kararlar: [docs/adr](docs/adr)
- [Değerlendirme betikleri](eval/README.md)

## Bilinen sınırlar (dürüst liste)

- Ölçümler **küçük yerel modellerle, CPU üzerinde** yapıldı (qwen2.5:0.5b, all-minilm:l6-v2). Üretim modelleriyle sonuçlar değişir.
- PII ve injection veri kümeleri **sentetik veya elle yazılmış**; gerçek trafik değil. PII sonuçları eğitim-içi (kurallar aynı kişilerce yazıldı).
- Entra ID modu gerçek bir tenant'a karşı denenmedi.
- Semantik önbellek, bu gömme modeliyle paraphrase ile yakın-yanlış soruyu güvenilir ayıramıyor; varsayılan eşik güvenli ama neredeyse hiç isabet vermiyor.
- Oracle desteklenmiyor (yol haritasında).
