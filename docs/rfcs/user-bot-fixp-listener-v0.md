# RFC: User-bot FIXP listener v0

| Field    | Value                                                              |
| -------- | ------------------------------------------------------------------ |
| Status   | Proposed                                                           |
| Tracking | [#166](https://github.com/pedrosakuma/B3TradingPlatform/issues/166) |
| Replaces | n/a (new inbound adapter alongside REST + WS)                      |

## 1. Context

Trading-host today exposes two inbound order-entry surfaces:

- **REST** `POST /api/orders` — JWT-authenticated, used by the trader UI
  and any HTTP-aware client.
- **WebSocket hub** `/hub` — JWT-authenticated, fan-out for ERs and
  algo lifecycle.

Both speak our own JSON shapes. A user who wants to write their own
bot must learn the platform's REST contract, deal with our auth,
deserialize our event envelopes. That is fine for casual integrations
but it has a ceiling: the audience is "users who know the platform",
not "users who know B3".

The existing **outbound** side speaks B3's native protocol via
`B3.EntryPoint.Client` (FIXP framing + SBE messages, V6). The asset
of speaking that protocol is wasted on the outbound leg only — every
B3-literate developer already has working `NewOrderSingle` /
`OrderCancelRequest` / `ExecutionReport` code.

This RFC proposes a **third inbound adapter**: a FIXP listener inside
the trading-host that lets a platform user run their own bot
externally, connect via the same protocol they would use against B3
production, authenticate with credentials they themselves generate
through the trader UI, and have their orders flow through the same
post-auth pipeline (`pre-trade → WAL → exchange gateway`) that REST
and WS use today. The platform becomes "your EntryPoint": from the
bot's point of view, talking to us is indistinguishable from talking
to B3, except the auth surface is per-user and the firm session that
ultimately reaches B3 is the platform's.

## 2. Goals

1. **Native FIXP/SBE inbound.** A bot speaking off-the-shelf B3
   EntryPoint client code (`B3.EntryPoint.Client` or the user's own
   implementation in any language with SBE codecs) can connect,
   authenticate, send `NewOrderSingle` / `OrderCancelRequest`, and
   receive `ExecutionReport`. The only platform-specific knowledge
   required is the value the user pastes into the SDK's
   `Credentials`/`SessionId` fields — both of those are emitted
   verbatim by the UI when the credential is generated.
2. **Per-user credentials, self-service.** The user generates and
   revokes their own FIXP credentials through the UI, scoped to their
   own account. The platform never displays a credential to anyone
   else, and never stores it in plaintext.
3. **Reuse the post-auth pipeline.** Orders submitted via FIXP must go
   through the **same** `OrderSubmissionService.SubmitAsync`, the
   same `RiskPipeline`, the same `FileEventStore` WAL, the same
   `IExchangeGateway` as REST orders. The FIXP layer is a transport
   adapter, not a parallel execution path. The submit request shape
   (`OrderSubmissionService.OrderSubmissionRequest`) gains two
   optional fields
   — `ExternalClOrdId` (string, the bot's wire identifier) and
   `Origin` (enum: `Rest` | `Ws` | `Fixp`) — so the existing service
   continues to allocate the internal `ulong` ClOrdId via
   `ClOrdIdPrefixRegistry` while the adapter records the external
   identifier alongside the resulting order in a single dispatcher
   callback (see §4.6, §4.8).
4. **Inherit isolation for free.** Cross-user isolation today is a
   single `ClaimsPrincipal` user-claim. The FIXP adapter resolves the
   credential to that same claim shape. No new isolation surface to
   audit.
5. **Production-safe by default.** Feature gate off by default. When
   on in `Environment=Production`, the host refuses to boot without
   TLS, without rate-limit configuration, and without an explicit
   "I really mean to expose this externally" opt-in.

## 3. Non-goals

- **SecurityList / MarketData / News on the FIXP channel.** B3's real
  EntryPoint sends these too; we are an order-entry endpoint only.
  Market data already has a separate (non-FIXP) WS channel and that
  stays as-is. The bot is expected to read MD elsewhere.
- **Multi-firm self-service.** v0 routes every bot's orders through
  the trading-host's default firm session. The credential carries no
  firm selector. When a user needs to pick a firm at session-time
  (or be mapped to a firm by an admin), that is a follow-up RFC and
  one extra column on `UserBotCredential`.
- **Cert-based auth (mTLS, client certs).** v0 ships username +
  AccessKey, mirroring B3's own credential shape. Cert-based auth is
  feasible but pulls in CA management, distribution, and rotation —
  not justified until somebody asks.
- **Credential rotation without revoke+regenerate.** v0 treats
  rotation as the user generating a new credential, switching their
  bot, and revoking the old one. No automated overlap-window or
  rolling-secret mechanism.
- **Server-side bot hosting.** The platform does not run the user's
  bot. It only exposes the channel. Hosted bots (#134's
  `SimulatorBot` is the only one) are a separate concern.
- **Full FIXP V6 message coverage.** v0 covers the session-control
  set (Negotiate, NegotiateResponse, NegotiateReject, Establish,
  EstablishAck, EstablishReject, Terminate, Sequence,
  RetransmitRequest, Retransmission, NotApplied) and the application
  set (NewOrderSingle, OrderCancelRequest, ExecutionReport,
  BusinessMessageReject). Other application messages (mass-cancel,
  cross-orders, quote requests) come back as `BusinessMessageReject`.
- **Multi-firm operator credentials** — that is **#126**, completely
  orthogonal. #126 is "how does the trading-host authenticate
  *outbound* to B3?". This RFC is "how does a user's bot authenticate
  *inbound* to the trading-host?".

## 4. Architecture

### 4.1 Process boundary

The listener lives **inside the trading-host process**. Same lifecycle,
same DI container, same logging stack, same configuration root
(`Trading:EntryPointListener:*`). It is just another inbound adapter,
on the same footing as the Kestrel HTTP listener and the WS hub.

```
                 ┌──────────────── trading-host process ────────────────┐
                 │                                                      │
TCP :5001 ──────►│ B3.Trading.EntryPointListener (new project)          │
(FIXP / SBE)     │   FixpListener  (TcpListener; HostedService)         │
                 │     ↓ accept                                         │
                 │   FixpSessionLoop (per-connection)                   │
                 │     ↓ Negotiate → Establish                          │
                 │   IUserBotCredentialResolver  (bcrypt)               │
                 │     ↓ resolves user                                  │
                 │   ClaimsPrincipal (synthetic, same shape as JWT)     │
                 │     ↓                                                │
                 │   FixpOrderAdapter                                   │
                 │     ↓ SBE NewOrderSingle → OrderSubmissionRequest    │
                 │     │   (Origin=Fixp, ExternalClOrdId=<bot value>,   │
                 │     │    Symbol=SymbolDirectory.ResolveBySecurityId)│
                 │   ┌──── existing pipeline ────────────────────┐      │
                 │   │ OrderSubmissionService.SubmitAsync →      │ ──► matching-platform / B3
                 │   │   RiskPipeline →                           │      │
                 │   │   FileEventStore (one composite WAL event:│      │
                 │   │   OrderSubmittedEvent w/ BotMapping?) →   │      │
                 │   │   IExchangeGateway                         │      │
                 │   └────────────────────────────────────────────┘     │
                 │                       ▲                              │
                 │                       │ ER                           │
                 │   FixpErMultiplexer ──┤                              │
                 │     ↓ reverse-lookup ClOrdId → user → session        │
                 │     ↓ encode SBE ExecutionReport → send              │
                 │                                                      │
HTTP /api/me/bot-credentials  ──►  UserBotCredentialEndpoints           │
(JWT-authenticated)               + UserBotCredentialStore (bcrypt)     │
                 └──────────────────────────────────────────────────────┘
```

### 4.2 Project layout

| Project | New / changed | Role |
|---|---|---|
| `B3.Trading.EntryPointListener` (new) | new | Pure protocol: framer, session loop, message adapters. References `B3.Trading.Application` for `OrderSubmissionService` and the new `IUserBotOrderMappingRegistry` / `IUserBotSessionRegistry`. |
| `B3.Trading.Api` | changed | Adds `UserBotCredentialEndpoints` (CRUD over `/api/me/bot-credentials`). |
| `B3.Trading.Application` | changed | Adds `UserBotCredential` entity + `IUserBotCredentialStore` (bcrypt hashing). Adds `IUserBotSessionRegistry` + `IUserBotOrderMappingRegistry`. Extends `OrderSubmittedEvent` with optional `BotMapping?` sub-record and adds new `OrderCancelRequestedEvent`/`BotSessionSeqAdvancedEvent`/`BotSessionVerAdvancedEvent` to `WalEvents`. Extends `OrderSubmissionService.OrderSubmissionRequest` with optional `ExternalClOrdId` + `Origin`. Extends `SymbolDirectory` with `TryGetSymbolBySecurityId(ulong, out string)` reverse lookup (the existing `SecurityIds` dict already gives us the inverse for free). |
| `B3.Trading.Host` | changed | Wires the listener as a `HostedService` (gated on the feature flag). DI registrations. |
| `frontend/` | changed | New settings page section: list / generate / revoke. |

`B3.Trading.EntryPointListener` is a separate project (not a folder
inside `B3.Trading.Api`) so that the FIXP framing tests can run as a
fast, focused unit suite and so the protocol surface is reviewable
independently of the HTTP API.

### 4.3 Wire format

FIXP V6 framing uses **B3 SOFH** (Simple Open Framing Header) followed
by an SBE message. Decoded/encoded via `B3.Entrypoint.Fixp.Sbe.V6.*`
types from the `B3.EntryPoint.Sbe` NuGet (already a transitive dep
via `B3.EntryPoint.Client`). The reference for the framer is
`B3.EntryPoint.Client.Framing.SofhFrameReader`/`SofhFrameWriter`,
which we already use on the outbound side; the server-side framer
mirrors its layout exactly, including endian.

Frame layout (one SOFH-framed record):

```
+------------+----------------+----------------+---------------------------+
| 2 bytes    | 2 bytes        | 8 bytes        | N bytes                   |
| msg length | encoding type  | SBE msg header | SBE-encoded payload       |
| (uint16,   | (uint16,       | (block len,    |                           |
|  little-   |  little-endian;|  template id,  |                           |
|  endian;   |  0xEB50 = SBE  |  schema id,    |                           |
|  includes  |  little-endian)|  version)      |                           |
|  this hdr) |                |                |                           |
+------------+----------------+----------------+---------------------------+
\___________ SOFH (4 bytes) __/                                            
                                                                           
\___________________________ msg length covers all bytes ________________/ 
```

**All multi-byte fields — both SOFH and SBE — are little-endian on
the wire.** The B3 SDK uses `BinaryPrimitives.ReadUInt16LittleEndian`
/ `WriteUInt16LittleEndian` for the SOFH fields and
`SbeLittleEndianEncodingType` for the SBE payload. A naive
big-endian implementation would byte-swap the framing fields and
fail to interop. Tests pin every supported message to a hex golden
vector captured from a known-good `B3.EntryPoint.Client` round-trip
against the matching-platform.

`FixpFramer` reads/writes whole frames over a `PipeReader` /
`PipeWriter`. The SBE message header is the standard 4×uint16 layout
(block length, template id, schema id, version) — see the
constructors of any `*DataReader` in `B3.Entrypoint.Fixp.Sbe.V6` for
the offsets we are obliged to honor.

### 4.4 Session state machine

Per-connection states:

```
        ┌────────┐  Negotiate ok        ┌─────────────┐
 client │  Idle  │ ───────────────────► │ Negotiated  │
 conn ─►│        │                      │             │
        └────────┘ ←──────── reject ────┴─────────────┘
                        NegotiateReject
                                                │
                                         Establish ok
                                                ▼
                                         ┌──────────────┐
                                         │ Established  │ ◄── application msgs
                                         │              │     NewOrderSingle/Cancel
                                         └──────┬───────┘     RetransmitRequest
                                                │
                                  Terminate / TCP close /
                                  fatal protocol error
                                                ▼
                                         ┌──────────────┐
                                         │ Terminated   │
                                         └──────────────┘
```

Transitions never revert: a `Terminate` in either direction tears the
TCP socket. Reconnects are new TCP sessions; gap recovery is handled
inside the new session via `Establish.NextSeqNo` + `RetransmitRequest`,
not by resuming the prior socket.

### 4.5 Authentication model

FIXP `Negotiate` carries a `Credentials` byte buffer plus a
`SessionId` (uint32) and `SessionVerId` (uint64). The platform
defines those three fields as follows:

#### Credential token (PAT-style)

The `Credentials` payload is **a single opaque token** — no
NUL-delimited username, no platform-specific framing inside the
buffer. The token has the printable shape:

```
b3t_<credShortId>_<secret>
```

- `b3t_` constant prefix marking it as a B3-Trading-platform user-bot
  token (mirrors GitHub's `ghp_` / `ghs_` PAT convention).
- `<credShortId>` 8-byte URL-safe base32 of the credential row's
  primary key. Public — it is the lookup index. Bcrypt is too slow
  for an unindexed scan over all credentials, so the prefix gives us
  a direct row fetch.
- `<secret>` 32-byte URL-safe base64 of cryptographically random
  bytes. **Never stored** in plaintext; bcrypt-hashed at rest with
  cost 12.

The full token is shown to the user **once** in the create-credential
modal (with a copy-to-clipboard helper and an explicit "you will not
see this again" warning). The platform stores `(credShortId,
accessKeyHash)` and uses `credShortId` for both the UI list display
and the FIXP lookup path.

This shape lets the bot place the entire token verbatim into the
SDK's `Credentials.FromUtf8(token)` slot without any string surgery,
satisfying Goal #1.

Resolution path on `Negotiate`:

1. Parse the buffer as UTF-8, validate the `b3t_` prefix and the
   two `_` delimiters; on shape failure, NegotiateReject(Credentials).
2. `IUserBotCredentialStore.FindByShortIdAsync(credShortId)` — if
   not found, NegotiateReject(Credentials).
3. If `RevokedAt != null`, NegotiateReject(Credentials).
4. `BCrypt.Verify(secret, cred.AccessKeyHash)` — if false,
   NegotiateReject(Credentials). (Constant-time inside bcrypt.)
5. Resolve `cred.UserId` to a user entity, build a synthetic
   `ClaimsPrincipal` with the same claims a JWT would produce (`sub`,
   `role`, `firm-claim` if applicable). Attach to the `FixpSession`.

NegotiateReject path **always** logs at Information with
`{"event":"fixp.negotiate.reject","reason":"<code>","remote":"<ip>","credShortId":"<value>"}`
— the `credShortId` is non-secret and aids ops investigations
without exposing the secret half. The bcrypt-hashed secret never
appears in any log line.

#### SessionId and SessionVerId

These FIXP fields identify the **logical session**, not a TCP
connection. The platform owns them and binds them to the credential:

- `SessionId = credential.SessionId` (**uint32**, non-zero). Generated
  at credential creation time (random non-zero `uint32`), stored on
  `UserBotCredential`. The user copies this from the create-credential
  modal alongside the access token. It is **non-secret** but stable
  for the credential's lifetime. Width matches the SBE schema:
  `B3.Entrypoint.Fixp.Sbe.V6.SessionID` is `readonly struct
  SessionID(uint value)`, and the rest of the platform
  (`ExchangeOptions.Firms[].SessionId`, `SimulatorBotOptions`) also
  models it as `uint`.
- `SessionVerId` (**uint64**). Server-managed per credential. Starts
  at 1 on credential creation and increments **only** when the
  platform needs to invalidate buffered state (e.g. ring buffer
  overflowed beyond replay window — see §4.7 — or operator forces
  re-establish). Each bump is persisted via `BotSessionVerAdvancedEvent`
  (§4.8) **before** the EstablishReject is sent, so the
  bot-observable state is monotonic and crash-recoverable. The bot
  must re-Establish with the latest `SessionVerId`; an
  `Establish.SessionVerId` lower than the server's current value
  receives `EstablishReject(InvalidSessionVerId)` carrying the new
  value in the reject payload (FIXP `EstablishRejectData` has a
  `NextSeqNo` field that the server populates with the new
  `SessionVerId` for the bot's next attempt). A higher value
  receives the same (server is the source of truth).
- The current `SessionVerId` is included in `EstablishAck`, so a
  bot that has lost track recovers by handshaking, reading the ack,
  and reconnecting with that value.

Multiple concurrent TCP connections sharing the same `(SessionId,
SessionVerId)` pair are rejected: the first establishes; subsequent
Negotiates on the same pair receive `NegotiateReject(SessionBlocked)`.
This enforces single-active-session-per-credential, on top of the
broader `MaxSessionsPerUser` cap.

Revoke-mid-session: on every inbound application message, the session
loop checks `_revocationToken.IsCancellationRequested` (a token wired
from `IUserBotSessionRegistry`); on revoke, the session sends
`Terminate(Unspecified)` and closes. This is a polling check on each
message rather than a pre-emptive interrupt because we want to finish
processing the current message cleanly.

### 4.6 ClOrdId namespacing

The platform's internal `ClOrdId` is a packed non-zero `ulong`
allocated by `ClOrdIdPrefixRegistry.Generate(owner)` inside
`OrderSubmissionService.SubmitAsync`. That allocation is the source
of truth on the wire to B3 and in the WAL; we are not changing it.

What changes for FIXP:

- `OrderSubmissionService.OrderSubmissionRequest` gains optional
  fields `string? ExternalClOrdId` (default null) and `OrderOrigin
  Origin` (default `Rest`). REST callers pass nothing; the FIXP
  adapter passes both.
- The bot's wire `ClOrdId` field is **a string** at the user-facing
  surface (the SBE schema defines it as `uint64`, so on the wire it
  is numeric — the bot is free to use `ulong` values opaque to us;
  we round-trip them as `ToString(CultureInfo.InvariantCulture)`
  into `ExternalClOrdId` purely for logging/debugging clarity).
- After `OrderSubmissionService` allocates the internal ClOrdId, it
  appends a **single composite WAL event** — `OrderSubmittedEvent`
  extended with an optional `BotMapping?` sub-record carrying
  `{ credentialId, externalClOrdId }`. The dispatcher's existing
  one-event-per-`Dispatch` contract is preserved (no atomicity
  hand-waving about "sibling events"); the mapping is part of the
  same WAL record as the order itself, so a crash before the record
  is durable loses both, and a crash after recovers both. This
  composite shape is repeated for cancel (see below).
- The mapping is held in memory by `IUserBotOrderMappingRegistry`,
  rebuilt at startup by replaying the WAL.
- Duplicate `(userId, externalClOrdIdNumeric)` within the live
  mapping returns `BusinessMessageReject(DuplicateClOrdId)` from the
  adapter **before** the pipeline is invoked. (This mirrors B3's own
  duplicate handling and avoids burning an internal ClOrdId on a
  guaranteed reject.)

```
inbound  (FIXP):    ClOrdId=42 (uint64), user=alice
adapter validates:  (alice, 42) not in live mapping → ok
adapter invokes:    OrderSubmissionService.SubmitAsync(OrderSubmissionRequest {
                       ..., Origin=Fixp, ExternalClOrdId="42" })
service allocates:  internal ClOrdId = 0x1A...004  (via ClOrdIdPrefixRegistry)
service publishes:  OrderSubmittedEvent (with BotMapping? sub-record)
gateway sends:      NewOrderSingle ClOrdID=0x1A...004 to B3
ER comes back:      ClOrdID=0x1A...004
mux looks up:       0x1A...004 → (alice, "42")
mux encodes:        ExecutionReport ClOrdID=42 (uint64) → alice's session
```

REST orders keep using the existing client-supplied or
host-generated ClOrdId; FIXP orders use the same internal `ulong`
ClOrdId allocator with no embedded user/credential identifier. The
FIXP-origin tag lives on `OrderSubmittedEvent.BotMapping?` (§4.8),
not on the ClOrdId itself.

For `OrderCancelRequest`, the bot supplies the `ClOrdID` of the
cancel and the `OrigClOrdID` of the order being cancelled. The
adapter:

1. Resolves `OrigClOrdID` (the bot's external value for the original
   order) against the mapping registry to the internal original
   ClOrdId. If it does not resolve to an order owned by the same
   user, returns `BusinessMessageReject(UnknownOrder)` — this is
   also the cross-user-isolation guard.
2. Allocates a new internal ClOrdId via the same `ClOrdIdPrefixRegistry`
   (matching the existing REST cancel path in
   `backend/src/B3.Trading.Api/OrdersEndpoints.cs:159`).
3. Inside a single `EventDispatcher.Dispatch` call, append a new
   `OrderCancelRequestedEvent { cancelClOrdId, originalClOrdId,
   ownerEndClientId, BotMapping? }`. The `apply` callback —
   synchronous and lock-held per the dispatcher contract — performs
   only the in-memory mutations: `ownership.RegisterCancelLink(...)`,
   the bot-cancel-mapping registration (when `BotMapping != null`),
   and `ClOrdIdPrefixRegistry.Observe(ownerEndClientId, cancelClOrdId)`
   to advance the per-end-client counter watermark (mirrors the
   existing replay handling for `OrderReplaceRequestedEvent` so a
   restart cannot reuse a cancel-side ClOrdId).
4. **After** `Dispatch` returns (so the WAL record is enqueued and
   the in-memory state is consistent), call
   `gateway.CancelAsync(originalOrder, cancelClOrdId, ct)` outside
   any dispatcher lock — async I/O must never run under the global
   dispatcher lock, which is documented for synchronous in-memory
   work only.

REST cancels continue to omit the `BotMapping` field; the new event
type with the field absent is functionally equivalent to today's
in-memory-only path on replay (the cancel-link is re-registered
from the event). v0 of the cancel WAL upgrade is scoped to
"persist-cancel-link" — broader cancel pipeline restructuring
(replay-time cancel reconciliation, pending-cancel snapshot) stays
out of scope and lands as needed in a follow-up.

#### `SecurityId → Symbol` reverse lookup

`OrderSubmissionService` currently requires a non-empty `Symbol`
(equity ticker), but the FIXP wire identifies instruments by
`uint64 SecurityId`. `SymbolDirectory` already holds the forward
`Symbol → SecurityId` map; v0 adds an inverse lookup
(`TryGetSymbolBySecurityId(ulong, out string)`) computed at
construction time from the same `SecurityIds` dictionary (no extra
config surface). The adapter calls it on every `NewOrderSingle`; on
miss, `BusinessMessageReject(UnknownSecurity)` is returned without
touching the pipeline.

### 4.7 Outbound ER routing

The internal ER bus has two layers today:

1. `ExecutionReportProcessor` consumes raw `ExecutionReportEnvelope`
   instances from `IEntryPointClient` (carrying full FIXP fields,
   including `OrigClOrdId` and the original numeric ClOrdIds).
2. After domain enrichment (cancel-link resolution, working-order
   updates), it publishes a normalized `ExecutionEvent` via
   `IExecutionEventSink` for the WS hub.

The FIXP outbound multiplexer hooks **both** layers depending on what
the SBE `ExecutionReport` needs:

- **Primary subscription**: `IExecutionEventSink` — gives us the
  domain-normalized status, quantities, prices, and the resolved
  internal ClOrdId, which is the routing key.
- **Side channel**: when the SBE `ExecutionReport` field set requires
  data not present in `ExecutionEvent` (e.g. `OrigClOrdID` for cancel
  ER, exchange timestamps, raw reject reason text), the multiplexer
  also subscribes to a new `IRawExecutionReportSink` invoked from
  `ExecutionReportProcessor` **before** normalization. v0 keeps both
  sinks ordered (raw first, normalized second) so the multiplexer can
  fold them per ClOrdId before sending the outbound SBE message.

Routing flow per ER:

1. Determine the routing key. For `New`/`PartialFill`/`Filled`/`Rejected`
   ERs, the routing key is the report's own internal ClOrdId
   (= `ExecutionEvent.ClOrdId`). For `Canceled`/`Replaced` ERs,
   `ExecutionReportProcessor` already normalizes
   `ExecutionEvent.ClOrdId` to the **original** internal ClOrdId via
   `OrigClOrdId` cancel-link resolution
   (`backend/src/B3.Trading.Application/ExecutionReportProcessor.cs:95-109`)
   — that **is** our routing key. So routing is always:
   `IUserBotOrderMappingRegistry.TryResolve(ExecutionEvent.ClOrdId)`.
2. If the order did not originate from FIXP, the lookup misses and
   the ER is silently ignored by the multiplexer (REST/WS handle it).
3. Compose SBE `ExecutionReport` with the **external** identifiers
   restored from the mapping. For cancel/replace ERs, both fields
   are set: the outbound SBE `ClOrdID` is the bot's **cancel**
   external ClOrdId (resolved from the cancel-side mapping using
   the raw `ExecutionReportEnvelope.ClOrdId`, which the side-channel
   sink delivers — see below), and `OrigClOrdID` is the bot's
   external ClOrdId for the original order (from the routing-key
   mapping). For new/fill/reject ERs, only `ClOrdID` is set from
   the routing-key mapping. **It is wrong to fold raw and normalized
   per single ClOrdId** (a cancel ER has two relevant ClOrdIds, not
   one); the multiplexer correlates by the pair
   `(rawEnvelope.ClOrdId, normalizedEvent.ClOrdId)` for cancel/replace
   and by single ClOrdId for new/fill/reject.
4. If the user has an active FIXP session, increment that session's
   outbound seq #, encode, send.
5. If no active session, enqueue in the user's per-credential
   outbound buffer (bounded ring, default 1024 — see §4.8 for
   persistence rules).

Drop policy when buffer overflows: drop **oldest**, increment metric
`entrypoint_listener.er_outbound_dropped_total`, log a Warning, and
**advance the credential's `SessionVerId`** so the bot's next
Establish forces an explicit version mismatch the bot must
acknowledge (it cannot replay through the gap, by definition).
This is a stronger signal than B3's own `RetransmitReject(OutOfRange)`
because we want the bot to reload state explicitly rather than
silently miss a fill.

### 4.8 Persistence

The mapping table and outbound sequence numbers must survive host
restart so a reconnecting bot sees a coherent FIXP session.
Persistence reuses the existing `FileEventStore` (one log,
single-fsync-per-batch, snapshot replay path already exercised by
the order pipeline) — **no second WAL file**. The existing
`EventDispatcher.Dispatch` contract takes one event per call; rather
than introduce sibling-event semantics it doesn't have, this RFC
**extends existing events with optional FIXP metadata** so a single
WAL append carries both order and mapping information.

Changes to `WalEvents.cs`:

- `OrderSubmittedEvent` — add optional `BotMapping?` sub-record:
  `record BotMapping(Guid CredentialId, string ExternalClOrdId)`. For
  REST/WS submissions the field is `null`; for FIXP, it is set. On
  replay, the registry rebuilds its `(internalClOrdId →
  (credentialId, externalClOrdId))` index from non-null entries.
- `OrderCancelRequestedEvent` — **new event type**. The current
  cancel path is in-memory only (`OwnershipMap.RegisterCancelLink`),
  which is fine for REST today but breaks ER round-trip for FIXP
  cancels across restarts. Event carries
  `{ cancelClOrdId, originalClOrdId, ownerEndClientId, BotMapping? }`.
  REST cancels emit it with `BotMapping=null`; FIXP cancels set both.
  The cancel pipeline in-memory mutation
  (`RegisterCancelLink` + bot-cancel-mapping registration +
  `ClOrdIdPrefixRegistry.Observe(ownerEndClientId, cancelClOrdId)`
  watermark advance) runs in the dispatcher `apply` callback under
  the same lock as the WAL append. The async `gateway.CancelAsync`
  call runs **outside** the dispatcher lock after `Dispatch` returns,
  per the dispatcher's documented contract that it only protects
  synchronous in-memory work. On replay, the cancel event re-runs
  the same in-memory mutation so the counter watermark advances and
  cannot reuse a cancel-side ClOrdId.
- `BotSessionSeqAdvancedEvent { credentialId, sessionVerId, lastOutboundSeq }`
  — appended on a periodic checkpoint cadence: every **5 seconds OR
  every 100 outbound messages, whichever comes first** (both
  configurable). This is **not** appended per-ER (would double the
  WAL pressure of FIXP-originated orders); it is a watermark that
  says "as of WAL position X, the credential's outbound seq was Y".
  On replay, the registry seeds with the last watermark; on
  Establish, the bot's `NextSeqNo` is checked against the bound
  `[Y, Y + (M-1)]` where M is the message-cadence threshold (default
  100). Inside the bound, the server replays from the most recent
  matching ER event in the WAL. **Outside the bound, the server
  treats this as unreplayable**: see the atomic version-bump path
  below.
- `BotSessionVerAdvancedEvent { credentialId, oldVer, newVer, reason }`
  — appended whenever the platform forces a `SessionVerId` bump.
  The bump path is:
  1. `Dispatch(BotSessionVerAdvancedEvent { ... newVer = oldVer+1, ... }, apply: () => registry.SetCurrentVer(credentialId, newVer))`.
  2. `await store.FlushAsync(ct)` — explicit fence. `FileEventStore`
     enqueues to a background writer in `Append`; without an explicit
     flush before a bot-observable side effect, a crash between the
     reject send and the disk flush would let the bot observe
     `newVer` while recovery rolled back to `oldVer`.
  3. **Only after the flush returns** (event durable, in-memory
     state mutated under the lock), respond to the bot with
     `EstablishReject(InvalidSessionVerId)` carrying `newVer` in the
     reject payload.
  This sequence ensures the bot's next attempt sees a server with a
  `currentVer` that is already advanced, durably — it cannot loop
  forever retrying the old version, and a crash cannot expose a
  rolled-back version to the bot. The same path is invoked from the
  outbound-buffer overflow handler in §4.7 (`reason="overflow"`)
  and from an admin-only `POST /admin/fixp/sessions/{credId}/bump`
  endpoint (`reason="operator"`).

#### Snapshot scope

The outbound buffer (raw queued ERs awaiting reconnect) is **not**
persisted in v0; on restart, a reconnecting bot will see the
checkpoint watermark and may receive `EstablishReject` if it asks
for ERs older than the watermark. The trade-off: persisting every
buffered ER would couple the FIXP channel to write throughput in a
way the rest of the system doesn't pay; lost-on-restart ERs are
recoverable by the bot via REST `/api/orders` for state
reconciliation (documented in operator + bot-author docs in
sub-issue H).

Recovery in this codebase is `snapshot.Restore() → ReadFromAsync(snapshot.seq)`.
Events older than `snapshot.seq` are not replayed. Therefore the
`PlatformSnapshot` schema must be extended to capture the new FIXP
state, so a snapshot taken between FIXP events does not lose
mappings or seq watermarks:

- `PlatformSnapshot.BotOrderMappings: IReadOnlyList<BotOrderMappingSnapshot>`
  — `(internalClOrdId, credentialId, externalClOrdId)` for live
  (non-reaped) mappings, captured under `WithSnapshotLock`.
- `PlatformSnapshot.BotCancelMappings: IReadOnlyList<BotCancelMappingSnapshot>`
  — `(cancelInternalClOrdId, originalInternalClOrdId, credentialId, externalCancelClOrdId)`
  for in-flight FIXP cancels, same lock.
- `PlatformSnapshot.BotSessions: IReadOnlyList<BotSessionStateSnapshot>`
  — `(credentialId, currentVer, lastCheckpointedOutboundSeq)` per
  credential.

`SnapshotService` is extended to capture and restore these
collections; the new registries (`IUserBotOrderMappingRegistry`,
`IUserBotSessionRegistry`) implement `ICaptureSnapshot` /
`IRestoreSnapshot` mirroring the existing `WorkingOrderBook`
pattern. On restore, post-snapshot WAL events
(`OrderSubmittedEvent.BotMapping?`, `OrderCancelRequestedEvent`,
`BotSession*AdvancedEvent`) further mutate these registries via the
existing event-replay machinery. This is sub-issue E's responsibility
to land alongside the new event types.

### 4.9 Configuration surface

```jsonc
"Trading": {
  "EntryPointListener": {
    "Enabled": false,                    // default OFF
    "Endpoint": "0.0.0.0:5001",
    "Tls": {
      "CertPath": null,
      "KeyPath": null,
      "Required": false                  // forced true in Production by validator
    },
    "MaxSessionsPerUser": 3,
    "RateLimit": {
      "NegotiatesPerMinutePerIp": 30,
      "NegotiatesPerMinutePerUsername": 10
    },
    "Buffers": {
      "OutboundRingSize": 1024,
      "MappingReapAfter": "00:10:00"
    },
    "AllowInProduction": false           // explicit opt-in (mirrors AllowErInjectionInProduction)
  }
}
```

Validator rules (`EntryPointListenerOptionsValidator`):

- `Enabled=true` → `Endpoint` must parse as `host:port`.
- `Enabled=true` AND `Environment=Production` → `Tls.Required=true`
  AND `Tls.CertPath`/`Tls.KeyPath` set AND `AllowInProduction=true`.
  Otherwise: refuse to boot. (Same shape as `ErInjectionBootGuard`
  shipped in #163.)
- `MaxSessionsPerUser` >= 1.
- All rate-limit numbers > 0 if `Enabled=true`.

### 4.10 Observability

Metrics (OTel UpDownCounter / Counter):

| Metric | Type | Labels |
|---|---|---|
| `entrypoint_listener.sessions_active` | UpDownCounter | (none — high cardinality forbidden) |
| `entrypoint_listener.negotiate_total` | Counter | `outcome=ok\|reject:<code>` |
| `entrypoint_listener.orders_in_total` | Counter | `kind=new\|cancel`, `outcome=accepted\|rejected` |
| `entrypoint_listener.er_out_total` | Counter | `kind=ack\|fill\|partial\|cancel\|reject` |
| `entrypoint_listener.er_outbound_buffered` | UpDownCounter | (none) |
| `entrypoint_listener.er_outbound_dropped_total` | Counter | (none) |
| `entrypoint_listener.retransmit_requests_total` | Counter | `outcome=replay\|reject` |

Notably absent: `username` as a label. Per-user cardinality on
metrics is a known operational hazard; per-user **state** lives in
the registry and per-user **events** live in the structured logs.

Health: `/health` exposes `entryPointListener.{enabled, activeSessions, listening}`
in its existing JSON body, so the trader UI's health overlay can show
"FIXP channel: 3 sessions" alongside the exchange status.

### 4.11 Production safety

Mirrors the four-guardrail pattern from `Mode=Mock+AllowErInjection`
shipped in #163:

1. **Loud boot warning** when `Enabled=true`, regardless of env.
2. **Metric tick** `entrypoint_listener.enabled` UpDownCounter goes
   to 1 so dashboards/alerts can detect drift.
3. **Health body** carries `entryPointListener.enabled=true`.
4. **Boot guard** refuses Production unless `AllowInProduction=true`.

Plus the FIXP-specific:

5. **TLS required in Production** at the validator level.
6. **Rate-limit on Negotiate** (per IP and per username) to make
   credential stuffing visible and slow.
7. **MaxSessionsPerUser** to bound resource use per credential.

## 5. Sub-issue decomposition

| # | Title | Scope | Independent? |
|---|---|---|---|
| #167 | A — RFC + parent | this document | n/a |
| #168 | B — Skeleton listener + handshake | TcpListener, SOFH framer, Negotiate/Establish/Terminate happy-path with **stub auth**, hosted-service wiring, golden-byte tests | yes (after A) |
| #169 | C — UserBotCredential CRUD | entity + bcrypt store + REST endpoints + UI + PAT-style token generator + per-credential SessionId | yes (after A; parallel with B) |
| #170 | D — Wire real credential | replace stub with real resolver, populate ClaimsPrincipal, integration tests | needs B + C |
| #171 | E — NewOrderSingle/Cancel adapter | SBE → pipeline + extend `OrderSubmissionRequest` with `ExternalClOrdId`/`Origin` + ClOrdId mapping registry + composite `OrderSubmittedEvent` (with `BotMapping?`) + new `OrderCancelRequestedEvent` (REST + FIXP) + `SymbolDirectory.TryGetSymbolBySecurityId` | needs D |
| #172 | F — Outbound ER multiplexer + outbound seq tracking | hooks `IExecutionEventSink` + new `IRawExecutionReportSink`, reverse-routes via mapping registry, **owns outbound seq number model and `BotSessionSeqAdvancedEvent` checkpoint cadence** so live and buffered cases share one source of truth | needs E |
| #173 | G — Retransmit semantics | inbound seq tracking, RetransmitRequest responder over the seq state F established, gap detection, `RetransmitReject` paths, Establish replay range matching `BotSessionSeqAdvancedEvent` watermark | needs F |
| #174 | H — Hardening + conformance | TLS, rate-limit, max-sessions, observability, conformance specs | needs G |

Each ships as an independent PR. After A merges, B and C can run in
parallel.

## 6. Risks and mitigations

### 6.1 Hand-rolled FIXP server

We are writing a server for a protocol where we only have client
SDKs. **Mitigation**: golden-byte tests captured from
`B3.EntryPoint.Client` round-trips against the matching-platform
(which we already run in CI for outbound conformance). Every message
the server emits is byte-compared to a known-good capture. Sub-issue
H adds a conformance suite that drives `B3.EntryPoint.Client` against
the listener end-to-end, treating the listener as a black-box "B3
substitute".

### 6.2 ClOrdId rewrite breaking observability

The internal `ulong` ClOrdId carries no PII (it is a packed prefix
allocated by `ClOrdIdPrefixRegistry`). An operator looking at a raw
ER log sees the same opaque numeric identifier they see for REST
orders. **Mitigation**: the FIXP-origin tag is on the
`OrderSubmittedEvent.Origin` enum (visible in WAL replay and the
admin order detail view), and the mapping `(internal → user,
externalClOrdId)` is exposed at `GET /admin/fixp/mapping?internalClOrdId=...`
(admin-only, read-only) for incident investigations.

### 6.3 Credential storage

The PAT-style token (`b3t_<credShortId>_<secret>`) is sensitive in
the secret half. **Mitigation**: bcrypt with cost 12 over the secret
half only (the `credShortId` is a non-secret index used for direct
row lookup); plaintext token shown to the user exactly once at
creation (modal + clipboard helper); revocation flips `RevokedAt`
and is enforced at the next inbound check (and immediately, per
§4.5, for active sessions). We deliberately do not implement "view
existing token" even for the owner — same UX as GitHub PATs.

### 6.4 Listener exposed externally without TLS

A misconfigured deployment could put the FIXP port on the public
internet over plaintext. **Mitigation**: `Enabled=true` +
`Environment=Production` + `Tls.Required=false` is a startup error.
The boot guard fails closed.

### 6.5 Resource exhaustion / abuse

A malicious actor with a valid credential could hold a thousand
sessions and exhaust resources. **Mitigation**: `MaxSessionsPerUser`
(default 3); `RateLimit.NegotiatesPerMinutePerUsername`; each session
has an idle timeout (no Sequence/heartbeat in 60s → Terminate).

### 6.6 SBE schema version drift

B3.EntryPoint.Sbe ships schema V6 with sub-versions (V6.V1, V6.V2,
V6.V3 visible). A bot using a slightly different sub-version may
mis-parse. **Mitigation**: NegotiateResponse explicitly advertises
the sub-version we speak; mismatched sub-version is a NegotiateReject
with a clear reason in the structured log. v0 picks the highest
sub-version supported by the SDK at the time of merge.

## 7. Open questions

- **Heartbeat interval default.** B3 typical is 5s; for our use case
  (bots over LAN to dev hosts as well as remote prod) 30s is
  probably more reasonable. Defer to sub-issue B for empirical pick.
- **Should revoke-mid-session terminate immediately or after the
  current message?** RFC defaults to "after current message" because
  it avoids dropping a half-processed `NewOrderSingle`. If operators
  prefer instant termination for security incidents, it is a one-line
  change in the session loop.
- **`BotSessionSeqAdvancedEvent` checkpoint cadence default.** v0
  proposes 5s / 100 msgs, which bounds replay-from-watermark to a
  small bot-visible "you may need to reconnect" window. If the WAL
  pressure measured during sub-issue F implementation is negligible,
  consider lowering to 1s / 25 msgs.
- **`GET /admin/fixp/mapping` query shape.** Admin-only lookup is
  sketched but not designed; defer to sub-issue E once the registry
  exists.

## 8. Compatibility / migration

Nothing to migrate. v0 is a new optional inbound adapter, off by
default. Existing deployments are unaffected unless they explicitly
opt in.

No ClOrdId namespace collision: bot-origin orders flow through the
same `ClOrdIdPrefixRegistry.Generate` allocator as REST/WS, so internal
ClOrdIds remain in a single contiguous space. The bot's external
ClOrdId is carried only as side-mapping metadata and never reaches
the gateway.

## 9. Future RFCs unblocked by v0

- **user-bot-fixp-multifirm-v0** — per-user firm selection,
  `UserBotCredential.FirmId`, multi-session-per-firm routing.
- **user-bot-fixp-marketdata-v0** — adding MD over the same channel
  if bots demand it.
- **user-bot-fixp-mtls-v0** — cert-based auth, CA management. ([RFC](./user-bot-fixp-mtls-v0.md), [#528](https://github.com/pedrosakuma/B3TradingPlatform/issues/528))
- **user-bot-fixp-rotation-v0** — overlap-window credential rotation
  if revoke+regenerate cadence becomes operational pain.
