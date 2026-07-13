# RFC: user-bot-fixp-edge-topology-v0 — network exposure & deployment topology

> Status: **Draft** · Tracking: [#532](https://github.com/pedrosakuma/B3TradingPlatform/issues/532)
> · Epic: [#527](https://github.com/pedrosakuma/B3TradingPlatform/issues/527)
> · Interacts with `user-bot-fixp-mtls-v0`, `docker-compose.public.yml` (#531),
> and the listener boot-guard.

## 1. Context

`docker-compose.public.yml` (#531) makes the trading-host exposable on the
public internet: REST/WS API + the inbound FIXP listener (default `:5001`)
under TLS+mTLS. Two surfaces now share one process: the **public bot port**
and the **operator/admin API**. Upstream, the host holds **internal firm
sessions** to the matching platform. This RFC pins the deployment topology so
the edge posture, mTLS termination, and drain behavior are deliberate, not
emergent.

## 2. Goals

- Decide the **TLS/mTLS termination point** and its blast radius.
- Segregate the public FIXP listener from the internal API/admin surface.
- Define firewall / security-group shape and where rate-limiting lives.
- Define health/drain interaction for a publicly-exposed listener.

## 3. Non-goals

- Cloud-provider specifics (this is a B3 *simulation*; map to ALB/NLB/etc. per
  deployment). Autoscaling. Multi-region. WAF rule authoring.

## 4. TLS/mTLS termination — the load-bearing decision

mTLS client-cert validation (#540, mTLS RFC §4) happens **in-process** at the
listener: it inspects the client leaf thumbprint and binds it to the
credential. That forces one of:

- **L4 passthrough (recommended).** NLB/L4 forwards TCP untouched; the
  trading-host terminates TLS and validates client certs. mTLS pin works
  unchanged. Edge does connection caps only.
- **L7 terminate-and-forward.** A proxy terminates TLS, must re-present the
  client cert (PROXY-protocol / header forwarding) — added complexity, header
  spoofing risk, and the boot-guard's "Required" assumptions break. Rejected
  for v0.

**Decision: terminate TLS+mTLS in the listener; edge is L4 passthrough.** The
boot-guard already requires `Tls.Required` + cert/CA, consistent with this.

## 5. Surface segregation

- Bind FIXP `:5001` on the public segment; REST/WS API `:5000` stays internal
  (operator VPN / private subnet). Two ports, two security groups, one process
  v0.
- v1 option: split into separate host instances (public-listener vs
  api/admin) sharing the WAL — deferred; in-process split + firewall is the v0
  cut, matching the single trading-host container today.
- Internal firm sessions egress only to the matching peer — never exposed.

## 6. Firewall / rate-limit topology

- Edge: per-IP **connection** caps (L4) — coarse flood shield.
- In-process: `ConnectionGate` concurrent caps + `AcceptConnectionRateLimiter`
  (#529) + per-min Negotiate limiter — the precise, per-credential line.
- Defense-in-depth: edge absorbs volumetric, in-process enforces policy. Alert
  reasons (#533) distinguish edge vs in-process drops.

## 7. Health / drain

- Public listener must drain gracefully: stop accepting, let in-flight
  sessions finish or Terminate within `OutboundDrainShutdownTimeout`, then
  close. Health endpoint stays internal; expose only a TCP liveness for the
  L4 LB. `entrypoint_listener.enabled` + sessions_active drive drain alerting
  (#533).

## 8. Open questions

- One process vs split public/internal hosts at scale? PROXY-protocol if a
  managed NLB can't do pure passthrough? Source-IP allow-list at edge for an
  early invite-only beta?

## 9. Decisions summary

L4 passthrough, terminate mTLS in listener; two ports/two SGs/one process;
defense-in-depth rate-limit; graceful drain with internal-only health.
