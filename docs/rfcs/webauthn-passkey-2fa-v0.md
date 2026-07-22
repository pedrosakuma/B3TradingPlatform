# RFC: WebAuthn / passkey second factor v0

| Field    | Value                                                               |
| -------- | ------------------------------------------------------------------- |
| Status   | Proposed                                                            |
| Tracking | [#319](https://github.com/pedrosakuma/B3TradingPlatform/issues/319) |
| Refs     | [#303](https://github.com/pedrosakuma/B3TradingPlatform/issues/303) (Q4.3 2FA), PR #318 (TOTP baseline) |
| Builds on | TOTP second factor (`/api/auth/2fa/*`, `UserTotpConfig`)               |

## 1. Context

Q4.3 (#303 / PR #318) shipped **TOTP (RFC 6238)** as the baseline second
factor. The login flow, per-user 2FA state, challenge-token handshake,
recovery codes, lockout, and encrypted-at-rest secret storage all exist
and are tested. WebAuthn / passkey support was explicitly deferred to
#319.

This RFC designs **WebAuthn (FIDO2) as a second, co-equal factor**
alongside TOTP, reusing the existing handshake and persistence seams so
the change is additive rather than a re-architecture. It does **not**
ship code — it locks the design, names the invariants that must survive,
sequences the work into shippable sub-issues, and surfaces the open
questions that need an answer before implementation.

### 1.1 What exists today (grounding)

| Concern | Where | Note |
| --- | --- | --- |
| Login → factor branch | `AuthEndpoints.cs:105-148` | TOTP active → `LoginTwoFactorRequiredResponse{requires2fa, totpChallengeToken}`; `Require2FA` but unenrolled → `LoginEnrollmentRequiredResponse{enrollmentToken}`; else mint JWT (`:150-166`). |
| Challenge tokens | `Totp/TotpStores.cs:88-163` | Opaque base64url CSPRNG token, `TotpChallengeKind.{Verify,ForceEnroll}`, TTL'd. |
| User record | `AuthOptions.cs:62-145` | `PasswordHash/Salt/Iterations`, `Role`, `Firm`, `Totp` (`UserTotpConfig`), `Require2FA`. |
| 2FA secret at rest | `Totp/TotpSecretProtector.cs` | ASP.NET Data Protection; key ring under `{DataDirectory}/dp-keys` (`TradingApplicationCoreServiceCollectionExtensions.cs:323-341`). |
| Recovery codes | `TotpService.cs:79-114`, store `InMemoryUserStore.cs:103-150` / `FileBackedUserStore.cs:212-273` | SHA-256 hashed, consumed atomically, replay-tracked via `ConsumedRecoveryCodes`. |
| User stores | `IUserStore.cs:14-66`, `InMemoryUserStore.cs`, `FileBackedUserStore.cs` | Env-seeded users stay config-authoritative; runtime mutations persisted to JSON. |
| JWT mint | `JwtIssuer.cs:14-54` | `sub`, `jti`, `role`, `firm`. **No `amr` claim today.** |
| FE login routing | `frontend/js/app.js:291-377`, `protocol.js:41-88` | Reads `requires2fa` / `requires2faEnrollment`, switches to the TOTP card. |
| Tests | `TotpEndpointTests.cs`, `AuthEndpointTests.cs` | enroll/verify/lockout/recovery/TTL/encrypted-at-rest/replay. |
| Crypto deps | `Directory.Packages.props:35-36` | `Otp.NET`, `System.IdentityModel.Tokens.Jwt`. **No FIDO2 library yet.** |

## 2. Goals

1. **WebAuthn as a co-equal second factor.** A user may register one or
   more passkeys, alongside or instead of TOTP.
2. **Multi-factor-aware login.** When a user has *any* factor enrolled,
   `/api/auth/login` returns a challenge that enumerates the available
   factors (`totp`, `webauthn`) so the FE can route to the right prompt.
3. **Reuse, don't reinvent.** Reuse the existing challenge-token
   handshake, `IUserStore` persistence + JSON snapshot, Data Protection
   key ring, recovery-code mechanism, and lockout posture.
4. **No regression to TOTP.** Existing TOTP-only users, the
   `ForceEnroll` path, and every current `TotpEndpointTests` scenario
   keep working byte-for-byte.
5. **Secure at rest.** Credential public keys + metadata persisted via
   the existing key ring; private keys never leave the authenticator
   (that is the whole point of WebAuthn).

### 2.1 Non-goals

- **Passwordless / first-factor WebAuthn.** v0 keeps password as the
  first factor; passkey is the *second*. (Usernameless discoverable-
  credential login is a v1 consideration — see §8 O5.)
- Attestation-based device allow-listing / enterprise MDM binding.
- Replacing TOTP. The two factors coexist; an operator may mandate
  either or both per user.

## 3. Invariants that must survive

| # | Invariant | Source |
| - | --------- | ------ |
| I1 | Password remains the first factor; WebAuthn/TOTP gate the JWT mint, never bypass it. | `AuthEndpoints.cs:66-166` |
| I2 | A user with `Require2FA=true` and **no** factor enrolled still gets the forced-enrollment path. | `AuthEndpoints.cs:127-148` |
| I3 | Recovery codes remain the device-loss escape hatch and are **shared across factors** — one recovery-code pool per user, not per factor. | `UserTotpConfig.RecoveryCodes` |
| I4 | Env-seeded users stay config-authoritative; only runtime-registered passkeys are persisted to the user JSON. | `FileBackedUserStore.cs:14-19,134-142` |
| I5 | Generic `401 {"error":"invalid credentials"}` for bad password — factor enumeration only happens **after** the password check passes. | `AuthEndpoints.cs:66-98` |
| I6 | Challenge tokens stay opaque, CSPRNG, single-use, TTL'd. | `TotpStores.cs:111-144` |
| I7 | Lockout/throttle applies to WebAuthn assertion failures as it does to TOTP. | `TotpAttemptTracker.cs`, `AuthRateLimitTests.cs` |

## 4. Library

**Recommendation: `Fido2NetLib`** (the `Fido2` package) — the de-facto
.NET WebAuthn server implementation, actively maintained, supports
attestation parse, assertion verification, and sign-counter handling.
Add to `Directory.Packages.props` (CPM) first, referenced from
`B3.Trading.Api`.

Rationale over hand-rolling: WebAuthn assertion verification (CBOR /
COSE key parsing, attestation statement formats, clientDataJSON +
authenticatorData signature checks) is exactly the kind of crypto we
should not implement ourselves. `Fido2NetLib` is permissively licensed
and has no native dependency that would break the managed-only posture.

## 5. Design

### 5.1 Persistence — `WebAuthnCredential`

Add an additive collection to the user record, parallel to `Totp`:

```
UserConfig.WebAuthn : UserWebAuthnConfig?
  Credentials : WebAuthnCredential[]   // 0..N registered passkeys

WebAuthnCredential
  CredentialId   : byte[] (base64url)  // handle returned by authenticator
  PublicKey      : byte[] (COSE key)   // protected at rest via IDataProtector
  SignCount      : uint                // clone-detection counter
  Aaguid         : Guid                // authenticator model id
  Label          : string              // user-facing ("YubiKey 5", "iPhone")
  CreatedAt      : DateTimeOffset
  LastUsedAt     : DateTimeOffset?
```

- `PublicKey` (and any sensitive blob) wrapped with the **same**
  `IDataProtector` purpose-string pattern as `TotpSecretProtector`
  (`Totp/TotpSecretProtector.cs`). The public key is not strictly
  secret, but protecting it keeps the storage posture uniform and
  guards against credential-substitution if the JSON leaks.
- Persisted by `IUserStore` exactly like `Totp`: env-seeded users are
  config-authoritative (I4); runtime registrations land in the JSON
  snapshot via the existing write path
  (`FileBackedUserStore.cs:327-350`).
- **Recovery codes stay in `UserTotpConfig`** (or are hoisted to a
  shared `UserConfig.RecoveryCodes` — see §8 O3) so a passkey-only user
  still has the device-loss escape hatch (I3).

### 5.2 Registration endpoints

Mirror the TOTP enroll shape (`TotpEndpoints.cs:29-96`), two-step
because WebAuthn registration is a challenge/response with the
authenticator:

- `POST /api/auth/webauthn/register/begin` → returns
  `PublicKeyCredentialCreationOptions` (RP info, user handle, challenge,
  excludeCredentials of already-registered IDs, pubKeyCredParams). The
  challenge is stashed in a short-TTL store keyed by an opaque token,
  reusing the `TotpStores` pattern.
- `POST /api/auth/webauthn/register/finish` → receives the attestation
  response, verifies it via `Fido2NetLib`, and on success appends a
  `WebAuthnCredential`. Accessible both in the **JWT-authenticated**
  mode (a logged-in user adding a passkey) and the **ForceEnroll**
  challenge-token mode (parity with TOTP `:46-76`).

### 5.3 Login / assertion flow

Extend `AuthEndpoints.cs:105-148` so the factor branch becomes
**factor-set aware**:

1. Password check unchanged (I1, I5).
2. Compute the user's enrolled factor set:
   `factors = { "totp" if Totp active, "webauthn" if Credentials.Any() }`.
3. If `factors` is non-empty → return a unified
   `LoginTwoFactorRequiredResponse` extended with
   `availableFactors: string[]` and, when `webauthn` is present, a
   `webauthnAssertionOptions` (`PublicKeyCredentialRequestOptions`:
   challenge + allowCredentials) alongside the existing
   `totpChallengeToken`. **Back-compat:** keep `requires2fa` +
   `totpChallengeToken` populated when TOTP is available so the current
   FE keeps working unchanged.
4. New verify endpoint `POST /api/auth/webauthn/assert` consumes the
   challenge token + the authenticator assertion, verifies signature +
   **sign-counter monotonicity** (clone detection) via `Fido2NetLib`,
   updates `SignCount`/`LastUsedAt`, and on success mints the JWT
   through the existing `JwtIssuer`.
5. `Require2FA` + zero factors → unchanged forced-enrollment path (I2),
   with the FE now free to offer "enroll TOTP **or** passkey".

### 5.4 JWT `amr` claim (opportunistic)

Today `JwtIssuer` mints no `amr` (`JwtIssuer.cs:37-43`). Add an optional
`amr` (authentication-method-reference) claim recording how the second
factor was satisfied (`pwd+otp`, `pwd+webauthn`, `pwd+recovery`). This
is additive, lets audit/compliance distinguish factor strength, and
costs one claim. Gated behind a small `JwtIssuer` overload so existing
call sites and `AuthUnitTests.cs:38-67` stay green until updated.

### 5.5 Frontend

`frontend/js/app.js:291-377` learns to branch on `availableFactors`:
render the TOTP card when only `totp`, drive the
`navigator.credentials.get()` WebAuthn ceremony when `webauthn` is
present, and offer a chooser when both. `protocol.js:41-88` gains
`webauthnAssert` / `webauthnRegister` wrappers mirroring the TOTP ones.

## 6. Decomposition (sub-issues)

1. **CPM + library** — add `Fido2NetLib` to `Directory.Packages.props`;
   `Fido2` options (RP id/name/origins) wired in
   `TradingAuthServiceCollectionExtensions.cs`.
2. **Persistence** — `WebAuthnCredential` + `UserWebAuthnConfig` on
   `UserConfig`; `IUserStore` add/list/update-sign-count/remove;
   protector reuse; JSON round-trip in both stores.
3. **Registration endpoints** — `/api/auth/webauthn/register/begin|finish`
   (JWT + ForceEnroll modes).
4. **Login + assertion** — factor-set-aware `/api/auth/login` response;
   `/api/auth/webauthn/assert`; lockout integration (I7).
5. **Recovery-code sharing** — ensure passkey-only users get/keep
   recovery codes (resolve §8 O3).
6. **`amr` claim** — optional auth-method-reference (§5.4).
7. **Frontend** — factor chooser + WebAuthn ceremony.
8. **Tests** — register/assert happy path, sign-counter clone
   detection, multi-credential, factor enumeration, TOTP-still-works
   regression, recovery-code fallback, encrypted-at-rest, lockout.

## 7. Test posture

Mirror `TotpEndpointTests.cs`. WebAuthn ceremonies are normally browser-
driven, so the server tests use `Fido2NetLib`'s test helpers / a
software authenticator (e.g. a deterministic key pair) to produce valid
attestation + assertion blobs, asserting:

- register/begin issues a challenge; finish persists a credential.
- assert with a valid signature mints a JWT; with a replayed/lower
  sign-counter is rejected (clone detection).
- a user with both factors can satisfy either; `availableFactors` is
  correct.
- TOTP-only users are entirely unaffected (regression).
- recovery code still works for a passkey-only user.

## 8. Open questions

- **O1 — Relying-Party ID across docker-compose origins.** WebAuthn
  binds credentials to an RP ID (an eTLD+1 / origin). The demo stack is
  reached on `localhost:<port>` while a real deployment uses a domain.
  We need an `Auth:WebAuthn:RpId` + allowed-origins config and a clear
  story for dev (`localhost`) vs prod. **Must be answered before
  implementation** — getting it wrong makes credentials silently
  unusable across environments.
- **O2 — Attestation conveyance.** `none` (privacy-preserving, simplest)
  vs `direct` (lets us record/allow-list authenticator models via
  AAGUID). Recommendation: `none` for v0 unless compliance wants device
  attestation.
- **O3 — Recovery-code ownership.** Keep recovery codes inside
  `UserTotpConfig`, or hoist to a shared `UserConfig.RecoveryCodes` so a
  passkey-only (no-TOTP) user has them without a phantom TOTP config?
  Recommendation: hoist (cleaner), with a migration that moves existing
  codes.
- **O4 — User handle stability.** WebAuthn user handle must be a stable,
  non-PII opaque id per user. Username is PII-ish and mutable; we likely
  need a per-user GUID minted at first registration and persisted.
- **O5 — Discoverable credentials / usernameless (future).** Out of
  scope for v0 but the persistence (credential-ID-indexed lookup) should
  not preclude it.
- **O6 — Operator mandate granularity.** Today `Require2FA` is per-user
  (`AuthOptions.cs:79-85`). Do we need "require *phishing-resistant*
  (WebAuthn only)" as a distinct level for privileged/admin accounts?
