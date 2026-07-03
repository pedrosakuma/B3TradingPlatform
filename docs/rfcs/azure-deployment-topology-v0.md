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

## 11. Resilience posture & scaling model

This section states honestly what the topology buys us. The short version:
**strong recovery resilience, partial availability (HA) resilience** — the
single points of failure fail *closed/degraded*, not catastrophically, and
recover from durable state. Appropriate for a public *simulation* sandbox; not
zero-downtime HA.

### 11.1 Two independent fan-outs (do not conflate them)

Market data and execution are **separate fan-outs in separate services** — they
scale and fail in **different domains**:

- **Market-data fan-out = the `marketdata` service** itself (WebSocket `:8080/ws`,
  binary protocol v2). Public clients / the frontend consume it **directly**
  (`frontend/js/app.js` "FE consumes B3MarketDataPlatform directly via mdWorker";
  `frontend/js/mdProtocol.js`). The trading-host does **not** re-broadcast MD.
- **Execution fan-out = the trading-host** (`B3.Trading.Api` WS/REST): order
  updates, execution reports, positions, `/ws/dropcopy`. It is **per-client /
  per-firm**, bound to the WAL/session — **not a broadcast**.
- The trading-host only **consumes** the MD WS as a *thin ref-price client* for
  the risk layer (`Trading__MarketData__WsUrl`, `docker-compose.yml`). One
  subscription, not a fan-out. If it drops, risk degrades (§1.8 *Degraded*) —
  **independently** of what public clients see, because they are on `marketdata`
  directly.

Consequence: the descoped conflated-MD-tier concern (#536) lives **upstream in
`marketdata`**, not in the trading-host; there is no MD consume+fan-out to
"split apart" on our side.

### 11.2 Three single-writer cores, three separate failure domains

| Core (single-writer) | Domain | On loss | Recovery | Blast radius |
|----------------------|--------|---------|----------|--------------|
| **matching-platform** (+ its WAL) | execution / sequencing | order accept pauses, `502`, `readyForOrders=false` (§1.7, fail-closed) | pod restart + WAL/snapshot replay; client re-Negotiate (`ReconcileFirmSessionVerIds` #420) | halts trading; MD to public clients unaffected |
| **trading-host WAL** | our positions / working orders / ClOrdId watermark | that firm's host down | warm restart snapshot+WAL replay; FIXP reconnect state machine (§11.3) | one host/firm; other firms unaffected |
| **marketdata UDP consumer** | market data | MD *Degraded*, ref-price stale (§1.8) | restart + SnapshotRecovery; matching re-resolves target IP | public MD gap; **execution unaffected** |

They are single-writer **by domain design**, not by StatefulSet limitation (§11.4).

### 11.3 Recovery resilience we actually have

- **Crash-consistent WAL** on trading-host + matching (source of truth). Cold
  start, warm restart snapshot+WAL replay, snapshot-only, WAL repair
  (`runbook-failover-recovery.md` §2). Premium Disk reattach preserves it across
  an AKS reschedule.
- **Graded FIXP reconnect state machine** (runbook §1.12): within
  `SuspendedTimeoutMs` → `Reattached` (auto `RetransmitRequest` replay + idempotent
  ER dedup, no operator action); beyond → `Renegotiated` (reap `PendingNew`, flag
  stale survivors #380/#515). Directly resists the disconnect churn a cloud env
  produces.
- **Fail-closed on dependency loss** (§1.7): matching down ⇒ `502`,
  `readyForOrders=false`. No silent bad trades.
- **Graceful drain** (§3, `OutboundDrainShutdownTimeout`) + health/liveness ⇒
  clean rolling updates on AKS.
- **AKS platform layer** adds what compose never had: pod reschedule, node
  self-heal, PVC reattach, multi-AZ node pools.

### 11.4 Scaling model — stateful vertical, stateless horizontal

StatefulSets *can* run N replicas; what pins the core to **one active writer** is
the single-writer WAL / sequencing authority — running N active writers would
**break ordering/truth invariants** (§8). This is intentional, not a limitation
to fix.

- **Stateful core** (matching, trading-host, marketdata UDP consumer) → scale
  **vertically** (bigger node), resilience via **active-passive failover**, never
  active-active. Single-instance throughput is the ceiling; past it the lever is
  vertical sizing or **sharding by partition key** (see below), not StatefulSet
  replicas.
- **Stateless edges** (the `marketdata` WS fan-out, REST/WS read paths, frontend)
  → scale **horizontally** (Deployments + HPA). This is where public load lands.

**Not all "single-writer" cores are equally un-scalable — the partition scope
differs.** The single-writer constraint is about the *scope* over which ordering
must be total, and that scope is different per tier:

- **matching-platform** — its sequencing authority is **global per instrument**:
  one order book, one sequencer, across **all** participants. There is no finer
  partition key below the instrument, so a firm's flow cannot be split off. This
  tier genuinely **does not scale out** below instrument granularity — a *given*,
  accepted constraint.
- **trading-host** — its invariants are **per-`EndClientId`** (grouped per firm):
  the ClOrdID monotonic watermark, and the stateful/order-sensitive pre-trade
  risk (`RollingNotional` sliding window, margin reservations, cash, self-trade
  prevention) are all keyed by end-client (`WalEvents.cs`,
  `Risk/Accounting/`, `Risk/ReserveOnSubmitMarginProvider.cs`). So single-writer
  here is **correctness, not just durability** — but its natural **partition key
  is the firm / end-client**. The host therefore **does scale horizontally by
  sharding on firm** (already the model: the `Firms[]` config, "one host per
  firm" — `runbook-failover-recovery.md` §4). You may not run two writers for the
  *same* firm's accounts, but you may run N firm-shards, each single-writer only
  over its own accounts.

So the core's horizontal-scale lever is the **partition key**, not the replica
count: matching is pinned at instrument granularity; trading-host shards by firm.
Per-shard availability is still a **fast-failover** question (WAL replay, or
#309-style active-passive if a shard needs hot standby), not a redundancy one.

So "StatefulSets are non-scalable" is **correct and not a problem for the core** —
the resilience question there is **fast failover, not redundancy**.

> ⚠️ **Caveat — this only holds *once §11.6 is addressed*.** Today the
> trading-host's public edge is **not** a separable stateless tier: it is welded
> into the same process as the single-writer WAL, so it inherits the core's
> single-instance constraint. The horizontal-scale claim above is **aspirational
> for the trading-host edge pending the process split in §11.6**; it holds today
> only for the `marketdata` WS fan-out and the frontend, which are already
> separate deployables.

### 11.5 Honest gaps (degraded-mode, not HA)

- **matching-platform active-passive HA is unshipped** (#309 / Q4.9; runbook §4 is
  *intended* behaviour only). Until it lands, matching is a **SPOF** with a
  failover *window* (disk reattach + re-Negotiate), not single-digit-second hot
  failover.
- **The UDP MD leg has no hot standby.** Unicast + single consumer (Decision U1)
  ⇒ MD availability = **MTTR of one pod**; multicast would have allowed a warm
  second consumer, unicast does not. Co-locating the UDP pair in one AZ (for
  latency) makes **that AZ a failure domain** for MD.
- **matching session state (SessionVerId/seq) is in-memory** ⇒ every failover
  forces client re-Negotiate + stales working orders (handled, but disruptive).
- **Single AKS cluster; no multi-region / DR** (explicit non-goal §4).

**Net:** recovery resilience is strong and CI-exercised (chaos drills, runbook
§6); availability resilience is partial — good for a public bot-arena, gated on
#309 for true HA of the execution core, with the **marketdata UDP consumer** as
the weakest MD-side link.

### 11.6 Biggest structural risk — the single-process monolith

The largest offender to the scaling/resilience model above is **how the
trading-host process is assembled**, not any single transport. `Program.cs`
(`B3.Trading.Host`) builds **one OS process / one deployable** (`b3-trading-host`)
that co-hosts, on the same heap and thread pool:

- `AddEntryPointListener` — the **public, untrusted FIXP/SBE listener** (mTLS
  edge, exposed to hostile input / DoS);
- `MapTradingEndpoints` — the **public REST/WS API + execution fan-out +
  `/ws/dropcopy`** (Kestrel);
- `AddTradingPersistence` (**single-writer WAL**) + `AddTradingRisk` +
  `AddTradingExchangeGateway` — the **stateful core / money-path**;
- plus the MD ref-price consumer, auth (password hashing), and the rate limiter.

**Why it is the load-bearing risk:**

1. **The WAL single-writer contaminates the naturally-stateless edge.** A second
   trading-host replica would be a second WAL writer — forbidden (§8). So the
   legitimate single-writer constraint of the core **forces the public FIXP
   listener and the WS/REST fan-out to be single-instance per firm too**. This is
   *why* §11.4's horizontal-scale story does not hold for the trading-host edge
   today.
2. **Shared failure domain.** The untrusted public edge shares a process with the
   money-path. A crash / OOM / unbounded GC pause / thread-pool starvation / bug
   or DoS in the listener takes down order processing, risk, WAL, and drop-copy
   for that firm.
3. **Resource contention, no isolation.** Bot TLS handshakes, password hashing,
   REST/JSON, WS fan-out, and the latency-sensitive order/risk/WAL path share one
   thread pool and GC heap; an edge burst injects latency/GC pressure into
   execution.

**Mitigating factors.** The seams already exist as **separate projects**
(`EntryPointListener`, `Api`, `Application`+WAL) with a clean layering
contract (WAL = source of truth, `Application` = single-writer — `AGENTS.md`).
So this is a **process-boundary + internal-transport** change, not a rewrite. For
current sandbox scale a single process is also simpler, and the core is
single-instance regardless — so the win from splitting is **fault isolation of
the untrusted edge from the money-path** and **unblocking horizontal fan-out**,
**not** raw throughput.

**Direction (own RFC — see Q5).** Carve along the existing seams:

- **Edge tier** (stateless, horizontal, untrusted): session-terminating FIXP
  listener + public WS/REST read + fan-out. N replicas behind the L4 LB; forwards
  order *intents* to the core.
- **Core tier** (stateful, single-writer, active-passive): `Application` + WAL +
  risk + exchange gateway. One writer per firm.

The wrinkle: the `listener → Application` call is **in-process** today; splitting
needs an internal RPC/queue between edge and core and a decision on whether
pre-trade risk runs at the edge (fast reject) or only in the core (authoritative)
— non-trivial, hence a dedicated RFC rather than a line item here.

## 12. Decisions summary

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

## 13. Open questions

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
- **Q5** **Trading-host process decomposition** (§11.6) — split the untrusted
  public edge (FIXP listener + WS/REST fan-out) from the stateful single-writer
  core into separate deployables. Needs a dedicated RFC: internal edge↔core
  transport (RPC vs queue), where pre-trade risk runs (edge fast-reject vs
  core-authoritative), and back-pressure/ordering across the boundary. Highest-
  leverage item for making §11.4's horizontal-scale story real and isolating the
  hostile-input edge from the money-path.
