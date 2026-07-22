{{/*
Expand the name of the chart.
*/}}
{{- define "b3-market-maker-bot.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
Create a default fully qualified app name. Truncated to 63 chars because
some Kubernetes name fields are limited to this (by the DNS naming spec).
*/}}
{{- define "b3-market-maker-bot.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/*
Common labels.
*/}}
{{- define "b3-market-maker-bot.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{ include "b3-market-maker-bot.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: b3-sim
{{- end -}}

{{/*
Selector labels. app.kubernetes.io/name is fixed to "market-maker-bot" (not
templated off nameOverride), matching the b3-trading-host/b3-trading-frontend
convention of a stable name for cross-chart NetworkPolicy podSelectors.
*/}}
{{- define "b3-market-maker-bot.selectorLabels" -}}
app.kubernetes.io/name: market-maker-bot
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{/*
Fully qualified image reference: repository@digest when a digest is pinned,
else repository:tag (tag defaults to .Chart.AppVersion).
*/}}
{{- define "b3-market-maker-bot.image" -}}
{{- if .Values.image.digest -}}
{{- printf "%s@%s" .Values.image.repository .Values.image.digest -}}
{{- else -}}
{{- printf "%s:%s" .Values.image.repository (.Values.image.tag | default .Chart.AppVersion) -}}
{{- end -}}
{{- end -}}
