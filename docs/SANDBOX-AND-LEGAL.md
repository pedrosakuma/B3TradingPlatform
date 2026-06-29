# Sandbox & legal framing

> Tracking: [#535](https://github.com/pedrosakuma/B3TradingPlatform/issues/535)
> · Epic: [#527](https://github.com/pedrosakuma/B3TradingPlatform/issues/527).
> Read before exposing the user-bot FIXP listener to external operators.

## 1. What this platform is

`B3TradingPlatform` is a **simulation** of the B3 exchange participant
(corretora) stack. It connects to a **B3 matching simulator**, not the real
B3 venue. There is:

- **No real money.** Positions, cash, fills, statements are synthetic.
- **No regulated brokerage.** This is not a CVM-registered broker, not an
  investment service, and issues no real financial instruments or advice.
- **No real market access.** Orders never reach a live exchange.

It exists for protocol conformance, algo development, and operational
rehearsal against a B3-compatible FIXP/SBE wire — a sandbox.

## 2. Implications for public bot exposure

When the FIXP listener is exposed (epic #527), external operators connect bots
with platform-issued credentials (mTLS optional). Even though no money is at
risk, treat it as untrusted-internet: the hardening (TLS+mTLS, connection caps,
rate limits, kill-switch, fuzz coverage) protects availability/integrity of the
simulation, not assets.

## 3. Operator onboarding — disclaimer / ToS gate

Before issuing a bot credential, the operator must acknowledge:

- This is a **simulation**; no real money, orders, or market access.
- Credentials are personal, non-transferable; misuse → revoke + kill-switch.
- No SLA / availability guarantee; sessions may be terminated for abuse.
- No financial advice or regulated service is provided.
- Connections may be rate-limited, cert-pinned, or denied at any time.

Gate credential issuance on recorded acceptance (out-of-band sign-off or an
acceptance flag at provisioning) until an in-app ToS gate ships.

## 4. Not in scope

Real funds, custody, regulatory registration, KYC/AML, tax — none apply to a
simulation. If real money/market access is ever introduced this framing is
void and a regulatory review is mandatory.

## 5. Cross-references

- Tenant lifecycle / incident response — [`RUNBOOK.md`](RUNBOOK.md) §3.
- mTLS / rotation / edge-topology RFCs — [`rfcs/`](rfcs/).
- Public overlay secrets — [`operations/fixp-listener.md`](operations/fixp-listener.md).
