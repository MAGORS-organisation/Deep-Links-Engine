{{/*
=============================================================================
Deep Link Engine (DLE) — named templates shared by every manifest in this chart.

Naming    dle.name, dle.fullname, dle.chart, dle.edge.fullname, dle.control.fullname
Labels    dle.labels / dle.selectorLabels (release-wide) and the per-component variants
          dle.edge.*, dle.control.*, dle.migrate.* (component label added)
Images    dle.image.edge, dle.image.control — repository:tag, tag defaulting to appVersion
Secrets   dle.secretName (existingSecret or the rendered one), dle.migrate.secretName
Config    dle.env — ONE environment entry from an ASP.NET Core configuration key:
            {{ include "dle.env" (dict "key" "Dle:Edge:Cache:L1Seconds" "value" 30) }}
          renders   Dle__Edge__Cache__L1Seconds: "30"   (colon → double underscore).
          dle.envMap — the same for a whole map (config.extra & co.)
Network   dle.networkPolicy.peers, .postgresPeers, .valkeyPeers, .clickHousePeers
=============================================================================
*/}}

{{/* ---- naming ------------------------------------------------------------------------------ */}}

{{- define "dle.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "dle.fullname" -}}
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

{{- define "dle.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "dle.edge.fullname" -}}
{{- printf "%s-edge" (include "dle.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "dle.control.fullname" -}}
{{- printf "%s-control" (include "dle.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "dle.migrate.fullname" -}}
{{- printf "%s-migrate" (include "dle.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "dle.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "dle.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{/* ---- labels ------------------------------------------------------------------------------ */}}

{{- define "dle.labels" -}}
helm.sh/chart: {{ include "dle.chart" . }}
app.kubernetes.io/name: {{ include "dle.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: dle
{{- with .Values.commonLabels }}
{{ toYaml . }}
{{- end }}
{{- end -}}

{{- define "dle.selectorLabels" -}}
app.kubernetes.io/name: {{ include "dle.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "dle.edge.labels" -}}
{{ include "dle.labels" . }}
app.kubernetes.io/component: edge
{{- end -}}

{{- define "dle.edge.selectorLabels" -}}
{{ include "dle.selectorLabels" . }}
app.kubernetes.io/component: edge
{{- end -}}

{{- define "dle.control.labels" -}}
{{ include "dle.labels" . }}
app.kubernetes.io/component: control
{{- end -}}

{{- define "dle.control.selectorLabels" -}}
{{ include "dle.selectorLabels" . }}
app.kubernetes.io/component: control
{{- end -}}

{{- define "dle.migrate.labels" -}}
{{ include "dle.labels" . }}
app.kubernetes.io/component: migrate
{{- end -}}

{{- define "dle.migrate.selectorLabels" -}}
{{ include "dle.selectorLabels" . }}
app.kubernetes.io/component: migrate
{{- end -}}

{{/* ---- images ------------------------------------------------------------------------------ */}}

{{- define "dle.image.edge" -}}
{{- printf "%s:%s" .Values.image.edge.repository (default .Chart.AppVersion .Values.image.edge.tag) -}}
{{- end -}}

{{- define "dle.image.control" -}}
{{- printf "%s:%s" .Values.image.control.repository (default .Chart.AppVersion .Values.image.control.tag) -}}
{{- end -}}

{{/* ---- secrets ----------------------------------------------------------------------------- */}}

{{/* The Secret the deployments read: the operator's own, or the one rendered by secret.yaml. */}}
{{- define "dle.secretName" -}}
{{- default (include "dle.fullname" .) .Values.secrets.existingSecret -}}
{{- end -}}

{{/*
The Secret the migration hook reads. Pre-install hooks run before regular resources exist, so
without an existingSecret the hook reads a hook-scoped copy (secret.yaml renders both).
*/}}
{{- define "dle.migrate.secretName" -}}
{{- default (include "dle.migrate.fullname" .) .Values.secrets.existingSecret -}}
{{- end -}}

{{/* Hook events for the migration Job (values.migrate.hooks, with the documented default). */}}
{{- define "dle.migrate.hooks" -}}
{{- if .Values.migrate.hooks -}}
{{- .Values.migrate.hooks -}}
{{- else if .Values.postgresql.enabled -}}
post-install,post-upgrade
{{- else -}}
pre-install,pre-upgrade
{{- end -}}
{{- end -}}

{{/* Bundled-subchart service names, as documented in values.yaml. */}}
{{- define "dle.postgresql.primaryHost" -}}
{{- if eq (toString .Values.postgresql.architecture) "replication" -}}
{{- printf "%s-postgresql-primary" .Release.Name -}}
{{- else -}}
{{- printf "%s-postgresql" .Release.Name -}}
{{- end -}}
{{- end -}}

{{- define "dle.postgresql.readHost" -}}
{{- if eq (toString .Values.postgresql.architecture) "replication" -}}
{{- printf "%s-postgresql-read" .Release.Name -}}
{{- end -}}
{{- end -}}

{{- define "dle.valkey.host" -}}
{{- printf "%s-valkey-primary" .Release.Name -}}
{{- end -}}

{{/* Connection strings, exactly the shape docker-compose.yml builds (Npgsql / StackExchange.Redis). */}}
{{- define "dle.postgres.connectionString" -}}
{{- if .Values.postgresql.enabled -}}
{{- required "postgresql.auth.password is required when postgresql.enabled=true (or set secrets.existingSecret)" .Values.postgresql.auth.password -}}
{{- printf "Host=%s;Port=5432;Database=%s;Username=%s;Password=%s" (include "dle.postgresql.primaryHost" .) .Values.postgresql.auth.database .Values.postgresql.auth.username .Values.postgresql.auth.password -}}
{{- else -}}
{{- required "externalPostgres.connectionString is required (or postgresql.enabled=true, or secrets.existingSecret)" .Values.externalPostgres.connectionString -}}
{{- end -}}
{{- end -}}

{{- define "dle.postgres.readConnectionString" -}}
{{- if .Values.postgresql.enabled -}}
{{- with (include "dle.postgresql.readHost" .) -}}
{{- printf "Host=%s;Port=5432;Database=%s;Username=%s;Password=%s" . $.Values.postgresql.auth.database $.Values.postgresql.auth.username $.Values.postgresql.auth.password -}}
{{- end -}}
{{- else -}}
{{- .Values.externalPostgres.readConnectionString -}}
{{- end -}}
{{- end -}}

{{- define "dle.valkey.connectionString" -}}
{{- if .Values.valkey.enabled -}}
{{- if .Values.valkey.auth.enabled -}}
{{- printf "%s:6379,password=%s" (include "dle.valkey.host" .) .Values.valkey.auth.password -}}
{{- else -}}
{{- printf "%s:6379" (include "dle.valkey.host" .) -}}
{{- end -}}
{{- else -}}
{{- .Values.externalValkey.connectionString -}}
{{- end -}}
{{- end -}}

{{/* The data block shared by the release Secret and its hook-scoped copy. */}}
{{- define "dle.secretData" -}}
{{ .Values.secrets.keys.masterSecret }}: {{ required "secrets.masterSecret is required (at least 32 characters) unless secrets.existingSecret is set" .Values.secrets.masterSecret | quote }}
{{ .Values.secrets.keys.postgresConnectionString }}: {{ include "dle.postgres.connectionString" . | quote }}
{{- with (include "dle.postgres.readConnectionString" .) }}
{{ $.Values.secrets.keys.postgresReadConnectionString }}: {{ . | quote }}
{{- end }}
{{- with (include "dle.valkey.connectionString" .) }}
{{ $.Values.secrets.keys.valkeyConnectionString }}: {{ . | quote }}
{{- end }}
{{- with .Values.externalClickHouse.connectionString }}
{{ $.Values.secrets.keys.clickHouseConnectionString }}: {{ . | quote }}
{{- end }}
{{- end -}}

{{/* ---- configuration → environment --------------------------------------------------------- */}}

{{/*
dle.env — one `NAME: "value"` line (with its own leading newline and indent, default 2) from a
Dle:* configuration key. Empty strings and nil render NOTHING so that the image's
appsettings.json default stays in force; booleans and numbers always render (false and 0 are
meaningful). Integral floats print without an exponent (YAML numbers arrive as float64).
*/}}
{{- define "dle.env" -}}
{{- $s := "" -}}
{{- if kindIs "bool" .value -}}
{{- $s = toString .value -}}
{{- else if kindIs "float64" .value -}}
{{- $s = ternary (toString (int64 .value)) (toString .value) (eq (floor .value) .value) -}}
{{- else if or (kindIs "int" .value) (kindIs "int64" .value) -}}
{{- $s = toString .value -}}
{{- else if .value -}}
{{- $s = toString .value -}}
{{- end -}}
{{- if $s -}}
{{- printf "\n%s%s: %s" (repeat (int (default 2 .indent)) " ") (replace ":" "__" .key) (quote $s) -}}
{{- end -}}
{{- end -}}

{{/* dle.envMap — dle.env for every entry of a map; keys may use either Dle:X or Dle__X. */}}
{{- define "dle.envMap" -}}
{{- $indent := .indent -}}
{{- range $k, $v := .map -}}
{{- include "dle.env" (dict "key" $k "value" $v "indent" $indent) -}}
{{- end -}}
{{- end -}}

{{/* Environment shared by edge and control (Dle:Privacy, Dle:Telemetry, config.extra). */}}
{{- define "dle.sharedEnv" -}}
{{- include "dle.env" (dict "key" "Dle:Privacy:ConsentMode" "value" .Values.config.privacy.consentMode) -}}
{{- include "dle.env" (dict "key" "Dle:Privacy:IpStorage" "value" .Values.config.privacy.ipStorage) -}}
{{- include "dle.env" (dict "key" "Dle:Telemetry:OtlpEndpoint" "value" .Values.config.telemetry.otlpEndpoint) -}}
{{- include "dle.envMap" (dict "map" .Values.config.extra) -}}
{{- end -}}

{{/* ---- network policy peers ---------------------------------------------------------------- */}}

{{/*
dle.networkPolicy.peers — a list of NetworkPolicyPeer entries from {cidrs, namespaceSelector,
podSelector}. CIDRs take precedence. Only the selectors that are set are emitted: an empty
`namespaceSelector: {}` would mean "every namespace", which is not what an omitted one means.
Renders nothing when nothing is configured (callers then fall back to "any destination").
*/}}
{{- define "dle.networkPolicy.peers" -}}
{{- if .cidrs -}}
{{- range .cidrs }}
- ipBlock:
    cidr: {{ . }}
{{- end }}
{{- else if or .namespaceSelector .podSelector -}}
{{- $peer := dict -}}
{{- with .namespaceSelector }}{{ $_ := set $peer "namespaceSelector" . }}{{ end -}}
{{- with .podSelector }}{{ $_ := set $peer "podSelector" . }}{{ end }}
- {{ toYaml $peer | nindent 2 | trim }}
{{- end -}}
{{- end -}}

{{/* PostgreSQL peers: configured CIDRs/selectors, else the bundled subchart's pods, else nothing. */}}
{{- define "dle.networkPolicy.postgresPeers" -}}
{{- $p := .Values.networkPolicy.postgres -}}
{{- if or $p.cidrs $p.namespaceSelector $p.podSelector -}}
{{- include "dle.networkPolicy.peers" (dict "cidrs" $p.cidrs "namespaceSelector" $p.namespaceSelector "podSelector" $p.podSelector) -}}
{{- else if .Values.postgresql.enabled }}
- podSelector:
    matchLabels:
      app.kubernetes.io/name: postgresql
      app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}
{{- end -}}

{{- define "dle.networkPolicy.valkeyPeers" -}}
{{- $p := .Values.networkPolicy.valkey -}}
{{- if or $p.cidrs $p.namespaceSelector $p.podSelector -}}
{{- include "dle.networkPolicy.peers" (dict "cidrs" $p.cidrs "namespaceSelector" $p.namespaceSelector "podSelector" $p.podSelector) -}}
{{- else if .Values.valkey.enabled }}
- podSelector:
    matchLabels:
      app.kubernetes.io/name: valkey
      app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}
{{- end -}}

{{- define "dle.networkPolicy.clickHousePeers" -}}
{{- $p := .Values.networkPolicy.clickhouse -}}
{{- include "dle.networkPolicy.peers" (dict "cidrs" $p.cidrs "namespaceSelector" $p.namespaceSelector "podSelector" $p.podSelector) -}}
{{- end -}}

{{/* ---- ingress ----------------------------------------------------------------------------- */}}

{{- define "dle.tlsSecretName" -}}
{{- default (printf "%s-tls" (include "dle.fullname" .)) .Values.ingress.tls.secretName -}}
{{- end -}}

{{/* cert-manager annotations, rendered on ONE Ingress per Secret (see ingress.yaml). */}}
{{- define "dle.certManagerAnnotations" -}}
{{- with .Values.ingress.tls.certManager.clusterIssuer }}
cert-manager.io/cluster-issuer: {{ . | quote }}
{{- end }}
{{- with .Values.ingress.tls.certManager.issuer }}
cert-manager.io/issuer: {{ . | quote }}
{{- end }}
{{- end -}}
