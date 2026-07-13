# RFC: User-bot FIXP edge mTLS / client-cert auth v0

| Field     | Value                                                               |
| --------- | ------------------------------------------------------------------- |
| Status    | Proposed                                                            |
| Tracking  | [#528](https://github.com/pedrosakuma/B3TradingPlatform/issues/528) |
| Refs      | [#527](https://github.com/pedrosakuma/B3TradingPlatform/issues/527) (go-public epic) |
| Builds on | [`user-bot-fixp-listener-v0`](./user-bot-fixp-listener-v0.md) (§4.5 auth, §4.9 config, §4.11 prod safety); pre-named in that RFC §9 |

## 1. Context

The user-bot FIXP listener (`user-bot-fixp-listener-v0`) shipped a complete
inbound adapter: TCP + SOFH framing, the FIXP handshake state machine,
PAT-style credential auth, ClOrdId namespacing, outbound ER routing and
retransmit. Its transport security is **server-only TLS** — the listener
presents a server certificate and wraps the socket in an `SslStream`, but
explicitly does **not** ask the client for a certificate:

```csharp
// FixpListenerHostedService.cs:183
await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
{
    ServerCertificate = _tlsCert,
    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
    ClientCertificateRequired = false,   // ← bot identity rests entirely on the PAT
}, handshakeCts.Token);
```

So today a bot's identity rests **entirely on the PAT** carried in the FIXP
`Negotiate.Credentials` buffer (`FixpSessionConnection.cs:484-498`). The PAT
is a strong bearer secret, but it is a *single* factor: anyone who can reach
the listener port and holds a leaked token is admitted up to the point of
credential verification, and credential-stuffing / token-spray traffic
reaches the application layer before being rejected.

For a **public deployment** (#527) we want defense-in-depth: a client
certificate as a **second factor and network-admission gate**, validated at
the TLS layer *before* the PAT is even read. A connection without a valid,
trusted client certificate is dropped during the handshake — it never reaches
`HandleNegotiateAsync`, never consumes a rate-limit token, never touches the
credential store.

### 1.1 Good news — no upstream blocker

This is a **listener-side-only** change. The bot-side SDK already supports
presenting a client certificate end-to-end:

| Capability | Where (SDK 0.16.0) |
| --- | --- |
| Present client cert(s) | `B3.EntryPoint.Client` `TlsOptions.ClientCertificates` |
| Pin/validate server cert | `TlsOptions.RemoteCertificateValidationCallback` |
| SNI / target host | `TlsOptions.TargetHost` |
| Enable TLS at all | `TlsOptions.Enabled` |

(Members verified present in `b3.entrypoint.client/0.16.0`; pinned in
`backend/Directory.Packages.props:7`.) The matching server-side primitive is
already in the BCL: `SslServerAuthenticationOptions.ClientCertificateRequired`
+ `RemoteCertificateValidationCallback` — the same `SslStream` the listener
already uses.

### 1.2 What exists today (grounding)

| Concern | Where | Note |
| --- | --- | --- |
| Server-only TLS handshake | `FixpListenerHostedService.cs:172-198` | `ClientCertificateRequired = false`; 5 s handshake timeout; `tls` reject metric on failure. |
| TLS config | `EntryPointListenerOptions.TlsOptions` | `CertPath`, `KeyPath`, `Required`, `Password`, `IsPfx`. No client-cert surface. |
| TLS validator | `EntryPointListenerOptionsValidator.cs:48-61` | Cert/key existence + PEM-needs-KeyPath checks, gated on `Tls.Required`. |
| Prod boot guard | `EntryPointListenerBootGuard.Validate` | Refuses Prod unless `AllowInProduction` + `Tls.Required` + cert/key paths. |
| PAT auth | `FixpSessionConnection.cs:484-518` | `IUserBotCredentialRegistry.TryAuthenticateAsync(token)` → resolves `UserBotCredential`; rejects → `NegotiateReject(Credentials)`. |
| Credential entity | `Application/UserBots/UserBotCredential.cs` | `Id`, `UserId` (JWT `sub`), `CredShortId`, `Label`, `SecretHash`, `CreatedAtUtc`, `RevokedAtUtc`. **No cert binding field.** |
| Resolved scope | `FixpConnectionScope` / `Principal` | Carries `CredentialId` + `ClaimsPrincipal` for the rest of the session. |
| Handshake metrics | `FixpListenerMetrics` | `TlsHandshakeCompleted`, `ConnectionsRejected{reason}`, `NegotiateTotal{outcome}`. |

This RFC does **not** ship code. It locks the design, names the invariants
that must survive, sequences the work into shippable sub-issues, and surfaces
the open questions that need an answer before implementation.

## 2. Goals

1. **Edge admission gate.** A connection lacking a valid, trusted client
   certificate is rejected during the TLS handshake, before any application
   bytes (Negotiate/Credentials) are processed.
2. **Second factor.** When enabled, a bot must present *both* a trusted
   client certificate *and* a valid PAT. Compromise of one without the other
   does not grant order entry.
3. **Optional / phased rollout.** Three modes — `None` (today's behavior),
   `Optional` (request a cert, log presence, do not require), `Required`
   (reject without a valid cert) — so deployments can roll mTLS out
   observe-then-enforce without a flag-day.
4. **Cert ↔ credential binding (opt-in).** A credential *may* be pinned to a
   specific client-cert identity (thumbprint), so a leaked PAT cannot be used
   from an arbitrary cert and vice-versa. Unpinned credentials keep working
   under `Optional`/`Required` (any trusted cert + valid PAT).
5. **Production safety.** Extend the boot guard so a public deployment can
   *require* mTLS in Production, failing closed on misconfiguration — same
   shape as the existing `Tls.Required` guard.
6. **No upstream change, no SDK fork.** Validate against `B3.EntryPoint.Client`
   0.16.0 as a black-box client presenting a cert.

## 3. Non-goals

- **Replacing the PAT.** mTLS is *additive* defense-in-depth, not a
  replacement for the bearer credential. The PAT remains the source of the
  `UserBotCredential` → `UserId` → `ClaimsPrincipal` resolution.
- **A full internal PKI / online CA service.** v0 trusts an
  operator-provisioned CA bundle (file on disk). Issuing, signing, OCSP, and
  an automated enrollment API are explicitly deferred (see §9).
- **Per-credential CA isolation.** v0 has one trust anchor (or bundle) for the
  whole listener. Per-firm / per-tenant CAs are a future RFC.
- **CRL / OCSP revocation checking over the network.** v0 uses a static,
  operator-managed revocation list (thumbprint deny-list) reloaded with the
  CA bundle. Online revocation is deferred.
- **mTLS for REST / WebSocket.** Out of scope — those sit behind the existing
  JWT/2FA stack and (typically) a reverse proxy.

## 4. Trust-anchor / CA model

### 4.1 Who issues bot client certs

v0 assumes an **operator-run offline or near-line CA** (e.g. a `step-ca`
instance, an internal ADCS, or even a long-lived `openssl`-minted CA for a
small fleet). The platform does **not** issue certs in v0; it only *trusts* a
configured CA bundle and *binds* leaf certs to credentials. The bot operator:

1. Generates a keypair + CSR for the bot.
2. Gets it signed by the trusted CA (out of band — ticket, `step ca sign`,
   etc.).
3. Configures the SDK with the leaf cert + key
   (`TlsOptions.ClientCertificates`) and the platform's server-cert trust
   anchor (`TlsOptions.RemoteCertificateValidationCallback` / system store).

The recommended leaf-cert shape (advisory, enforced only loosely in v0):

- **CN / SAN**: the `CredShortId` of the credential it is bound to (so the
  cert is self-describing for ops), e.g. `CN=b3t-bot-<credShortId>`.
- **EKU**: `clientAuth` (1.3.6.1.5.5.7.3.2). v0 *may* require this EKU under
  `Required` mode (open question §7).
- **Validity**: operator policy; short-lived (≤90 d) encouraged. The platform
  enforces `NotBefore`/`NotAfter` via standard chain validation.

### 4.2 Chain validation

On the accept path, the validation callback runs the standard .NET chain
build against the **configured trust anchor only** (not the machine root
store — we do *not* want any publicly-trusted CA to be able to mint a bot
cert):

```
X509ChainPolicy {
  TrustMode             = CustomRootTrust,          // ignore the OS root store
  CustomTrustStore      = <loaded CA bundle>,       // Tls.ClientCa.* (§5)
  RevocationMode        = NoCheck,                   // v0: static deny-list, no OCSP/CRL
  VerificationFlags     = NoFlag,                    // full chain, time-valid, basic constraints
  ApplicationPolicy     = { clientAuth } (optional, §7),
}
```

A connection fails the gate (handshake aborts, `ConnectionsRejected{reason="mtls"}`)
when any of: no cert presented (under `Required`), chain does not build to the
custom anchor, cert is time-invalid, or the leaf thumbprint is on the static
deny-list.

### 4.3 Cert ↔ credential binding

Two binding strengths, chosen per credential:

| Binding | Stored on credential | Negotiate-time check |
| --- | --- | --- |
| **Unpinned** (default) | nothing new | Any cert that passes §4.2 chain validation is accepted; the PAT alone resolves the credential. |
| **Pinned** | `BoundCertThumbprint` (SHA-256 hex, nullable) | The presented client-cert leaf thumbprint **must equal** `BoundCertThumbprint`, *in addition* to PAT match. Mismatch → `NegotiateReject(Credentials)`. |

The binding is enforced at **Negotiate** time (not handshake time), because
that is where the credential is resolved and where we already have the
`ClaimsPrincipal` plumbing. The client cert captured during the TLS handshake
is stashed on the connection (`SslStream.RemoteCertificate`) and read back
when the PAT resolves to a `UserBotCredential`:

```
1. TLS handshake: validate cert against CA bundle (§4.2). Stash leaf X509Certificate2.
2. Negotiate: resolve PAT → UserBotCredential (existing path).
3. If cred.BoundCertThumbprint is non-null:
     require leaf.Thumbprint == cred.BoundCertThumbprint (constant-time compare)
     else NegotiateReject(Credentials) + structured log {event:"fixp.mtls.binding_mismatch"}.
4. Proceed.
```

`BoundCertThumbprint` is set at credential-creation / edit time via the
existing user-bot-credential REST surface (`UserBotCredentialsEndpoints.cs`).
It is **non-secret** (a thumbprint), shown in the credential list UI, and
nullable so existing credentials keep working.

> **Why thumbprint, not CN?** CN/SAN are operator-chosen and mutable across
> re-issues; the thumbprint is a precise pin. The trade-off is that cert
> rotation requires re-pinning (§4.4). For fleets that rotate often, leaving
> the credential **unpinned** (chain-validation only) is the right default —
> pinning is for high-value credentials.

### 4.4 Rotation / revocation

- **Cert rotation (pinned credential).** Re-issue the leaf, then update
  `BoundCertThumbprint` via the credential-edit endpoint. To avoid a flag-day,
  the endpoint accepts an *optional second* `PendingCertThumbprint` — during an
  overlap window the Negotiate check accepts **either** thumbprint, then the
  operator promotes pending→bound and clears pending. (Mirrors the
  rotation-overlap idea sketched for PATs in `user-bot-fixp-rotation-v0`,
  §9.) v0 *may* ship single-thumbprint only and defer overlap (open
  question §7).
- **Cert rotation (unpinned).** No platform action — the new cert just has to
  chain to the same trusted CA.
- **Cert revocation.** Add the leaf thumbprint to the operator-managed
  **deny-list** (a file alongside the CA bundle, reloaded on the same cadence,
  §5.2). A denied thumbprint fails §4.2 at the handshake gate for *all*
  credentials. This is the fast, network-free path; CRL/OCSP is deferred.
- **CA rotation.** Add the new CA to the bundle (bundle = concatenation of
  PEMs) during the overlap, re-issue leaves under the new CA, then drop the
  old CA from the bundle. Bundle reload is hot (§5.2), no restart.
- **Credential revocation** (existing) is unchanged: `RevokedAt` on the
  `UserBotCredential` still rejects at Negotiate regardless of cert validity.

## 5. Config surface

Extends `EntryPointListenerOptions.TlsOptions` (the `Trading:EntryPointListener:Tls`
section). New fields, all backward-compatible (defaults preserve today's
behavior):

```jsonc
"Trading": {
  "EntryPointListener": {
    "Tls": {
      "CertPath": "/certs/server.pfx",   // existing — server leaf
      "KeyPath": null,                    // existing
      "Required": true,                   // existing — server TLS on
      "Password": null,                   // existing

      // ── new in mtls-v0 ──
      "ClientCertificateMode": "None",    // None | Optional | Required  (default None)
      "ClientCa": {
        "BundlePath": null,               // PEM bundle of trusted issuer CA(s)
        "DenyListPath": null,             // optional newline-delimited SHA-256 thumbprints
        "ReloadInterval": "00:05:00"      // hot-reload cadence for bundle + deny-list
      },
      "RequireClientAuthEku": true        // require clientAuth EKU on leaf (Required mode)
    },
    "AllowInsecureMtlsInProduction": false // explicit opt-in escape hatch (mirrors AllowInProduction)
  }
}
```

`ClientCertificateMode`:

| Mode | Handshake | Negotiate | Use |
| --- | --- | --- | --- |
| `None` | `ClientCertificateRequired=false` (today) | PAT only | Default; unchanged behavior. |
| `Optional` | request cert; validate **if present**; allow if absent | PAT only; pinned creds enforce thumbprint **only when a cert was presented** | Observe-then-enforce rollout; measure adoption via metrics before flipping to `Required`. |
| `Required` | `ClientCertificateRequired=true`; reject if absent or chain-invalid | PAT + (pin if set) | Enforced public deployment. |

> `ClientCertificateMode` is meaningful only when `Tls.Required=true` (you
> cannot do mTLS without TLS). The validator (§5.1) rejects
> `ClientCertificateMode != None` with `Tls.Required=false`.

### 5.1 Validator rules (extend `EntryPointListenerOptionsValidator`)

When `Enabled=true`:

- `ClientCertificateMode != None` requires `Tls.Required=true`.
- `ClientCertificateMode != None` requires `ClientCa.BundlePath` set and the
  file to exist and parse as ≥1 X509 cert.
- `ClientCa.DenyListPath`, if set, must exist (contents may be empty).
- `ClientCa.ReloadInterval > TimeSpan.Zero`.
- Parse failures (unreadable bundle, zero certs) → fail closed at boot.

### 5.2 Hot reload

The CA bundle and deny-list are loaded into an atomically-swapped
`X509Certificate2Collection` + `HashSet<string>` behind a thread-safe holder,
refreshed every `ReloadInterval` (and once at startup). This lets operators
add a CA, rotate, or revoke a thumbprint **without restarting the listener** —
important for a public service. The validation callback reads the current
snapshot per-handshake. (Reuses the file-watch/poll pattern; implementation
detail for the sub-issue.)

## 6. Implementation sketch (listener side)

The single behavioral seam is the handshake block in
`FixpListenerHostedService.HandleAcceptedClientAsync` (`:172-198`):

```csharp
var caSnapshot = _clientCaProvider.Current;   // hot-reloaded bundle + deny-list (§5.2)
await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
{
    ServerCertificate = _tlsCert,
    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
    ClientCertificateRequired = _opts.Tls.ClientCertificateMode == Required,
    RemoteCertificateValidationCallback = (s, cert, chain, errors) =>
        ValidateClientCert(cert, _opts.Tls.ClientCertificateMode, caSnapshot, out _),
}, handshakeCts.Token);
```

- `ValidateClientCert` builds a chain with `CustomRootTrust` against the
  snapshot (§4.2), checks the deny-list, optionally checks the `clientAuth`
  EKU, and — under `Optional` with no cert — returns `true` (admit, no cert).
- The validated leaf (`sslStream.RemoteCertificate as X509Certificate2`) is
  passed into `FixpSessionConnection` so the Negotiate path can enforce the
  per-credential thumbprint pin (§4.3). New ctor/scope param:
  `X509Certificate2? ClientCertificate`.
- New reject reason on the existing metric:
  `ConnectionsRejected{reason="mtls"}`; new structured logs
  `fixp.mtls.handshake.{completed,rejected}` and
  `fixp.mtls.binding_mismatch` (all carry `remote` + non-secret thumbprint,
  never the PAT).
- New metric `entrypoint_listener.mtls_client_certs_total{outcome=ok|reject:<reason>|absent}`
  so `Optional`-mode adoption is measurable before flipping to `Required`.

### 6.1 Domain / credential change

`UserBotCredential` gains a nullable `BoundCertThumbprint` (and, if §4.4
overlap ships, `PendingCertThumbprint`). These flow through the existing
registry (`IUserBotCredentialRegistry`), WAL event, snapshot, and the REST
CRUD + UI. Both are non-secret SHA-256 hex strings; null = unpinned.

## 7. Production safety / boot guard

Extend `EntryPointListenerBootGuard.Validate` (the existing Prod fail-closed
guard) with mTLS-aware rules:

1. Existing rules unchanged (`AllowInProduction`, `Tls.Required`, cert/key
   paths).
2. **New, advisory→enforced:** in Production, if `ClientCertificateMode` is
   `None` or `Optional`, the guard *warns loudly* but boots (mTLS is opt-in,
   not yet mandatory platform-wide). A deployment that wants to **mandate**
   mTLS sets nothing extra — it just configures `Required`; the guard then
   verifies `ClientCa.BundlePath` is present and parses, else refuses to boot.
3. **Escape hatch:** `AllowInsecureMtlsInProduction=true` is required to run
   `Required` mode *without* a deny-list configured, or to silence the
   `None`/`Optional`-in-Production warning — mirrors the
   `AllowInProduction`/`AllowErInjection` opt-in shape so "less secure than the
   default public posture" is always an explicit, audited config choice.
4. `BuildWarning` gains an mTLS line: e.g.
   `mTLS: Required (CA bundle: /certs/bot-ca.pem, deny-list: 3 entries)` or
   `⚠ mTLS: None — bot identity rests on PAT alone`.

The decision of whether mTLS is *mandatory* for the public launch posture is
an ops/policy call captured in the epic (#527), not hard-coded here — the guard
makes the *misconfiguration* of a chosen mode fail closed, not the choice of
mode itself.

## 8. Conformance

Add to `B3.Trading.Conformance` (real-stack, profile-gated) — and
fast unit/integration coverage in `B3.Trading.EntryPointListener.Tests`:

| Test | Mode | Expectation |
| --- | --- | --- |
| SDK client **with** trusted leaf | `Required` | Handshake completes; Negotiate+PAT accepted; order round-trips. |
| SDK client **without** cert | `Required` | Handshake aborts; `ConnectionsRejected{reason="mtls"}`; never reaches Negotiate. |
| SDK client with **untrusted** cert (wrong CA) | `Required` | Handshake aborts (chain fails custom-root). |
| SDK client with **time-invalid** cert | `Required` | Handshake aborts. |
| SDK client with **denied** thumbprint | `Required` | Handshake aborts even though chain is valid. |
| Cert present but **wrong pin** | `Required`, pinned cred | Handshake ok; `NegotiateReject(Credentials)`; `fixp.mtls.binding_mismatch`. |
| Cert present, **correct pin** | `Required`, pinned cred | Accepted. |
| No cert | `Optional` | Admitted; metric `outcome=absent`; PAT-only path works. |
| Hot-reload: add thumbprint to deny-list | `Required` | New connections with that thumbprint rejected within `ReloadInterval` without restart. |

The conformance harness drives `B3.EntryPoint.Client` 0.16.0 configured with
`TlsOptions.ClientCertificates` against the listener-as-black-box, exactly the
"SDK-as-client" shape used by `user-bot-fixp-listener-v0` sub-issue H. Per
`AGENTS.md`, conformance failures here are treated as real regressions.

## 9. Sub-issue decomposition

| # | Title | Scope | Independent? |
| --- | --- | --- | --- |
| A | RFC (this document) | design lock | n/a |
| B | Config + validator + hot-reload CA provider | `TlsOptions` fields, `EntryPointListenerOptionsValidator` rules (§5.1), atomically-swapped CA-bundle/deny-list provider (§5.2) | yes (after A) |
| C | Handshake gate | `ValidateClientCert`, wire `ClientCertificateMode` into `AuthenticateAsServerAsync`, `mtls` reject reason + metrics + logs (§6) | needs B |
| D | Credential cert-binding | `UserBotCredential.BoundCertThumbprint` (+ optional pending), registry/WAL/snapshot, REST CRUD + UI, Negotiate-time pin check (§4.3, §6.1) | needs B; parallel with C |
| E | Boot-guard extension | Prod mTLS rules + `AllowInsecureMtlsInProduction` + warning line (§7) | needs B |
| F | Conformance + hardening | SDK-as-client mTLS suite (§8), docs (RUNBOOK + DOCKER overlay vars) | needs C + D + E |

After A, B unblocks everything; C and D run in parallel.

## 10. Risks and mitigations

### 10.1 Mis-set `Required` locks every bot out

Flipping straight to `Required` before bots have certs is a self-inflicted
outage. **Mitigation:** the `Optional` mode + `mtls_client_certs_total`
metric exist precisely to roll out observe-then-enforce: deploy `Optional`,
watch `outcome=absent` fall to zero, *then* flip to `Required`.

### 10.2 Trusting the machine root store by accident

If the validation callback fell back to the OS root store, any
publicly-trusted CA could mint a bot cert. **Mitigation:** `CustomRootTrust`
+ `CustomTrustStore` only (§4.2); never `X509ChainTrustMode.System`. A unit
test asserts a cert chaining to a public root is rejected.

### 10.3 Pin brittleness on rotation

Thumbprint pins break on every re-issue. **Mitigation:** unpinned is the
default (chain-validation is already strong); pinning is opt-in for
high-value creds; the optional pending-thumbprint overlap (§4.4) covers
zero-downtime rotation for those.

### 10.4 No online revocation

A compromised-but-unexpired leaf is valid until the operator acts.
**Mitigation:** the static deny-list (§4.4) is the fast network-free kill
path, hot-reloaded within `ReloadInterval`; full CRL/OCSP is deferred to a
future RFC where the latency/availability trade-offs can be designed
properly.

### 10.5 Handshake-time cost / DoS

Chain building per handshake costs CPU; a flood of TLS handshakes could be a
DoS vector. **Mitigation:** the existing 5 s handshake timeout bounds each
attempt; the per-IP Negotiate rate-limit does *not* cover pre-Negotiate
handshakes, so a connection-rate limit at the accept loop (or upstream
LB/firewall) is recommended for public exposure — noted for sub-issue F, may
spill into a follow-up.

## 11. Open questions

- **Enforce `clientAuth` EKU?** `RequireClientAuthEku` defaults true under
  `Required`. Some minimal CAs omit EKUs; confirm the bot fleet's certs carry
  it before defaulting on, or make it advisory-log-only in v0.
- **Ship rotation overlap (`PendingCertThumbprint`) in v0 or defer?** Adds a
  field + endpoint shape. Lean: defer to `user-bot-fixp-rotation-v0` unless an
  early pinned-cred user needs zero-downtime rotation immediately.
- **CN-derived binding as an alternative to thumbprint pin?** A
  `CN must equal b3t-bot-<credShortId>` convention would auto-bind without an
  explicit pin and survive rotation, at the cost of trusting the CA's naming
  discipline. Could ship *alongside* thumbprint pinning as a softer binding
  mode. Decide in sub-issue D.
- **Where does the connection-rate limit live (§10.5)?** Listener accept loop
  vs. assume an upstream LB/WAF for public exposure. Probably the latter for
  v0; confirm with the #527 deployment topology.

## 12. Compatibility / migration

Nothing to migrate. Every new field defaults to today's behavior
(`ClientCertificateMode=None`, `BoundCertThumbprint=null`). Existing
deployments and existing credentials are unaffected until an operator opts in.
`None → Optional → Required` is a pure config progression with no schema
migration; the only persisted addition is the nullable `BoundCertThumbprint`
on `UserBotCredential`, which absent-defaults for every existing row.

## 13. Future RFCs unblocked by this one

- **user-bot-fixp-pki-v0** — platform-issued certs: an enrollment API, online
  signing, automated short-lived leaf issuance (ACME-style), per-firm CAs.
- **user-bot-fixp-revocation-online-v0** — CRL / OCSP / OCSP-stapling for
  network revocation instead of the static deny-list.
- **user-bot-fixp-rotation-v0** — overlap-window rotation for *both* PAT and
  cert pins (already pre-named in the listener RFC §9).
