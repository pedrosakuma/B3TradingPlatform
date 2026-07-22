# RFC: Operator & Mandate (v0)

| Field    | Value                                                              |
| -------- | ------------------------------------------------------------------ |
| Status   | Draft                                                              |
| Tracking | TBD                                                                |
| Replaces | n/a — names a concept the domain is currently missing              |

## 1. Context

The platform today treats the authenticated principal and the
account holder as **the same thing**:

```csharp
// backend/src/B3.Trading.Api/OrdersEndpoints.cs
private static EndClientId ResolveOwner(HttpContext ctx, EndClientRegistry registry)
{
    var sub = ctx.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
              ?? throw new InvalidOperationException("Authenticated request missing sub claim.");
    return registry.Register(sub);
}
```

`sub` (who logged in) **is** `EndClientId` (whose position/cash gets
moved). That fusion is correct for retail — one human, one CPF, one
account. It is wrong everywhere else:

- A buy-side trader managing N portfolios (one per CNPJ).
- A market-making team where any of three humans can act for the
  same end-client.
- A back-office user who can *read* every end-client of the firm but
  *act* for none.

The platform does not have a word for "a human/bot that acts on
behalf of an account holder but is not the account holder". The
first instinct (and the first draft of this RFC, ditched after
review) was to extend the JWT with an array of end-clients and call
it done — but that smuggles product decisions ("which end-client is
the default at submit time?", "what does a read with no selector
return?") into auth, where they don't belong.

This RFC introduces the missing domain concepts and refactors the
current behavior as the degenerate case of them.

## 2. The two missing concepts

### 2.1 Operator

An **Operator** is a principal — a human (UI session) or a bot
(FIXP/API session) — that can take actions in the platform. It has
an identity, credentials, and audit footprint, but it does **not**
own positions, cash, or P&L. Every authenticated request is made by
exactly one Operator.

Today this is implicit: `sub` is treated as both Operator and
EndClient because the model assumes they coincide.

### 2.2 Mandate

A **Mandate** is the persistent, auditable relationship
`Operator → EndClient` that authorizes the operator to act/read on
the end-client's behalf. A mandate carries:

- `OperatorId` and `EndClientId` (the parties)
- `Scope` — `Act`, `Read`, or `Act+Read`
- `Lifecycle` — `Active`, `Suspended`, `Revoked`, with timestamps
- `IssuedBy` and `IssuedAt` — audit provenance (firm admin, etc.)

A mandate is the **only** way an operator gains authority over an
end-client. The retail case is one operator with one self-mandate
(`OperatorId == EndClientId`, scope `Act+Read`, issued at user
provisioning). Multi-portfolio case is one operator with N mandates,
issued by the firm admin.

Mandates live in config initially (`Trading:Auth:Mandates[]`); a
DB-backed registry is a follow-up when issuance becomes dynamic.

## 3. What this changes (and what it doesn't)

### 3.1 Domain (new)

- `OperatorId` — distinct value type from `EndClientId` in the
  domain (no implicit conversion). The compiler stops mistaking one
  for the other.
- `Mandate` record + `IMandateRegistry` (config-loaded at startup,
  immutable per process). Lookup by `(OperatorId, EndClientId)` and
  by `OperatorId → IReadOnlyList<Mandate>`.
- `ActingContext` value type carried through the submit/cancel/
  modify pipeline: `(OperatorId acting, EndClientId on_behalf_of,
  MandateId proof)`. WAL events gain `ActorOperatorId` next to the
  existing `OwnerId` (which keeps meaning "the end-client whose book
  this affects").

### 3.2 Auth (thin)

- JWT keeps a single `sub` claim → that's the `OperatorId`.
- JWT keeps `firm`.
- JWT carries **no** end-client list. End-client authority is read
  server-side from the mandate registry, not asserted by the token.
  This removes the bug class "issuer adds an end-client to the array
  by mistake and the platform silently honors it".
- Retail keeps the same token shape it has today (a `sub` whose
  mandate is the self-mandate). Zero token-format change for retail.

### 3.3 Action semantics (explicit, no defaults)

For any **mutation** (`POST /api/orders`, `POST /algos`, modify), the
request **must** name the end-client it is for via `onBehalfOf` (or
the equivalent FIXP field). Resolver verifies:

1. There exists a mandate `(operatorSub, requestedEndClient)` with
   scope including `Act` and lifecycle `Active`.
2. Otherwise → 403 with a deterministic reason code.

There is **no default**, even if the operator has exactly one
mandate. "I had only one anyway" is exactly the regime where typos
go undetected six months later when a second mandate appears.
Retail clients pass their own `EndClientId` (== their `sub`)
explicitly; the UI fills it in transparently. The cost is one extra
field; the win is no silent ambiguity.

(This is the §1-item-1 concern from the prior draft, hoisted into
the domain instead of buried in auth defaults.)

### 3.4 Read semantics (explicit role, not implicit fan-out)

A read query (`GET /history`, `/api/positions`, `/api/pnl`, etc., and the WS
hub) takes a required selector:

- `?onBehalfOf=<endClient>` — query one end-client; mandate
  must have `Read` scope.
- `?onBehalfOf=*` — query every end-client the operator has an
  active `Read`-scoped mandate for. The wildcard is opt-in, not the
  default of an absent parameter.

Absent parameter → 400 (helpful: "specify ?onBehalfOf=<id> or *").
This is verbose by design — the previous draft's "default = fan-out
over the array" is exactly how a back-office user accidentally sees
a portfolio that was supposed to be revoked but the token cache
hadn't caught up. With explicit wildcard, the operator (and the
audit log) records *intent to see across mandates*, every time.

(This is the §1-item-2 concern hoisted out of "infer from array
length" into "explicit role/scope choice per request".)

### 3.5 No TOFU end-clients

`EndClientRegistry.Register` becomes an internal startup-only API
called by the bootstrap that loads `Trading:Auth:EndClients[]` from
config. The resolver only **looks up**; it never registers. An
unknown end-client in a mandate or in `?onBehalfOf=` is rejected at
startup (config validation) or at request time (404), never created
by side effect of a request.

(This is the §1-item-3 concern made explicit.)

## 4. Persistence and audit

`OwnerId` in WAL events keeps its current meaning: the end-client
whose book the event affects. Replay and snapshots are unchanged.

New `ActorOperatorId` field added to WAL events that originate from
a request (submit, cancel, modify, kill-switch toggle, credential
issuance). This is **additive** — recovery code that doesn't read
the new field continues to work. Audit log surfaces "who did it"
alongside "for whom".

`MandateId` is logged on each acting event too, so the audit answers
"under which mandate did operator X act for end-client Y at time
T?" without joining against a mutable registry.

## 5. Lifecycle scenarios

- **Mandate revoked while operator has live orders.** Existing
  orders keep running (they were validly submitted). New
  submits/cancels/modifies under that pair → 403. The operator can
  still cancel via mandates that grant `Act` on the same end-client
  if one exists; otherwise the firm admin (separate operator) is the
  fallback.
- **Operator suspended.** All mandates where they are the operator
  become unusable; their live orders are not auto-cancelled (that's
  a firm risk decision, out of scope here).
- **Token expires mid-algo.** Algo keeps running under its persisted
  `(OwnerId, ActorOperatorId, MandateId)`; refreshing the token does
  not re-validate the algo's mandate. Mandate revocation is the only
  way to stop new actions under it.
- **Kill-switch on `(firm, endClient)` with N mandates.** All
  operators with `Act` on that pair are blocked from new submits;
  reads under `Read` scope remain allowed.

## 6. Mapping today's behavior

| Today | Under this RFC |
| --- | --- |
| `sub` → `EndClientId` via `EndClientRegistry.Register` | `sub` → `OperatorId`; end-client comes from explicit `onBehalfOf` checked against mandate registry |
| Implicit self-ownership | Explicit self-mandate `(OperatorId, EndClientId=OperatorId, Act+Read)` issued at provisioning |
| WAL `OwnerId` only | WAL `OwnerId` + `ActorOperatorId` + `MandateId` |
| Read endpoints filter by `sub` | Read endpoints require `?onBehalfOf=<id|*>`; wildcard fans across `Read` mandates |
| FIXP credential = end-client | FIXP credential = `(OperatorId, EndClientId, MandateId)` triple; one credential, one mandate. Multi-mandate operator → multiple credentials. Unchanged from #183 semantics, renamed clearly. |
| `EndClientRegistry.Register` is TOFU | Registry is config-loaded at startup; resolver is read-only |

Retail (one operator, self-mandate) ends up byte-identical in
behavior; only the UI sends an explicit `onBehalfOf` value that
happens to equal `sub`.

## 7. Migration

Five PRs, each independently mergeable and reversible:

1. **PR A — domain.** Introduce `OperatorId`, `Mandate`,
   `IMandateRegistry` (config-loaded). Bootstrap synthesizes a
   self-mandate for every existing user so the config diff is empty.
   No endpoint changes yet.
2. **PR B — resolver swap.** Replace `ResolveOwner(HttpContext)`
   with `IActingContextResolver` that requires the explicit
   `onBehalfOf` field on mutating requests. Retail UI updated to
   send it. Endpoints reject absent/invalid → 403/400.
3. **PR C — read explicitness.** Endpoints and WS hubs require
   `?onBehalfOf=`; `*` performs fan-out over `Read` mandates. UI
   updated. Cover all read surfaces in one PR for cross-cutting
   consistency.
4. **PR D — audit fields.** Add `ActorOperatorId` and `MandateId`
   to WAL events (additive; recovery code untouched). Audit endpoint
   surfaces them.
5. **PR E — multi-mandate ops + docs.** Config schema for
   `Trading:Auth:Mandates[]`, validation, ops docs, example
   `docker/real/mandates.example.json`, UI selector (only visible
   when operator has > 1 mandate).

PRs A–B together give a no-behavior-change refactor that names the
concepts and removes the TOFU. PR C is the only one that breaks the
read contract; UI ships in lockstep. PRs D–E are additive.

## 8. Non-goals

- **Cross-firm mandates.** A mandate's end-client must belong to the
  operator's firm. Cross-firm acting is a separate, much larger,
  compliance discussion.
- **Per-instrument scoping inside a mandate.** Mandates are
  end-client-level. Per-symbol restrictions belong in the existing
  risk pipeline.
- **Dynamic mandate issuance UI.** Config-only for v0; the admin
  endpoint is a v1 problem.
- **Per-mandate kill-switch.** Kill-switch stays `(firm, endClient)`-
  scoped. A mandate becoming unusable is a revocation, not a kill.

## 9. Open questions

- Naming: `Mandate` vs `Agency` vs `Authorization`. `Mandate` matches
  the Portuguese legal term ("mandato") and is what the buy-side
  desks will recognize; sticking with it unless reviewers object.
- Should `OperatorId` and `EndClientId` share a string namespace
  (`alice` could be both an operator login and an end-client id) or
  be in disjoint namespaces? Leaning disjoint to keep mistakes
  loud — they are different domain concepts.
- Should the self-mandate be implicit (synthesized for any
  end-client the operator's `sub` matches) or explicit in config?
  Implicit keeps retail config trivial; explicit is more
  defensible. Leaning explicit-with-synthesis-helper.
- Per-mandate rate limits / per-end-client throttling — almost
  certainly yes eventually, out of scope here.

## 10. Risks

- **Verbosity.** Every endpoint gains a required field/parameter.
  Mitigated for retail by UI auto-filling; FIXP clients gain one
  extra SBE field. Worth the trade vs silent default routing.
- **Migration of existing tokens.** Tokens in flight on the day of
  PR B keep working (`sub` is still the only claim consulted; the
  resolver now uses it as `OperatorId`). The change is server-side.
- **Audit log breadth.** Adding `ActorOperatorId` + `MandateId` to
  every WAL event grows event size by ~50 bytes. Negligible at
  current throughput; revisit if we ever JSON-pack denser.
- **Coupling between domain and persistence schema.** Additive WAL
  field handled by existing JSON forward-compat conventions; no
  recovery risk.

## 11. References

- PR #183 — user-bot FIXP listener (per-credential single end-client;
  this RFC renames the binding to per-mandate)
- PR #287 — per-firm credentials (firm claim; orthogonal)
- `backend/src/B3.Trading.Application/EndClientRegistry.cs`
- `backend/src/B3.Trading.Api/OrdersEndpoints.cs` (current
  `ResolveOwner`, future replacement site)
- `backend/src/B3.Trading.Api/Auth/JwtIssuer.cs`
- Prior draft (this PR's previous commit) — `multi-end-client-v0.md`,
  rejected because it framed the change as auth plumbing rather
  than naming the missing domain concept.
