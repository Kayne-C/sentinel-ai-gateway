# Tehdit modeli

Varlıklar: tenant belgeleri (ACL'li), kullanıcıların kişisel verisi, model bütçesi, denetim kaydı.

| # | Tehdit | Savunma | Kalan risk |
|---|---|---|---|
| T1 | Yetkisiz kullanıcı başka birinin belgesinden bilgi alır (prompt ile ikna, numaralandırma, rol yapma) | ACL vektör sorgusunun içinde; yetkisiz parça modele hiç girmez. Denetim kaydında kaynak yetki dışı mı diye ayrıca taranır | Ölçüldü: 0/493, 0/171 (detektörsüz). Sıfır ispat değildir |
| T2 | Önbellek üzerinden sızıntı (A'nın cevabı B'ye) | Önbellek tenant + model katmanı kapsamlı; isabette kaynak kümesi çağıranın yetkisiyle yeniden doğrulanır; belge/ACL değişince girdiler silinir | Kapsam dışı bir anahtar bileşeni eklenirse bozulur; testle korunur |
| T3 | Dolaylı injection (belgeye gömülü talimat) | Getirilen parçalar taranır; şüpheli parça bağlamdan çıkar; belge alımında da karantina | Tespit zayıf (recall ~%60); asıl sınır T1: modelin ele geçirilmesi yetkisiz veri getirmez |
| T4 | PII modele, loglara, çıktıya sızar | Girdi maskeleme, kasa, logda içerik yok, çıktı süzgeci (akışta da) | Regex kapsamı dışı PII (adlar, adresler, yazıyla yazılmış sayılar) |
| T5 | Kasa üzerinden başkasının PII'sı geri konur | Yer tutucu yalnız çağıranın kendi isteğinden gelen değerler için geri çevrilir | — |
| T6 | Denetim kaydı sessizce değiştirilir | Hash zinciri + SQL ledger; `/audit/verify` kırılan ilk sırayı bildirir | Zincirin sonunu kesen saldırgan: ledger özeti dışarıya demirlenmeli (yol haritası) |
| T7 | Proxy üzerinden filtre atlatma | Katı izin listesi, yeniden kurulan istek/yanıt, bilinmeyen alan düşer, araç çağrısı yok | Yeni OpenAI alanları bilinçli eklenene kadar çalışmaz (istenen davranış) |
| T8 | Bütçe tüketme / maliyet saldırısı | Atomik rezervasyon, token üst sınırı, hız sınırlama | Dağıtık hız sınırı yok (tek örnek) |
| T9 | Token sahteciliği / grup taşması | Üretimde Entra imza doğrulaması; taşmada reddet. Dev token yalnız Development modunda | Gerçek tenant ile denenmedi |
| T10 | Hata mesajlarından bilgi sızması | RFC 9457 problem details, içerik yok | — |

Kapsam dışı: model sağlayıcının kendisine güven, ağ katmanı (mTLS), çok bölgeli kopyalama, kötü niyetli yönetici (Sentinel.Admin rolü).
