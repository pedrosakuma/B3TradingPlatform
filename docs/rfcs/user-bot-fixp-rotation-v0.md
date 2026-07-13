# RFC: user-bot-fixp-rotation-v0 — credential rotation & lifecycle

> Status: **Draft** · Tracking: [#530](https://github.com/pedrosakuma/B3TradingPlatform/issues/530)
> · Epic: [#527](https://github.com/pedrosakuma/B3TradingPlatform/issues/527)
> · Pre-named in `user-bot-fixp-listener-v0` §9, mirrored by
> `user-bot-fixp-mtls-v0` §4.4.

## 1. Context

`UserBotCredential` today supports **create + revoke + (optional) cert-pin**
only. The PAT (`b3t_<shortId>_<secret>`) is minted once, bcrypt-hashed, and
lives until an operator soft-revokes it. For a private/internal listener that
is fine; for a **public** surface it is a liability:

- A leaked PAT is valid forever until someone notices and revokes it — no
  expiry, no "rotate every 90 days" hygiene.
- Rotation today = create-new + revoke-old, which is a **flag-day**: the bot
  is down between switching tokens. Public operators want overlap.
- No "last used / last IP" signal, so a stale or compromised credential is
  invisible until abuse.

### 1.1 What exists today (grounding)

- `UserBotCredential` record: `Id, UserId, CredShortId, Label, SecretHash,
  CreatedAtUtc, RevokedAtUtc?, BoundCertThumbprint?`
  (`backend/src/B3.Trading.Application/UserBots/UserBotCredential.cs`).
- `IUserBotCredentialRegistry`: `CreateAsync`, `SetBoundCertThumbprintAsync`,
  `RevokeAsync`, `ListByUser`, `TryAuthenticateAsync` — the listener only ever
  calls `TryAuthenticateAsync`, so the read path is the stability seam.
- Persistence: bcrypt(cost=12) hash to WAL + snapshot; revoke is a soft flag.
- mTLS pin (#540): optional `BoundCertThumbprint`; overlap-window cert rotation
  is already pre-sketched in mTLS RFC §4.4 (pending-thumbprint).

## 2. Goals

- **Overlap-window rotation**: issue a new secret while the old still
  authenticates for a grace period, then auto-revoke the old. Zero downtime.
- **Optional `ExpiresAt`**: a credential can carry an expiry; auth fails closed
  after it, surfaced in list as a distinct state.
- **Last-used tracking**: `LastUsedAtUtc` + `LastSeenIp` so operators can spot
  dormant or anomalous credentials.
- REST + UI rotate flow; WAL/snapshot persistence of new fields, backward
  compatible (old records deserialize with new fields null).

## 3. Non-goals

- Automatic time-based rotation / scheduler — operator-initiated only in v0.
- Secret vault integration / external KMS.
- mTLS cert rotation overlap (owned by mTLS RFC §4.4, separate `Pending`
  thumbprint) — this RFC is PAT-secret overlap only; the two compose.
- Hard delete of credentials (audit trail stays append-only, mirrors revoke).

## 4. Model changes

Extend the record additively — all new fields nullable so existing
WAL/snapshot rows hydrate unchanged:

```csharp
public sealed record UserBotCredential(
    Guid Id, string UserId, string CredShortId, string Label, string SecretHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RevokedAtUtc = null,
    string? BoundCertThumbprint = null,
    // rotation-v0:
    string? PendingSecretHash = null,        // 2nd secret valid during overlap
    DateTimeOffset? RotationDeadlineUtc = null, // old hash auto-revoked after
    DateTimeOffset? ExpiresAt = null,        // hard expiry, fail-closed
    DateTimeOffset? LastUsedAtUtc = null,
    string? LastSeenIp = null);
```

`CredShortId` is **stable across rotation** — the public id embedded in the PAT
does not change, only the secret half does, so O(1) lookup is preserved.

## 5. Auth-path semantics (`TryAuthenticateAsync`)

1. Resolve by `CredShortId` (unchanged).
2. Reject if `RevokedAtUtc` set or `ExpiresAt` < now (fail-closed).
3. bcrypt-verify the presented secret against `SecretHash`; if no match and
   `PendingSecretHash` set and `now < RotationDeadlineUtc`, verify against it.
4. On success record `LastUsedAtUtc`/`LastSeenIp` (write-coalesced, see §8).
5. mTLS pin (#540) still applies on top, unchanged.

After `RotationDeadlineUtc`, a sweep promotes pending→primary (or the rotate is
finalised) and the old hash stops verifying. Both secrets accepted only inside
the window.

## 6. Rotation lifecycle

- **Rotate**: mint a new secret → store its hash as `PendingSecretHash`, set
  `RotationDeadlineUtc = now + GracePeriod`, return the new plaintext once. Old
  secret stays primary.
- **Finalise** (operator) or **auto** (deadline sweep): pending→`SecretHash`,
  clear pending, advance — old secret dead. Idempotent.
- **Abort**: clear pending before finalise. Old keeps working.
- Composes with mTLS: secret overlap + cert pending-thumbprint overlap are
  orthogonal windows.

## 7. REST + UI

- `POST   /api/userbots/credentials/{id}/rotate` → `{ plainToken, deadline }`.
- `POST   /api/userbots/credentials/{id}/finalize-rotation`.
- `PATCH  /api/userbots/credentials/{id}` → set/clear `ExpiresAt`.
- `ListByUser` exposes `lastUsedAtUtc`, `lastSeenIp`, `expiresAt`,
  `rotationDeadlineUtc` (never any secret). State badges: active / pending-rotate
  / expiring / expired / revoked. Cross-user → 404 (no id oracle), unchanged.

## 8. Persistence

- New nullable fields ride the existing WAL record + snapshot; old rows
  hydrate with nulls. Bump the credential snapshot schema minor.
- `LastUsedAtUtc/LastSeenIp` are high-churn — coalesce (write at most every N s
  per credential) so per-message auth doesn't thrash the WAL; on restart the
  field can lag by the coalesce window (acceptable: it's a hint, not the
  contract).

## 9. Config surface

```jsonc
"Trading": { "EntryPointListener": { "Rotation": {
  "GracePeriod": "24:00:00",        // overlap window default
  "MaxCredentialTtl": null,         // optional ceiling on ExpiresAt
  "LastUsedCoalesceSeconds": 60
}}}
```

## 10. Sub-issue decomposition

- A: model + WAL/snapshot fields + serialization back-compat.
- B: registry rotate/finalize/expiry + dual-secret + last-used coalescing.
- C: auth-path overlap + expiry + sweep; metrics (`rotations_total`,
  `auth_via_pending_total`, `expired_reject_total`).
- D: REST + UI flows.
- E: docs (RUNBOOK rotate/revoke) — feeds #535.

## 11. Risks

- **Overlap = two live secrets** → minimise grace, force finalise, expose both
  in audit. **Last-used coalesce** trades freshness for WAL load. **Expiry
  lockout** mitigated by expiring-soon badge + alert (#533). **Back-compat**:
  every field nullable, defaults preserve today's behavior.

## 12. Open questions

- Auto-rotate scheduler in v1? Per-tenant grace override? Hard TTL ceiling
  enforced platform-wide vs per-credential?
