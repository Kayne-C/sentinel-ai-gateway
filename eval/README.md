# Değerlendirme betikleri

`eval/Sentinel.Evals` bir konsol uygulamasıdır. Çıktılar `eval/out/` (git'te yok), özetleri `eval/results/` altına kopyalanıp commit edilir.

```bash
dotnet run --project eval/Sentinel.Evals -- pii                 # gateway gerekmez
dotnet run --project eval/Sentinel.Evals -- injection           # gateway gerekmez; deepset'i indirir
# Aşağıdakiler çalışan bir gateway ister (localhost:5100, Development, Redis + SQL Server + Ollama)
dotnet run --project eval/Sentinel.Evals -- redteam --name=redteam-learned-on [--max=200]
dotnet run --project eval/Sentinel.Evals -- cache
dotnet run --project eval/Sentinel.Evals -- overhead
```

Detektörsüz ablasyon için gateway'i `Guardrails__Injection__Enabled=false` ile başlatın.

## Veri kümeleri

| Küme | Kaynak | Not |
|---|---|---|
| PII | Tohumlu üretici (`PiiEval.cs`) | Sentetik, tohum sonuç dosyasında |
| Injection (kendi) | El yazısı TR/EN örnekler (`InjectionEval.cs`) | Eğitimde %80, held-out %20 |
| Injection (dış) | `deepset/prompt-injections` (CC BY 4.0) | **İndirilir, commit edilmez**; test bölümü eğitime girmez |
| Red team | `src/Sentinel.Gateway/Demo/corpus.json` | Kişiler, belgeler, canary kodları |

`eval/train/train_injection_classifier.py` öğrenilmiş sınıflandırıcıyı yeniden eğitir (Ollama gömmeleri + scikit-learn). Çıktı,
`src/Sentinel.Infrastructure/AI/Learned/injection-model.json`; örneklerin SHA-256 özeti model dosyasına yazılır.

Sonuçların yorumu ve sınırları: [docs/evals.md](../docs/evals.md).
