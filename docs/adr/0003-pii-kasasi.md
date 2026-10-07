# ADR-0003: PII yer tutucu + kasa

**Karar:** PII `[TCKN_1]` gibi yer tutucuyla değişir; gerçek değer isteğe özel kasada kalır. Yanıtta yalnızca **çağıranın kendi
isteğinde** bulunan değerler geri konur.

**Neden:** Modelin ürettiği veya belgeden gelen bir yer tutucu başka birinin verisini açığa çıkaramaz. Tam maskeleme (geri koymasız)
kullanıcı deneyimini bozar. **Maliyet:** kasa istek ömrüyle sınırlı; konuşma boyunca kalıcılık yok.
Doğrulamalı tanıyıcılar (TCKN algoritması, IBAN mod-97, Luhn) yanlış alarmı düşürür.
