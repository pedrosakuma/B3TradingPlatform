# Helm charts

Two per-service Helm charts, published as OCI artifacts alongside the images
built by [`.github/workflows/docker.yml`](../.github/workflows/docker.yml):

| Chart                  | Replaces (b3deploy manifest)                                  | Publishes to                                              |
| ----------------------- | -------------------------------------------------------------- | ----------------------------------------------------------- |
| `b3-trading-host`       | `envs/prod/trading-host.yaml` + `envs/prod/secret-provider.yaml` | `oci://ghcr.io/pedrosakuma/charts/b3-trading-host`         |
| `b3-trading-frontend`   | `envs/prod/frontend.yaml`                                       | `oci://ghcr.io/pedrosakuma/charts/b3-trading-frontend`     |

This is Layer-1 (component-local) per the [deploy topology RFC](../docs/rfcs)
(#557): the templates and their default values live here, next to the images
they deploy. b3deploy (Layer-2) pins `chart@version` + `image@sha256:` and
supplies env-specific values (Key Vault name, cluster DNS ClusterIP, firm
config, secrets) — see #568.

## Usage

```bash
# render locally
helm template my-release charts/b3-trading-host \
  --set keyVault.name=kv-b3-prod-xxxx \
  --set keyVault.clientId=<managed-identity-client-id> \
  --set keyVault.tenantId=<tenant-id>

helm template my-release charts/b3-trading-frontend \
  --set nginx.resolver=10.0.0.10 \
  --set nginx.tradingUpstream=trading-host.<namespace>.svc.cluster.local:5000

# install from the published OCI artifact (once #568's CI job has pushed a version)
helm install trading-host oci://ghcr.io/pedrosakuma/charts/b3-trading-host \
  --version <chart-version> \
  -f my-prod-values.yaml
```

See each chart's `values.yaml` for the full, commented values surface.

## Versioning contract

Each chart's `Chart.yaml` `version` (SemVer) is bumped independently of its
`appVersion` whenever templates or default values change. `appVersion`
tracks the image tag the chart was last validated against; `values.image.tag`
(or `values.image.digest` for a pinned deploy) is the live default actually
used at install time. b3deploy pins both `chart@version` and
`image@sha256:...` — the two need not move in lockstep, but a chart release
should always be validated against the appVersion it declares.

CI (`.github/workflows/helm-charts.yml`) lints + templates both charts on
every PR touching `charts/**`, and packages + pushes to GHCR on `main` —
skipping the push if that exact `chart@version` is already published (OCI
tags are treated as immutable once released).

## Known prerequisites

- `b3-trading-frontend`'s `nginx.tradingUpstream` requires the image built
  from this repo's `frontend/` directory to support `TRADING_UPSTREAM`
  (#564) — without it, the upstream is hardcoded to the Docker Compose short
  name and Kubernetes deploys get 502s (nginx's `resolver` doesn't apply
  `/etc/resolv.conf` search domains).
- `b3-trading-frontend`'s `nginx.marketDataWsUrl` requires the image built
  from this repo's `frontend/` directory to support `MARKETDATA_WS_URL`
  (#572) — without it, the "Market Data" panel has no deploy-time default
  and every operator must paste the WS URL in by hand each session.
- `b3-trading-host`'s reconnect resilience after a matching pod IP change
  depends on #565 (FIXP `EntryPointClient` re-resolving DNS on reconnect).
- `b3-trading-host` requires the Azure Key Vault CSI Secrets Store driver
  installed on the cluster (`secrets-store.csi.x-k8s.io/v1` CRDs) whenever
  `keyVault.name` is set (the default; unset it only for a Stub/CI-only
  install with no real secrets).
