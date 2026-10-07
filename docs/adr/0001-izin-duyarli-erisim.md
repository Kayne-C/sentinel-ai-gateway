# ADR-0001: ACL, vektör sorgusunun içinde filtrelenir

**Durum:** Kabul. **Bağlam:** Yetkisiz belge parçası modele bir kez bile girerse prompt ile dışarı çıkarılabilir.

**Karar:** Tenant ve ACL koşulu, benzerlik sıralamasıyla **aynı SQL sorgusunda** (ön filtre) uygulanır. Arama tam KNN'dir
(`VECTOR_DISTANCE`), ANN değildir. Çünkü ANN + sonradan filtre, az sayıda yetkili belge olduğunda sonuç kaçırır, ön filtreli ANN ise
motora bağlıdır. Bu ölçekte (belge başına yüzlerce parça) tam arama yeterince hızlıdır.

**Sonuçlar:** Güvenlik özelliği model davranışına bağlı değildir; detektörler kapalıyken bile red team'de 0 sızıntı.
Maliyet: çok büyük korpuslarda tam arama ölçeklenmez; o noktada ACL-bölümlü ANN indeksi gerekir.
