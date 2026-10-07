# ADR-0006: YARP veri düzlemi ve katı izin listesi

**Karar:** OpenAI uyumlu uç `IHttpForwarder` ile sunulur (YARP'ın tam rota/küme modeli yerine), çünkü istek ve yanıtı **yeniden kurmamız** gerekir.
Yalnız izin listesindeki alanlar iletilir. Bilinmeyen alanlar düşer, `tools`/`functions` desteklenmez.

**Bilinenler:** YARP gövdeyi dönüştürmeye izin vermez, bu yüzden özel `HttpContent` kullanılır. Hata durumunda dönüştürücüye null yanıt gelir, bu da ele alınır.
**Maliyet:** Yeni OpenAI özellikleri bilinçli olarak eklenene kadar çalışmaz; bu güvenlik için istenen davranıştır.
