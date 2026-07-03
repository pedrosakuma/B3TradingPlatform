# RFC: azure-deployment-topology-v0 — cloud deployment topology & transport mapping

> Status: **Draft** · Tracking: [#557](https://github.com/pedrosakuma/B3TradingPlatform/issues/557)
> · Epic: go-public (#527, closed) follow-up
> · Interacts with `user-bot-fixp-edge-topology-v0`, `user-bot-fixp-mtls-v0`,
> `integration-real-stack-v0`, `docker-compose.public.yml` (#531).

## 1. Context

The go-public hardening (#527) shipped the *logical* edge posture but left
"cloud-provider specifics" an explicit non-goal (`user-bot-fixp-edge-topology-v0`
§3). This RFC pins a concrete **Azure** target so the family can move from
"works under docker-compose" to "runs on Azure" without emergent decisions —
and, critically, so the **non-WebSocket / UDP transports** are mapped onto
Azure primitives that actually support them.

The family is the participant side of a B3 **simulation** ecosystem
(`B3MatchingPlatform` sim + `B3MarketDataPlatform` + our trading-host). Go-public
means a **public sandbox / bot-arena**, not a regulated broker. Regulatory/CVM
authorization stays out of scope.

## 2. The transport inventory (why this RFC exists)

The system is **not uniform HTTP**. External surfaces are WebSocket/TCP, but
internally it carries the exchange-native transports — including a **UDP**
market-data leg that is *multicast* on real B3.

| Leg | Direction | Transport | Port(s) | Faces |
|-----|-----------|-----------|---------|-------|
| Bot order entry | bot → trading-host | **FIXP/SBE over TCP + mTLS** | `:5001` | **public** |
| Bot / operator market data + API | client → trading-host | REST + **WebSocket** | `:5000` | public / operator |
| Frontend | browser → frontend → trading-host | HTTP/WS (nginx reverse-proxy) | `:8080` | public |
| Firm sessions | trading-host → matching-platform | **FIXP/SBE over TCP** | `:9876` | **internal** |
| Market-data fan-in | trading-host → marketdata | **WebSocket** | `:8080` | **internal** |
| **UMDF feed** | **matching-platform → marketdata** | **UDP (multicast on real B3; unicast on cloud)** | `30084/30085/31084/30184 udp` | **internal** |

> **Rule of thumb:** to the outside world it is **WebSocket/TCP**; internally the
> exotic leg is **UDP UMDF**. That one leg drives the whole compute choice.

## 3. Goals

- Map every transport in §2 onto Azure primitives that preserve its semantics —
  in particular the **mTLS-in-listener** and **UDP UMDF** legs.
- Pick the compute substrate (AKS vs VMSS vs PaaS) consistent with those
  constraints.
- Define networking (VNet/subnets/NSG), secrets, registry, persistence (WAL),
  and observability targets.
- Keep it faithful-enough to the real B3 topology for a simulation.

## 4. Non-goals

- Multi-region / DR, autoscaling policy tuning, WAF rule authoring, cost model.
- Changing any wire protocol (all upstream; this repo only adapts/deploys).
- Real-multicast on Azure (see §5 — it does not exist; we design around it).

## 5. The load-bearing constraint — Azure has no IP multicast

**Azure Virtual Networks do not support IP multicast or broadcast.** Real B3
UMDF is multicast; the docker-compose family already runs the marketdata
consumer in **unicast UDP** mode (`transport: "unicast"`,
`UMDF_MULTICAST_CONFIG=/app/config/transport.json`, `MulticastPacketSource`
skips `SetMulticastOption` — upstream `B3MarketDataPlatform#10`/#11, recorded in
`integration-real-stack-v0` §8). So the cloud posture is **already the
supported path**; this RFC just makes it mandatory and pins its consequences.

**Decision U1 — UMDF runs unicast UDP on Azure.** matching-platform emits UMDF
to a single stable unicast target (the marketdata instance), never a multicast
group.

**Consequences of losing multicast (must be designed around):**

- **Single-consumer fan-out.** Multicast would let N marketdata replicas join
  one group for free. Unicast emits to **one** address. Therefore the
  marketdata tier is **not horizontally scalable via the UDP leg** — scale-out
  happens *downstream* on the WebSocket fan-out (`:8080`), which is where public
  fan-out actually lives anyway (cf. #536's conflated-tier idea, descoped). One
  marketdata instance per matching emitter in v0.
- **Stable addressing required.** UDP is connectionless: matching must send to a
  fixed private address:port. Do **not** put the UDP leg behind a load balancer
  (LB health/UDP semantics + NAT would break the unicast bind). Use a **stable
  private endpoint**: on AKS a **headless Service + StatefulSet** (stable pod
  DNS/IP) or a fixed ClusterIP; on VMSS a reserved private IP. matching's
  `transport.json` / emit sink points at that address.
- **Same failure domain.** matching-platform ⇄ marketdata is a tight UDP pair
  with no delivery guarantees; co-locate them (same subnet / same node pool /
  same availability zone) to keep loss + jitter low and avoid cross-zone UDP.
- **No LB in the path.** The internal UDP leg is **direct instance-to-instance**
  east-west inside the VNet. Azure Standard LB *does* support UDP rules, but we
  deliberately keep UMDF off any LB (unicast pinned target, not a pool).

## 6. Compute substrate

The **mTLS-in-listener + L4-passthrough** decision (`user-bot-fixp-edge-topology-v0`
§4) plus the **raw UDP** leg jointly **rule out Azure L7 PaaS**:

- ❌ **App Service / Container Apps / App Gateway / Front Door** — all terminate
  TLS at L7 (break the in-process client-cert pin) and are HTTP(S)-oriented (no
  raw inbound UDP between services, awkward raw-TCP FIXP). Rejected.
- ✅ **AKS (recommended)** — supports: raw TCP via **Standard Load Balancer**
  (L4 passthrough, `externalTrafficPolicy: Local` to preserve client source IP
  for the per-IP caps in #529); intra-cluster **UDP** pod-to-pod; StatefulSets +
  Azure Disk for single-writer WALs; headless Services for the UDP unicast
  target. One cluster, node pools per tier.
- ✅ **VMSS + Docker (alternative)** — the compose family lifts as-is onto VMs
  in a VNet with an L4 Standard LB for the public FIXP/WS ports and direct
  private UDP between the matching and marketdata VMs. Simpler mental model,
  less orchestration; weaker rollout/self-heal story. Acceptable v0 fallback.

**Decision C1 — target AKS**, with VMSS-compose as the documented fallback for
an early/minimal deployment.

## 7. Networking

- One **VNet**, three subnets: `snet-public-edge` (LB frontends), `snet-app`
  (trading-host, frontend), `snet-market` (matching-platform + marketdata, the
  UDP pair — co-located).
- **NSGs / surface segregation** (mirrors `user-bot-fixp-edge-topology-v0` §5):
  - Public ingress only to **FIXP `:5001`** (mTLS) and the **frontend `:8080`**
    via the Standard LB. Operator **API `:5000`** stays private (VPN / bastion /
    private endpoint) — never public.
  - `:9876` (FIXP firm sessions) and the `30084/30085/31084/30184 udp` UMDF
    ports are **intra-VNet only**, locked to the matching⇄marketdata and
    host→matching source/dest by NSG rule.
- **L4 Standard LB, `externalTrafficPolicy: Local`** so the listener still sees
  the real client IP that the per-IP `AcceptConnectionRateLimiter` (#529) keys
  on. Edge does connection caps only; policy stays in-process (defense in depth,
  #529/#533).

## 8. Persistence — the single-writer WAL

Both trading-host and matching-platform own a **single-writer WAL + snapshots**
(WAL is the source of truth across restarts — `AGENTS.md`; matching's WAL is
`matching-data`). This forbids a shared/scale-out filesystem:

- **Azure Disk (Premium SSD) PVC, `ReadWriteOnce`**, bound to a **StatefulSet**
  replica (or the reserved VM). One writer, one disk.
- HA is therefore **active-passive with disk reattach**, not active-active.
  matching session state (SessionVerId/seq) is in-memory today anyway — a
  failover forces client re-Negotiate (already handled by
  `ReconcileFirmSessionVerIds`, #420). Multi-writer HA is explicitly deferred.

## 9. Secrets, registry, images

- **Azure Key Vault** for the `${VAR:?}` required secrets that
  `docker-compose.public.yml` (#531) refuses to default: auth signing key,
  `OwnerHashSalt`, ClOrdId mask salt, FIXP **server cert/key + bot CA bundle +
  revocation deny-list**. Mount via CSI Secrets Store driver (AKS); the deny-list
  stays **hot-reloadable** (no restart) as designed (#538).
- **ACR** mirroring the current GHCR images (`b3-matching`, `b3-marketdata`,
  `b3-trading-host`, `b3-trading-frontend`). Keep matching **sha-pinned** (as the
  base compose already does); resolve the `:latest` marketdata/trading tags to
  digests at deploy time.

## 10. Observability

The family emits Prometheus metrics + has a Grafana overlay and public-auth
alert rules (`docs/ops/public-auth-alerts.md`, #533). Two options:

- **Azure Managed Prometheus + Managed Grafana** (recommended on AKS) — scrape
  the existing `/metrics`; import the existing dashboards/alert rules.
- Self-hosted Prometheus/Grafana pods (lift the observability overlay) if we
  want zero Azure-monitoring coupling.

Keep the health endpoint **internal**; expose only a TCP liveness to the L4 LB
(`user-bot-fixp-edge-topology-v0` §7), and drive drain alerting off
`entrypoint_listener.enabled` + `sessions_active`.

## 11. Decisions summary

- **U1** UMDF runs **unicast UDP** (no multicast on Azure); one marketdata
  instance per emitter, stable private endpoint, co-located with matching, no LB
  in the UDP path.
- **C1** **AKS** (L4 Standard LB, `externalTrafficPolicy: Local`), VMSS-compose
  as fallback. L7 PaaS rejected (breaks mTLS-in-listener + raw UDP/TCP).
- Networking: one VNet / three subnets, public only to FIXP `:5001` + frontend;
  API `:5000`, FIXP `:9876`, UMDF UDP intra-VNet only.
- Persistence: per-writer Premium Azure Disk (RWO) on StatefulSets;
  active-passive HA via disk reattach.
- Secrets: Key Vault + CSI; hot-reload deny-list. Registry: ACR, digest-pinned.
- Observability: Managed Prometheus + Grafana; internal health, TCP liveness.

## 12. Open questions

- **Q1** AKS single cluster vs matching/marketdata in a separate "venue" node
  pool (blast-radius + UDP locality) vs entirely separate cluster.
- **Q2** If a future need forces multiple marketdata consumers without
  multicast: unicast-per-consumer emit list on matching, or an in-VNet app-level
  UDP relay/reflector? (upstream `B3MarketDataPlatform` touchpoint).
- **Q3** Does the managed Standard LB do pure L4 passthrough for the mTLS TCP
  port in every SKU/region we care about, or do we need PROXY-protocol
  (rejected in the edge RFC)? Probe before committing.
- **Q4** WAL durability under node eviction — Premium Disk reattach time vs a
  brief accept-closed window; measure against `OutboundDrainShutdownTimeout`.
