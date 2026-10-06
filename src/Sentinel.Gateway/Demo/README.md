# Demo data

`corpus.json` is a fictional knowledge base for two tenants (Contoso, Fabrikam). Every restricted document carries a
unique `CANARY-…` token: the red-team evaluation (`eval/`) asserts that no canary ever reaches a caller — or the model —
whose ACL does not include that document. `tedarikci-listesi` deliberately contains an indirect prompt injection and
must be quarantined at ingestion.

All names, numbers, IBANs and identity numbers are synthetic (checksum-valid where the PII tests need it).
