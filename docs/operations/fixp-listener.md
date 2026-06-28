# FIXP Listener — Operations Guide

> Part of the [User-bot FIXP listener v0 RFC](../rfcs/user-bot-fixp-listener-v0.md).
> Tracking: [#166](https://github.com/pedrosakuma/B3TradingPlatform/issues/166).

## Overview

The FIXP listener exposes a native B3 EntryPoint–compatible inbound channel on the
trading-host. External bots connect via FIXP/SBE, authenticate with platform-issued
credentials, and submit orders through the same post-auth pipeline as REST and WebSocket.

## Enabling in Production

The listener is **disabled by default**. To enable:

```env
Trading__EntryPointListener__Enabled=true
Trading__EntryPointListener__Endpoint=0.0.0.0:5001
Trading__EntryPointListener__AllowInProduction=true
Trading__EntryPointListener__Tls__Required=true
Trading__EntryPointListener__Tls__CertPath=/etc/ssl/fixp/server.crt
Trading__EntryPointListener__Tls__KeyPath=/etc/ssl/fixp/server.key
# Optional mTLS second factor for public bot access:
Trading__EntryPointListener__Tls__ClientCertificateMode=Required
Trading__EntryPointListener__Tls__ClientCa__BundlePath=/etc/ssl/fixp/bot-ca-bundle.pem
Trading__EntryPointListener__Tls__ClientCa__DenyListPath=/etc/ssl/fixp/bot-denylist.txt
Trading__EntryPointListener__Tls__ClientCa__ReloadInterval=00:05:00
Trading__EntryPointListener__Tls__RequireClientAuthEku=true
```

### Boot guard

In `Environment=Production`, the host refuses to start unless ALL of:

- `Enabled=true`
- `AllowInProduction=true`
- `Tls:Required=true`
- `Tls:CertPath` is set (and for PEM certs, `Tls:KeyPath` is also set)

PFX/P12 users can put the `.pfx` path in `CertPath` and leave `KeyPath` empty.

## TLS Setup

### PEM (recommended)

```bash
# Generate a dev self-signed cert
openssl req -x509 -newkey rsa:2048 -keyout server.key -out server.crt \
  -days 365 -nodes -subj '/CN=localhost'
```

Configuration:

```env
Trading__EntryPointListener__Tls__CertPath=/path/to/server.crt
Trading__EntryPointListener__Tls__KeyPath=/path/to/server.key
```

### PFX/PKCS#12

```bash
openssl pkcs12 -export -out server.pfx -inkey server.key -in server.crt
```

Configuration:

```env
Trading__EntryPointListener__Tls__CertPath=/path/to/server.pfx
Trading__EntryPointListener__Tls__Password=your-pfx-password
```

### Let's Encrypt

Use certbot or ACME clients to obtain PEM files, then point `CertPath` and `KeyPath`
at the fullchain and privkey files. Renewal requires a host restart (v0 does not
hot-reload certs).

## mTLS client certificates

The listener can require a trusted bot client certificate in addition to the
bot PAT:

| Setting | Values / default | Description |
|---------|------------------|-------------|
| `Tls:ClientCertificateMode` | `None`, `Optional`, `Required` (`None`) | `Optional` observes cert adoption; `Required` rejects clients without a valid cert. |
| `Tls:ClientCa:BundlePath` | path | Concatenated PEM bundle of trusted bot CA certificates. |
| `Tls:ClientCa:DenyListPath` | path or empty | SHA-256 leaf thumbprint deny-list. |
| `Tls:ClientCa:ReloadInterval` | `00:05:00` | Hot-reload cadence for the bundle and deny-list. |
| `Tls:RequireClientAuthEku` | `true` | Require `clientAuth` EKU on client leaf certificates. |
| `AllowInsecureMtlsInProduction` | `false` | Explicit escape hatch for less-secure production mTLS posture. |

Provision a bot CA out of band, issue each bot a client leaf certificate, and
mount only the CA bundle and deny-list into the trading-host. Bot private keys
belong only in bot runtimes.

For CA rotation, concatenate old and new CA PEMs in the bundle, wait one
`ReloadInterval`, move bots to leaves issued by the new CA, then remove the
old CA. The listener picks this up without a restart.

For fast revocation, add the leaf's SHA-256 thumbprint to the deny-list. The
format is one 64-character SHA-256 hex thumbprint per line; uppercase is
canonical, separators are ignored, and blank lines / `#` comments are allowed.
New handshakes using a denied leaf fail after the next reload.

## Rate Limiting

Token-bucket rate limiting protects the Negotiate endpoint:

| Setting | Default | Description |
|---------|---------|-------------|
| `RateLimit:NegotiatesPerMinutePerIp` | 30 | Per source IP |
| `RateLimit:NegotiatesPerMinutePerUsername` | 10 | Per credential (post-auth) |
| `AcceptRateLimit:ConnectionsPerSecondPerIp` | 0 | Opt-in accept-loop connection rate limit; `0` disables it |
| `AcceptRateLimit:BurstPerIp` | 30 | Burst size for the accept-loop limiter |

### Tuning

- For N bots each reconnecting every 5 minutes: set per-IP ≥ N×12/min safety margin.
- Per-credential limits protect against a bot in a tight reconnect loop.
- Tokens refill continuously at the configured rate per minute.
- The accept-loop limiter is disabled by default. For public exposure, prefer
  upstream LB / WAF / firewall connection-rate controls and tune the in-process
  limiter only as an additional guard.

## Max Sessions Per User

```env
Trading__EntryPointListener__MaxSessionsPerUser=3
```

A user with 3 active sessions will have their 4th Negotiate rejected. Sessions are
released on Terminate or socket close.

## Monitoring / Metrics

All instruments use the `B3.Trading` meter (subscribe with OTel or Prometheus).

| Metric | Type | Tags | Description |
|--------|------|------|-------------|
| `entrypoint_listener.enabled` | UpDownCounter | — | 1 when listener active |
| `entrypoint_listener.sessions_active` | UpDownCounter | — | Current established sessions |
| `entrypoint_listener.negotiate_total` | Counter | `outcome` | Negotiate outcomes |
| `entrypoint_listener.orders_in_total` | Counter | `kind`, `outcome` | Inbound orders |
| `entrypoint_listener.er_out_total` | Counter | — | Outbound ERs routed |
| `entrypoint_listener.er_outbound_buffered` | UpDownCounter | — | Buffered messages |
| `entrypoint_listener.er_outbound_dropped_total` | Counter | — | Overflow drops |
| `entrypoint_listener.retransmit_requests_total` | Counter | `outcome` | Retransmit outcomes |
| `fixp.handshake.tls.completed.total` | Counter | — | TLS handshakes |
| `fixp.connections.rejected.total` | Counter | `reason` | Rejected connections |

### Prometheus query examples

```promql
# Active sessions
entrypoint_listener_sessions_active

# Negotiate reject rate (5m)
rate(entrypoint_listener_negotiate_total{outcome=~"reject:.*"}[5m])

# TLS failure rate
rate(fixp_connections_rejected_total{reason="tls"}[5m])
```

## Admin Endpoints

All under `/admin/fixp`, require `admin` role.

### List sessions

```bash
curl -H "Authorization: Bearer $TOKEN" http://localhost:5000/admin/fixp/sessions
```

### Bump session version

```bash
curl -X POST -H "Authorization: Bearer $TOKEN" \
  http://localhost:5000/admin/fixp/sessions/{credentialId}/bump
```

### Force-terminate a session

```bash
curl -X POST -H "Authorization: Bearer $TOKEN" \
  http://localhost:5000/admin/fixp/sessions/{credentialId}/terminate
```

### Inspect outbound buffer

```bash
curl -H "Authorization: Bearer $TOKEN" \
  http://localhost:5000/admin/fixp/credentials/{credentialId}/buffer
```

## Troubleshooting

### Common reject reasons

| Reason | Cause | Fix |
|--------|-------|-----|
| `CREDENTIALS` | Bad PAT token | Regenerate credential in UI |
| `RATE_LIMIT_IP` | Too many Negotiates from same IP | Wait or increase limit |
| `RATE_LIMIT_CREDENTIAL` | Too many Negotiates for same credential | Slow reconnect loop |
| `MAX_SESSIONS_PER_USER` | User hit session cap | Close other sessions first |
| `INVALID_SESSIONVERID` | Version mismatch | Use version from NegotiateResponse |
| `SESSION_BLOCKED` | Single-active violation | Previous session not cleanly closed |

### Bumping session version

Use the admin endpoint or the credential's version is auto-bumped on single-active
violations and buffer overflows.

### Reading the outbound buffer

The `/admin/fixp/credentials/{id}/buffer` endpoint shows:
- `size`: number of buffered messages
- `isOverflowed`: true if the buffer has overflowed (version was bumped)

## Bot-Author Notes

- **PAT format**: `b3t_<shortId>_<secret>` — paste into SDK's Credentials field.
- **Handshake flow**: Negotiate → NegotiateResponse → Establish → EstablishAck.
- **Retransmit**: Send RetransmitRequest with (fromSeq, count) to replay missed ERs.
- **Overflow**: If the server buffer overflows, the session version is bumped and the
  bot must reconnect with the new version. Reconcile state via REST `/api/orders`.
- **Heartbeat**: Server sends Sequence on configured cadence (default 3s).

## Out of v0

The following are explicitly deferred:
- Mass-cancel on disconnect
- WAL-persisted outbound buffer
