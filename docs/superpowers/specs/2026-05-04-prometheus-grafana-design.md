# Prometheus + Grafana Integration — Design

**Date:** 2026-05-04
**Status:** Draft — pending user review
**Scope:** Add an opt-in Prometheus + Grafana observability stack to the Online Boutique microservices demo, packaged as a kustomize component and a Helm chart values flag.

## 1. Goals & Non-Goals

### Goals

- Provide a `kubectl apply` (kustomize) and `helm install` (Helm) path that stands up Prometheus + Grafana in-cluster with pre-provisioned dashboards.
- Surface per-service RED metrics (Rate, Errors, Duration) for the 10 traced services without modifying any service code.
- Surface basic pod health (restarts, readiness) for all 11 services.
- Match the existing repo conventions (mirror the `google-cloud-operations` component shape and the dual kustomize + Helm distribution).
- Target a local demo workflow: kind / minikube / Docker Desktop, accessed via `kubectl port-forward`.

### Non-Goals

- Production-grade Prometheus (no PVC, no HA, no Alertmanager, no remote_write).
- Native Prometheus client-library instrumentation in each service (deferred — current design derives metrics from existing OTel traces).
- TLS / authn / authz for Prometheus or Grafana (anonymous-access demo mode only).
- Long-term metric retention (default 6h, in `emptyDir`).
- Coexistence with `google-cloud-operations` in the same install (mutually exclusive — documented).

## 2. Architecture

```
                  ┌─────────────┐
                  │  Services   │  (10 services emit traces; ENABLE_TRACING=1)
                  │  emit OTLP  │
                  │   traces    │
                  └──────┬──────┘
                         │ OTLP/gRPC :4317
                         ▼
              ┌──────────────────────┐
              │  OTel Collector      │
              │  ┌────────────────┐  │
              │  │ spanmetrics    │  │  derives RED metrics
              │  │ connector      │  │  from traces
              │  └────────────────┘  │
              │  prometheus exporter │  /metrics on :9464
              └──────────┬───────────┘
                         │ Prometheus scrape
                         ▼
       ┌─────────────────────────────────────┐
       │           Prometheus                │  scrapes:
       │  (Deployment, emptyDir, 6h ret.)    │   • OTel collector :9464
       └─────────────────┬───────────────────┘   • kube-state-metrics :8080
                         │                        • annotated pods (k8s SD)
                         ▼
             ┌────────────────────┐
             │      Grafana       │  • anonymous Viewer
             │  • datasource      │  • 2 dashboards provisioned
             │  • dashboards      │  • access via port-forward :3000
             └────────────────────┘
```

**Core idea:** zero service code changes. The OTel Collector's `spanmetrics` connector synthesizes per-service RED metrics from existing trace data and exposes them on a Prometheus-scrapable endpoint. Combined with `kube-state-metrics`, this gives a real demo dashboard fast.

**Mutual exclusivity with `google-cloud-operations`:** this component ships its own OTel Collector config (Prometheus exporter only, no `googlecloud` exporter). Users pick *either* `prometheus-grafana` *or* `google-cloud-operations`, not both. The component README and the Helm `values.yaml` comment will state this explicitly.

## 3. Components

### 3.1 OTel Collector (replaces the one in `kustomize/components/google-cloud-operations`)

Image: `otel/opentelemetry-collector-contrib:0.150.1` (same image already in use; `spanmetrics` connector is included).

Pipeline:

```yaml
receivers:
  otlp:
    protocols:
      grpc:

connectors:
  spanmetrics:
    histogram:
      explicit:
        buckets: [5ms, 10ms, 25ms, 50ms, 100ms, 250ms, 500ms, 1s, 2s, 5s]
    dimensions:
      - name: service.name
      - name: rpc.method
      - name: http.method
      - name: http.route
      - name: http.status_code
    exemplars:
      enabled: false
    metrics_flush_interval: 15s

exporters:
  prometheus:
    endpoint: 0.0.0.0:9464
    namespace: onlineboutique
    resource_to_telemetry_conversion:
      enabled: true
    enable_open_metrics: true

service:
  pipelines:
    traces:
      receivers: [otlp]
      exporters: [spanmetrics]
    metrics:
      receivers: [spanmetrics]
      exporters: [prometheus]
```

The Service exposes both `:4317` (existing OTLP/gRPC) and `:9464` (new, Prometheus scrape) and carries `prometheus.io/scrape: "true"` annotations on the Pod template.

### 3.2 Prometheus

- `Deployment` (1 replica), `emptyDir` storage, 6h retention (`--storage.tsdb.retention.time=6h`).
- `Service` (ClusterIP, port 9090).
- `ConfigMap` with the scrape config below.
- `ServiceAccount` + `ClusterRole` + `ClusterRoleBinding` granting `get/list/watch` on `pods`, `services`, `endpoints`, `nodes`, `nodes/metrics`.
- Resource requests: `cpu: 200m, memory: 256Mi`; limits: `cpu: 500m, memory: 512Mi`.

Scrape config (three jobs):

```yaml
scrape_configs:
  - job_name: otel-collector
    static_configs:
      - targets: [opentelemetrycollector:9464]

  - job_name: kube-state-metrics
    static_configs:
      - targets: [kube-state-metrics:8080]

  - job_name: kubernetes-pods
    kubernetes_sd_configs:
      - role: pod
    relabel_configs:
      - source_labels: [__meta_kubernetes_pod_annotation_prometheus_io_scrape]
        action: keep
        regex: "true"
      - source_labels: [__meta_kubernetes_pod_annotation_prometheus_io_path]
        action: replace
        target_label: __metrics_path__
        regex: (.+)
      - source_labels: [__address__, __meta_kubernetes_pod_annotation_prometheus_io_port]
        action: replace
        regex: ([^:]+)(?::\d+)?;(\d+)
        replacement: $1:$2
        target_label: __address__
      - action: labelmap
        regex: __meta_kubernetes_pod_label_(.+)
```

The third job is future-proofing: any pod that later carries `prometheus.io/scrape: "true"` annotations is auto-scraped without further config.

### 3.3 kube-state-metrics

Standard upstream KSM, slim install:

- `Deployment` (1 replica), image `registry.k8s.io/kube-state-metrics/kube-state-metrics:v2.13.0`.
- `Service` exposing port 8080.
- `ServiceAccount` + `ClusterRole` + `ClusterRoleBinding` (read-only access to standard k8s resources).
- Resource requests: `cpu: 50m, memory: 64Mi`; limits: `cpu: 100m, memory: 128Mi`.

### 3.4 Grafana

- `Deployment` (1 replica), `emptyDir` storage, image `grafana/grafana:11.2.0`.
- `Service` (ClusterIP, port 3000).
- Two `ConfigMap`s:
  - `grafana-provisioning` — provisioning configs for datasource and dashboard provider.
  - `grafana-dashboards` — generated by `configMapGenerator` from `dashboards/*.json`.
- Env:
  - `GF_AUTH_ANONYMOUS_ENABLED=true`
  - `GF_AUTH_ANONYMOUS_ORG_ROLE=Viewer`
  - `GF_SECURITY_ADMIN_PASSWORD=admin` (demo only).
- Resource requests: `cpu: 100m, memory: 128Mi`; limits: `cpu: 200m, memory: 256Mi`.

Provisioning files mounted at `/etc/grafana/provisioning/datasources/datasource.yaml` and `/etc/grafana/provisioning/dashboards/dashboards.yaml`. Dashboard JSON files mounted at `/var/lib/grafana/dashboards/`.

### 3.5 Dashboards

Both auto-load on first launch.

#### Dashboard 1 — "Online Boutique — Service Overview" (`uid: boutique-overview`)

Single-pane view. Template variable: `$namespace` (default).

| Row | Panel | Type | Query |
|---|---|---|---|
| Top stats | Total RPS | stat | `sum(rate(onlineboutique_calls_total[1m]))` |
| Top stats | Error rate (%) | stat | `sum(rate(onlineboutique_calls_total{status_code="STATUS_CODE_ERROR"}[1m])) / sum(rate(onlineboutique_calls_total[1m])) * 100` |
| Top stats | p95 latency (ms) | stat | `histogram_quantile(0.95, sum by (le) (rate(onlineboutique_duration_milliseconds_bucket[5m])))` |
| Top stats | Pods running | stat | `sum(kube_pod_status_phase{phase="Running",namespace="$namespace"})` |
| RED grid | RPS per service | timeseries | `sum by (service_name) (rate(onlineboutique_calls_total[1m]))` |
| RED grid | Error rate per service | timeseries | `sum by (service_name) (rate(onlineboutique_calls_total{status_code="STATUS_CODE_ERROR"}[1m])) / sum by (service_name) (rate(onlineboutique_calls_total[1m]))` |
| RED grid | p95 latency per service | timeseries | `histogram_quantile(0.95, sum by (service_name, le) (rate(onlineboutique_duration_milliseconds_bucket[5m])))` |
| Pod health | Pod restarts (1h) | table | `sum by (pod) (increase(kube_pod_container_status_restarts_total{namespace="$namespace"}[1h]))` |
| Pod health | Pods not ready | table | `kube_pod_status_ready{condition="true",namespace="$namespace"} == 0` |

#### Dashboard 2 — "Online Boutique — Service Drilldown" (`uid: boutique-drilldown`)

Template variables: `$namespace` (default), `$service` (from `label_values(onlineboutique_calls_total, service_name)`).

| Row | Panel | Type | Query |
|---|---|---|---|
| Header | Selected service | stat | (header card driven by `$service`) |
| RED | Request rate by method/route | timeseries | `sum by (rpc_method, http_route) (rate(onlineboutique_calls_total{service_name="$service"}[1m]))` |
| RED | Errors by status | timeseries | `sum by (status_code) (rate(onlineboutique_calls_total{service_name="$service",status_code!="STATUS_CODE_OK"}[1m]))` |
| RED | Latency p50/p95/p99 | timeseries | `histogram_quantile(0.50\|0.95\|0.99, sum by (le) (rate(onlineboutique_duration_milliseconds_bucket{service_name="$service"}[5m])))` (three queries) |
| RED | Latency heatmap | heatmap | `sum by (le) (rate(onlineboutique_duration_milliseconds_bucket{service_name="$service"}[1m]))` |
| Pod | CPU per pod | timeseries | `sum by (pod) (rate(container_cpu_usage_seconds_total{namespace="$namespace",pod=~"$service-.*"}[2m]))` |
| Pod | Memory per pod | timeseries | `sum by (pod) (container_memory_working_set_bytes{namespace="$namespace",pod=~"$service-.*"})` |
| Pod | Restart count | stat | `sum(kube_pod_container_status_restarts_total{namespace="$namespace",pod=~"$service-.*"})` |

Note: container CPU/memory rely on the kubelet/cAdvisor metrics that KSM does *not* export. cAdvisor scraping is deferred to v2 (see §8 Open Questions), so these two panels will be empty on clusters where cAdvisor isn't pre-scraped. The dashboard will include a note panel acknowledging this.

## 4. File Layout

### kustomize

```
kustomize/components/prometheus-grafana/
├── kustomization.yaml          # Component manifest, resources + per-service patches
├── otel-collector.yaml         # Collector with spanmetrics + prometheus exporter
├── prometheus.yaml             # Deployment, Service, ConfigMap, SA + RBAC
├── kube-state-metrics.yaml     # Deployment, Service, SA + RBAC
├── grafana.yaml                # Deployment, Service, datasource + dashboards ConfigMaps
├── dashboards/
│   ├── service-overview.json
│   └── service-drilldown.json
└── README.md

kustomize/tests/prometheus-grafana/
└── kustomization.yaml          # Smoke test composing component against base
```

`kustomization.yaml` includes the same per-service patches as `google-cloud-operations/kustomization.yaml` (10 services: checkoutservice, currencyservice, emailservice, frontend, paymentservice, productcatalogservice, recommendationservice, shippingservice, cartservice, loadgenerator) — setting `ENABLE_TRACING=1`, `COLLECTOR_SERVICE_ADDR=opentelemetrycollector:4317`, and `OTEL_SERVICE_NAME=<service>`. `adservice` is omitted because it has no tracing wiring (mirroring the existing component's behavior).

### Helm

Modified:

- `helm-chart/values.yaml` — add the `prometheusGrafana` block (see §5).
- `helm-chart/templates/opentelemetry-collector.yaml` — when `prometheusGrafana.create`, swap exporters/pipeline to the Prometheus version and add the `:9464` Service port.
- `helm-chart/templates/<each service>.yaml` — when `prometheusGrafana.create`, set `ENABLE_TRACING=1` + `COLLECTOR_SERVICE_ADDR` + `OTEL_SERVICE_NAME` env vars (for the same 10 services).

New:

- `helm-chart/templates/prometheus-grafana.yaml` — gated by `{{- if .Values.prometheusGrafana.create }}`, ships Prometheus, Grafana, kube-state-metrics, RBAC, ConfigMaps. Dashboard JSON inlined via `{{ .Files.Get "dashboards/service-overview.json" | indent 4 }}`.
- `helm-chart/dashboards/service-overview.json` and `helm-chart/dashboards/service-drilldown.json` — same dashboard files as the kustomize component.

## 5. Helm Values

```yaml
prometheusGrafana:
  create: false
  prometheus:
    retention: 6h
    resources:
      requests: { cpu: 200m, memory: 256Mi }
      limits:   { cpu: 500m, memory: 512Mi }
  grafana:
    anonymousAccess: true
    adminPassword: admin
    resources:
      requests: { cpu: 100m, memory: 128Mi }
      limits:   { cpu: 200m, memory: 256Mi }
  kubeStateMetrics:
    create: true
```

`prometheusGrafana.create` and `googleCloudOperations.metrics` are mutually exclusive; if both are true the Helm template fails fast with a `{{ fail }}` directive.

## 6. Data Flow

1. User installs the component (`kustomize edit add component ../components/prometheus-grafana` or `helm install ... --set prometheusGrafana.create=true`).
2. Per-service patches set `ENABLE_TRACING=1` and `COLLECTOR_SERVICE_ADDR=opentelemetrycollector:4317`.
3. Services emit OTLP/gRPC traces to the OTel Collector on port 4317.
4. Collector pipeline:
   - `traces` pipeline: receives OTLP → fans out to `spanmetrics` connector.
   - `spanmetrics` connector emits derived metrics (`calls_total`, `duration_milliseconds_bucket`).
   - `metrics` pipeline: receives from connector → exports via `prometheus` exporter at `0.0.0.0:9464`.
5. Prometheus scrapes:
   - `opentelemetrycollector:9464` for RED metrics.
   - `kube-state-metrics:8080` for k8s state.
   - Annotated pods via `kubernetes_sd_configs`.
6. Grafana queries Prometheus; user accesses dashboards via `kubectl port-forward svc/grafana 3000:3000`.

## 7. Error Handling & Edge Cases

- **Tracing not enabled.** If `ENABLE_TRACING=1` is missing on a service, RED panels will be empty for that service. The component patches set this for all 10 supported services, so this only manifests if a user partially overrides values.
- **adservice.** No tracing in adservice yet; its row in the per-service Overview panels will show no data. The README will call this out.
- **Dual install with `google-cloud-operations`.** Both components define a `Deployment/opentelemetrycollector` with conflicting configs. kustomize will fail to build (duplicate resource); Helm will short-circuit via `{{ fail }}`. Documented in both READMEs.
- **Pod CPU/Memory panels empty.** Until cAdvisor scraping is added (out of scope for v1), the drilldown CPU/Memory panels render as empty with a note. KSM-derived restart and readiness panels still work.
- **Prometheus pod evicted.** With `emptyDir`, all metrics are lost. Acceptable for a demo; the README states this.
- **Port conflicts inside cluster.** New ports introduced cluster-internally: `9464` (collector → prom), `9090` (prometheus UI), `3000` (grafana), `8080` (KSM). All ClusterIP, no NodePort/LoadBalancer.

## 8. Open Questions

None blocking. Possible v2 follow-ups (out of scope here):

- Add cAdvisor scrape job to populate per-pod CPU/memory drilldown panels.
- Add Alertmanager + a sample alert rule (e.g. error rate > 5%).
- Native Prometheus `/metrics` endpoints in each service (RED-from-traces ⊆ what native instrumentation can offer).
- Switch to a `StatefulSet` + PVC for Prometheus if users want survivable storage.

## 9. Testing & Validation

- **Kustomize**: add `kustomize/tests/prometheus-grafana/kustomization.yaml` as a standalone smoke test that composes the component on top of `kustomize/base`. (The existing `*-with-all-components` test pattern doesn't apply here because `prometheus-grafana` is mutually exclusive with `google-cloud-operations`; combining them would conflict on the `opentelemetrycollector` Deployment.) CI runs `kustomize build` against this directory.
- **Helm**: `helm template . --set prometheusGrafana.create=true` produces valid YAML; `helm template . --set prometheusGrafana.create=true --set googleCloudOperations.metrics=true` fails with the expected `{{ fail }}` message.
- **Manual smoke**:
  1. `kind create cluster && skaffold run` (or `kubectl apply -k kustomize/`) with the component enabled.
  2. `kubectl port-forward svc/grafana 3000:3000`.
  3. Open `http://localhost:3000/d/boutique-overview`. Within ~1 minute (after loadgenerator warms up and `metrics_flush_interval` ticks) the RPS panel shows non-zero data for each service.
  4. Open the Drilldown dashboard, switch `$service` between values, confirm latency heatmap renders.

## 10. Rollout

This is opt-in (`create: false` by default in Helm; not in the default kustomize stack). No impact on the default install path. README updates point to the new component.
