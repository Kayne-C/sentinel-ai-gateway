{{- define "sentinel.fullname" -}}
{{- printf "%s" .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "sentinel.labels" -}}
app.kubernetes.io/name: sentinel
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end -}}

{{- define "sentinel.selectorLabels" -}}
app.kubernetes.io/name: sentinel
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "sentinel.image" -}}
{{ .Values.image.repository }}:{{ .Values.image.tag | default .Chart.AppVersion }}
{{- end -}}

{{/* Non-secret settings and secret references shared by the gateway and the migration job. */}}
{{- define "sentinel.env" -}}
- { name: ASPNETCORE_ENVIRONMENT, value: Production }
- { name: Auth__Mode, value: {{ .Values.auth.mode | quote }} }
- { name: Auth__Entra__TenantId, value: {{ .Values.auth.tenantId | quote }} }
- { name: Auth__Entra__ClientId, value: {{ required "auth.clientId is required" .Values.auth.clientId | quote }} }
{{- range $i, $audience := .Values.auth.audiences }}
- { name: Auth__Entra__Audiences__{{ $i }}, value: {{ $audience | quote }} }
{{- end }}
{{- range $i, $tenant := .Values.auth.allowedTenants }}
- { name: Auth__AllowedTenants__{{ $i }}, value: {{ $tenant | quote }} }
{{- end }}
- { name: Database__Provider, value: SqlServer }
- name: Database__ConnectionString
  valueFrom: { secretKeyRef: { name: {{ required "secrets.existingSecret is required" .Values.secrets.existingSecret }}, key: DATABASE_CONNECTION_STRING } }
- { name: SemanticCache__Provider, value: Redis }
- { name: Budgets__Provider, value: Redis }
- name: ConnectionStrings__Redis
  valueFrom: { secretKeyRef: { name: {{ .Values.secrets.existingSecret }}, key: REDIS_CONNECTION_STRING } }
- { name: Ai__Provider, value: {{ .Values.ai.provider | quote }} }
- { name: Ai__Endpoint, value: {{ required "ai.endpoint is required" .Values.ai.endpoint | quote }} }
- name: Ai__ApiKey
  valueFrom: { secretKeyRef: { name: {{ .Values.secrets.existingSecret }}, key: AI_API_KEY } }
- { name: Ai__EmbeddingModel, value: {{ .Values.ai.embeddingModel | quote }} }
- { name: Ai__SendEmbeddingDimensions, value: {{ .Values.ai.sendEmbeddingDimensions | quote }} }
- { name: Guardrails__Injection__Learned__Enabled, value: {{ .Values.ai.learnedInjection | quote }} }
{{- if .Values.ai.contentSafety.enabled }}
- { name: Ai__ContentSafety__Enabled, value: "true" }
- { name: Ai__ContentSafety__Endpoint, value: {{ required "ai.contentSafety.endpoint is required" .Values.ai.contentSafety.endpoint | quote }} }
- name: Ai__ContentSafety__ApiKey
  valueFrom: { secretKeyRef: { name: {{ .Values.secrets.existingSecret }}, key: CONTENT_SAFETY_API_KEY } }
{{- end }}
- { name: Models__DefaultTier, value: {{ .Values.models.defaultTier | quote }} }
{{- range $name, $tier := .Values.models.tiers }}
- { name: Models__Tiers__{{ $name }}__Model, value: {{ $tier.model | quote }} }
- { name: Models__Tiers__{{ $name }}__InputPricePer1MTokens, value: {{ $tier.inputPricePer1MTokens | quote }} }
- { name: Models__Tiers__{{ $name }}__OutputPricePer1MTokens, value: {{ $tier.outputPricePer1MTokens | quote }} }
- { name: Models__Tiers__{{ $name }}__MaxOutputTokens, value: {{ $tier.maxOutputTokens | quote }} }
{{- end }}
- { name: Budgets__DefaultTenantMonthlyTokens, value: {{ .Values.budgets.tenantMonthlyTokens | quote }} }
- { name: Budgets__DefaultSubjectDailyTokens, value: {{ .Values.budgets.subjectDailyTokens | quote }} }
- { name: Rag__TopK, value: {{ .Values.rag.topK | quote }} }
- { name: Rag__MinSimilarity, value: {{ .Values.rag.minSimilarity | quote }} }
- { name: SemanticCache__SimilarityThreshold, value: {{ .Values.semanticCache.similarityThreshold | quote }} }
- { name: SemanticCache__Ttl, value: {{ .Values.semanticCache.ttl | quote }} }
- { name: Audit__StoreRedactedPrompts, value: {{ .Values.audit.storeRedactedPrompts | quote }} }
- { name: RateLimiting__RequestsPerSecondPerSubject, value: {{ .Values.rateLimiting.requestsPerSecondPerSubject | quote }} }
- { name: RateLimiting__Burst, value: {{ .Values.rateLimiting.burst | quote }} }
{{- if .Values.observability.otlpEndpoint }}
- { name: OTEL_EXPORTER_OTLP_ENDPOINT, value: {{ .Values.observability.otlpEndpoint | quote }} }
{{- end }}
{{- end -}}

{{/* Container hardening shared by every container of the release. */}}
{{- define "sentinel.containerSecurityContext" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: true
runAsNonRoot: true
runAsUser: 1654
capabilities: { drop: [ALL] }
seccompProfile: { type: RuntimeDefault }
{{- end -}}
