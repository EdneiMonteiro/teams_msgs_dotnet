{{- define "teams-msgs.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "teams-msgs.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s" (include "teams-msgs.name" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end }}

{{- define "teams-msgs.labels" -}}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version | replace "+" "_" }}
app.kubernetes.io/name: {{ include "teams-msgs.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: teams-msgs
{{- end }}

{{- define "teams-msgs.api.labels" -}}
{{ include "teams-msgs.labels" . }}
app.kubernetes.io/component: api
{{- end }}

{{- define "teams-msgs.worker.labels" -}}
{{ include "teams-msgs.labels" . }}
app.kubernetes.io/component: worker
{{- end }}

{{- define "teams-msgs.api.selectorLabels" -}}
app.kubernetes.io/name: {{ include "teams-msgs.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/component: api
{{- end }}

{{- define "teams-msgs.worker.selectorLabels" -}}
app.kubernetes.io/name: {{ include "teams-msgs.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/component: worker
{{- end }}

{{- define "teams-msgs.api.image" -}}
{{- printf "%s/%sapi:%s" .Values.image.registry (default "" .Values.image.repository) .Values.image.apiTag -}}
{{- end }}

{{- define "teams-msgs.worker.image" -}}
{{- printf "%s/%sworker:%s" .Values.image.registry (default "" .Values.image.repository) .Values.image.workerTag -}}
{{- end }}
