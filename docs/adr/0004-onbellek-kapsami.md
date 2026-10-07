# ADR-0004: Semantik önbellek kapsamı ve geçersiz kılma

**Karar:** Anahtar = tenant + ad alanı + model katmanı. İsabette cevabın kaynak belge sürümleri ve ACL'si çağıran için yeniden
doğrulanır. Belge veya ACL değişince, o belgeyi kaynak gösteren girdiler (kümeye dayalı indeks) silinir.

**Neden:** Önbellek, ACL'yi atlatmanın en kolay yoludur. **Dürüst not:** ölçümde MiniLM gömmesi Türkçe'de paraphrase ile yakın-yanlışı
ayıramadı (medyan 0,780 / 0,772), bu yüzden güvenli varsayılan eşik (0,92) neredeyse hiç isabet vermiyor. Çoklu dilli gömme gerekli.
