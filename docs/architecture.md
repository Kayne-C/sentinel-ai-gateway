# Mimari

## Katmanlar

```
Sentinel.Domain          Saf iş kuralları: belge, ACL, sınıflandırma, denetim girdisi, hata/Result tipleri
Sentinel.Guardrails      PII tanıyıcıları + redactor + çıktı süzgeci, injection detektörleri (bağımlılığı yok)
Sentinel.Application     Kullanım senaryoları (Ask, belge yönetimi, proxy), portlar, davranışlar (doğrulama, telemetri)
Sentinel.Infrastructure  EF Core (SQL Server 2025 vektör / SQLite), Redis önbellek + bütçe, MEAI sağlayıcıları, denetim
Sentinel.SemanticKernel  Kernel filtreleri ve izin duyarlı KnowledgePlugin
Sentinel.Gateway         ASP.NET Core: kimlik, uç noktalar, YARP veri düzlemi, demo uç noktaları
Sentinel.Migrations.SqlServer  Ayrı migration derlemesi (ledger tablosu dahil)
```

Bağımlılık yönü içe doğrudur ve `Sentinel.ArchitectureTests` ile test edilir. Mediator, Result/Error ve doğrulama
pipeline'ı kütüphanesiz, uygulamanın içinde yazılmıştır.

## Soru-cevap akışı (`POST /api/v1/ask`)

1. **Kimlik:** JWT doğrulanır; `tid` tenant, `oid` kullanıcı, `groups` + `roles` prensip kümesini oluşturur.
   Grup taşması (`_claim_names`) görülürse istek **reddedilir** (kapalı başarısız).
2. **Girdi koruması:** PII maskelenir (yer tutucu + kasa). Detektörler yalnızca **maskelenmiş** metni görür.
3. **Bütçe:** tenant (aylık) ve kullanıcı (günlük) token bütçesinden atomik rezervasyon.
4. **Önbellek:** tenant + ad alanı + model katmanı kapsamında semantik arama. İsabet, kaynak kümesi
   (belge sürümleri, ACL) yeniden doğrulanarak servis edilir.
5. **Erişim:** soru gömülür; vektör sorgusu `tenant = @t AND ACL ∩ prensipler ≠ ∅` koşuluyla **aynı sorguda** çalışır.
6. **İçerik taraması:** getirilen parçalar injection için taranır, şüpheliler bağlamdan çıkarılır.
7. **Yönlendirme:** istem + bağlam karmaşıklığı `fast` veya `reasoning` katmanını seçer.
8. **Model çağrısı:** Microsoft.Extensions.AI + Polly dayanıklılık, token üst sınırı katman ayarından.
9. **Çıktı koruması:** akış üzerinde de çalışan çıktı süzgeci; yalnızca çağıranın kendi PII değerleri geri konur.
10. **Denetim:** tenant hash zincirine eklenir (hash = SHA-256(önceki hash ‖ kanonik içerik)), SQL Server'da ledger tablosuna da yazılır.
    Ham içerik yazılmaz; metrikler ve kaynak kimlikleri yazılır.

## Veri düzlemi (`/v1/chat/completions`)

OpenAI uyumlu istemcilerin kodunu değiştirmeden geçebilmesi için YARP `IHttpForwarder` kullanılır. Ancak istek **olduğu gibi
iletilmez**: izin listesindeki alanlardan yeniden kurulur, yanıt da yeniden kurulur. Bilinmeyen alanlar (ör. `tools`, `functions`)
düşer. YARP gövdeyi değiştirmeye izin vermediği için iletici özel bir `HttpContent` ile beslenir.

## Depolama

- **SQL Server 2025:** `vector(384)` sütunu, `VECTOR_DISTANCE('cosine', ...)` ile tam arama, ACL tablosu ve ledger (append-only) denetim tablosu.
- **Redis 8:** semantik önbellek (HNSW + `HYBRID_POLICY ADHOC_BF`) ve token bütçe sayaçları.
- **SQLite:** yalnız geliştirme/test, vektör araması bellek içi hesaplanır.

## Dağıtım

`docker-compose.yml` (geliştirme), `deploy/helm/sentinel` (Kubernetes: HPA, PDB, NetworkPolicy, güvenli varsayılanlar),
`.github/workflows/ci.yml` (derleme, test, Helm şablon + kubeconform).
