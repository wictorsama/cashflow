{{- define "cashflow.image" -}}
{{ .root.Values.image.registry }}{{ .name }}:{{ .root.Values.image.tag }}
{{- end -}}

{{- define "cashflow.labels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/part-of: cashflow
{{- end -}}

{{/*
  A highly available PostgreSQL (CloudNativePG): one primary and streaming replicas on other nodes, automatic failover.
  Called once per service database, so each service keeps its own database cluster. Requires the CloudNativePG operator.
  Applications connect to <name>-rw, which always points at the current primary.
*/}}
{{- define "cashflow.postgres" -}}
{{- $name := printf "postgres-%s" .db -}}
apiVersion: v1
kind: Secret
metadata:
  name: {{ $name }}-app
type: kubernetes.io/basic-auth
stringData:
  username: {{ .db }}
  password: {{ required "postgres.password is required" .root.Values.postgres.password | quote }}
---
apiVersion: postgresql.cnpg.io/v1
kind: Cluster
metadata:
  name: {{ $name }}
  labels: {{ include "cashflow.labels" (dict "name" $name) | nindent 4 }}
spec:
  instances: {{ .root.Values.postgres.instances }}
  imageName: {{ .root.Values.postgres.image }}
  bootstrap:
    initdb:
      database: {{ .db }}
      owner: {{ .db }}
      secret: { name: {{ $name }}-app }
  storage:
    size: {{ .root.Values.postgres.storage }}
  # A committed write is on a second node before it is acknowledged, so a failover loses nothing. If the replica is
  # unavailable the primary keeps accepting writes ("preferred"): availability of the entries comes first.
  {{- if gt (int .root.Values.postgres.instances) 1 }}
  postgresql:
    synchronous:
      method: any
      number: 1
      dataDurability: preferred
  {{- end }}
  # Replicas never share a node with the primary.
  affinity:
    enablePodAntiAffinity: true
    podAntiAffinityType: required
    topologyKey: kubernetes.io/hostname
  resources: {{ toYaml .root.Values.postgres.resources | nindent 4 }}
{{- end -}}

{{/* A stateless .NET service: Deployment, Service, PodDisruptionBudget and optional HPA. */}}
{{- define "cashflow.service" -}}
{{- $name := .name -}}
{{- $v := .values -}}
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ $name }}
  labels: {{ include "cashflow.labels" (dict "name" $name) | nindent 4 }}
spec:
  {{- if not $v.hpa.enabled }}
  replicas: {{ $v.replicas }}
  {{- end }}
  strategy:
    type: RollingUpdate
    rollingUpdate: { maxUnavailable: 0, maxSurge: 1 }
  selector:
    matchLabels: { app.kubernetes.io/name: {{ $name }} }
  template:
    metadata:
      labels: {{ include "cashflow.labels" (dict "name" $name) | nindent 8 }}
    spec:
      # Spread replicas across nodes: losing one node never takes the service down.
      topologySpreadConstraints:
        - maxSkew: 1
          topologyKey: kubernetes.io/hostname
          whenUnsatisfiable: ScheduleAnyway
          labelSelector:
            matchLabels: { app.kubernetes.io/name: {{ $name }} }
      securityContext: { runAsNonRoot: true, seccompProfile: { type: RuntimeDefault } }
      # The API migrates its database before it starts listening, so wait for PostgreSQL instead of crash-looping.
      {{- if .db }}
      initContainers:
        - name: wait-for-postgres
          image: {{ .root.Values.postgres.image }}
          command: ["sh", "-c", "until pg_isready -h {{ include "cashflow.pghost" (dict "root" .root "db" .db) }} -p {{ include "cashflow.pgport" (dict "root" .root "db" .db) }}; do echo waiting for the database; sleep 2; done"]
          securityContext:
            runAsUser: 26   # the postgres user of the CloudNativePG image
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            capabilities: { drop: ["ALL"] }
          resources:
            requests: { cpu: 10m, memory: 16Mi }
      {{- end }}
      containers:
        - name: {{ $name }}
          image: {{ include "cashflow.image" (dict "root" .root "name" $name) }}
          imagePullPolicy: {{ .root.Values.image.pullPolicy }}
          ports: [{ name: http, containerPort: 8080 }]
          # Keep serving for a few seconds after the pod leaves the Service: the endpoint removal takes a moment to
          # reach every proxy, and requests sent in that window would otherwise hit a pod that already stopped.
          lifecycle:
            preStop:
              exec: { command: ["sleep", "5"] }
          env:
            # $(VAR) references only expand variables declared earlier in this list.
            {{- if .db }}
            - name: POSTGRES_PASSWORD
              valueFrom: { secretKeyRef: { name: cashflow-secrets, key: postgres-password } }
            - name: RABBITMQ_PASSWORD
              valueFrom: { secretKeyRef: { name: cashflow-secrets, key: rabbitmq-password } }
            - { name: Database__MigrateOnStartup, value: "true" }
            {{- end }}
            - { name: Auth__MetadataAddress, value: {{ .root.Values.auth.metadataAddress | quote }} }
            - { name: Auth__Issuer, value: {{ .root.Values.auth.issuer | quote }} }
            - { name: Auth__Audience, value: {{ .root.Values.auth.audience | quote }} }
            - { name: Auth__RequireHttpsMetadata, value: {{ .root.Values.auth.requireHttpsMetadata | quote }} }
            {{- if .root.Values.observability.enabled }}
            - { name: Otel__TracesEndpoint, value: "http://jaeger:4317" }
            - { name: Otel__MetricsEndpoint, value: "http://prometheus:9090/api/v1/otlp/v1/metrics" }
            {{- end }}
            {{- if .db }}
            - name: ConnectionStrings__Postgres
              value: {{ include "cashflow.pgconn" (dict "root" .root "db" .db) | quote }}
            - name: RabbitMq__Uri
              value: {{ include "cashflow.amqpuri" .root | quote }}
            {{- end }}
            {{- if .assistant }}
            - { name: Assistant__Provider, value: {{ .root.Values.assistant.provider | quote }} }
            - { name: Assistant__RequestsPerMinute, value: {{ .root.Values.assistant.requestsPerMinute | quote }} }
            - { name: Assistant__Claude__Model, value: {{ .root.Values.assistant.claude.model | quote }} }
            # Only read when the provider is "claude"; absent otherwise, and the service then runs with the rules.
            - name: Assistant__Claude__ApiKey
              valueFrom: { secretKeyRef: { name: cashflow-secrets, key: anthropic-api-key, optional: true } }
            {{- end }}
            {{- if .reconciliation }}
            - { name: Reconciliation__Enabled, value: "true" }
            - { name: Reconciliation__EntriesUrl, value: "http://entries" }
            - { name: Reconciliation__TokenUrl, value: {{ .root.Values.auth.tokenUrl | quote }} }
            - { name: Reconciliation__ClientId, value: reconciler }
            - name: Reconciliation__ClientSecret
              valueFrom: { secretKeyRef: { name: cashflow-secrets, key: reconciler-secret } }
            - { name: Reconciliation__IntervalMinutes, value: {{ .root.Values.reconciliation.intervalMinutes | quote }} }
            - { name: Reconciliation__WindowDays, value: {{ .root.Values.reconciliation.windowDays | quote }} }
            - { name: Reconciliation__GraceSeconds, value: {{ .root.Values.reconciliation.graceSeconds | quote }} }
            {{- end }}
            {{- if .redis }}
            {{- if .root.Values.external.redis.enabled }}
            - name: Redis__ConnectionString
              valueFrom: { secretKeyRef: { name: cashflow-secrets, key: redis-connection-string } }
            {{- else }}
            - { name: Redis__ConnectionString, value: "redis:6379" }
            {{- end }}
            {{- end }}
          startupProbe:
            httpGet: { path: /health, port: http }
            failureThreshold: 30
            periodSeconds: 2
          readinessProbe:
            httpGet: { path: /health/ready, port: http }
            periodSeconds: 5
          livenessProbe:
            httpGet: { path: /health/live, port: http }
            periodSeconds: 10
            timeoutSeconds: 5
            failureThreshold: 6
          resources: {{ toYaml $v.resources | nindent 12 }}
          securityContext:
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            capabilities: { drop: ["ALL"] }
          volumeMounts:
            - { name: tmp, mountPath: /tmp }
      volumes:
        - { name: tmp, emptyDir: {} }
---
apiVersion: v1
kind: Service
metadata:
  name: {{ $name }}
spec:
  {{- if $v.nodePort }}
  type: NodePort
  {{- end }}
  selector: { app.kubernetes.io/name: {{ $name }} }
  ports:
    - name: http
      port: 80
      targetPort: http
      {{- if $v.nodePort }}
      nodePort: {{ $v.nodePort }}
      {{- end }}
---
apiVersion: policy/v1
kind: PodDisruptionBudget
metadata:
  name: {{ $name }}
spec:
  minAvailable: 1
  selector:
    matchLabels: { app.kubernetes.io/name: {{ $name }} }
{{- if $v.hpa.enabled }}
---
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: {{ $name }}
spec:
  scaleTargetRef: { apiVersion: apps/v1, kind: Deployment, name: {{ $name }} }
  minReplicas: {{ $v.hpa.minReplicas }}
  maxReplicas: {{ $v.hpa.maxReplicas }}
  metrics:
    - type: Resource
      resource:
        name: cpu
        target: { type: Utilization, averageUtilization: {{ $v.hpa.cpuTarget }} }
{{- end }}
{{- end -}}

{{/* Where a service finds its database: the in-cluster CloudNativePG service, or a managed database when external.postgres is on. */}}
{{- define "cashflow.pghost" -}}
{{- if .root.Values.external.postgres.enabled -}}{{ (index .root.Values.external.postgres .db).host }}{{- else -}}postgres-{{ .db }}-rw{{- end -}}
{{- end -}}

{{- define "cashflow.pgport" -}}
{{- if .root.Values.external.postgres.enabled -}}{{ (index .root.Values.external.postgres .db).port | default 5432 }}{{- else -}}5432{{- end -}}
{{- end -}}

{{- define "cashflow.pgconn" -}}
{{- if .root.Values.external.postgres.enabled -}}
{{- $d := index .root.Values.external.postgres .db -}}
Host={{ $d.host }};Port={{ $d.port | default 5432 }};Database={{ $d.database | default .db }};Username={{ $d.username | default .db }};Password=$(POSTGRES_PASSWORD);SSL Mode={{ .root.Values.external.postgres.sslMode }}
{{- else -}}
Host=postgres-{{ .db }}-rw;Database={{ .db }};Username={{ .db }};Password=$(POSTGRES_PASSWORD)
{{- end -}}
{{- end -}}

{{- define "cashflow.amqpuri" -}}
{{- if .Values.external.rabbitmq.enabled -}}
{{ .Values.external.rabbitmq.scheme }}://{{ .Values.external.rabbitmq.user }}:$(RABBITMQ_PASSWORD)@{{ .Values.external.rabbitmq.host }}:{{ .Values.external.rabbitmq.port }}/{{ .Values.external.rabbitmq.vhost }}
{{- else -}}
amqp://{{ .Values.rabbitmq.user }}:$(RABBITMQ_PASSWORD)@rabbitmq:5672/
{{- end -}}
{{- end -}}
