# ADR-0005: Hash zinciri + SQL ledger

**Karar:** Her tenant için SHA-256 hash zinciri uygulama katmanında, ek olarak SQL Server ledger (append-only) tablosunda tutulur.
`/audit/verify` zinciri baştan hesaplar, ilk kırılan sırayı ve ledger özetini döner. Ekleme tenant başına serileştirilir.

**Neden:** Uygulama zinciri veritabanından bağımsız doğrulanabilir; ledger veritabanı yöneticisinin sessiz değişikliğini zorlaştırır.
**Sınır:** Zincirin kuyruğunu kesmek, özet dışarıya (ör. imzalı zaman damgası) demirlenmedikçe fark edilmez.
