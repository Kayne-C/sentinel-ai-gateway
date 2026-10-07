# ADR-0002: Guardrail katmanlaması

**Karar:** Savunma sırası: (1) ACL (kesin), (2) PII maskeleme (deterministik), (3) injection tespiti (olasılıksal),
(4) çıktı süzgeci. Detektörler **yalnız maskelenmiş** metni görür ve ham PII'ı hiçbir yere taşımaz.

**Neden:** Olasılıksal bileşen güvenlik sınırı olamaz. Ölçüm bunu gösterdi: kurallar deepset held-out'ta %6,7, öğrenilmiş katman %60 recall.
Bu yüzden injection tespiti "ek" katmandır, sızıntı garantisi ACL'den gelir.

**Wrapper/çift tarama:** Semantic Kernel filtresi ile gateway aynı metni iki kez taramasın diye metadata işaretçisi (`pre-guarded`) kullanılır.
