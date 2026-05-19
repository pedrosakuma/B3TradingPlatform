# RFC: Trader → multiple end-clients (multi-end-client v0)

| Field    | Value                                                              |
| -------- | ------------------------------------------------------------------ |
| Status   | Draft                                                              |
| Tracking | TBD                                                                |
| Replaces | n/a (extends current 1:1 user↔end-client mapping)                  |

## 1. Context

Today the platform binds **one authenticated principal to exactly one
`EndClientId`**. The mapping is implicit and lives in a single helper:

```csharp
// backend/src/B3.Trading.Api/OrdersEndpoints.cs (and peers)
private static EndClientId ResolveOwner(HttpContext ctx, EndClientRegistry registry)
{
    var sub = ctx.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
              ?? throw new InvalidOperationException("Authenticated request missing sub claim.");
    return registry.Register(sub);
}
```

`sub` (the JWT subject) **is** the `EndClientId`. Every downstream
ownership decision flows from this: WAL events (`OwnerId`), WS hub
filtering (`WebSocketHub`, `DropCopyWebSocketHub`), positions, P&L,
balance, history, cancel-by-id authorization, drop-copy fanout.

This is the right model for retail (one human, one CPF, one
sub-account at the broker). It is **wrong** for the institutional /
prop-desk model the platform also wants to support:

- A buy-side trader at a fund manages N portfolios, each booked
  under a different end-client (CPF/CNPJ) at the broker.
- A market-making team rotates the same human across multiple
  registered firms/desks for compliance reasons.
- A back-office user needs read access across the whole firm's
  end-clients (audit / reconciliation).

The `firm` claim already exists (PR #287, per-firm credentials) and
gives us **firm-level** isolation. What is missing is the orthogonal
axis: **one human inside a firm acting on behalf of N end-clients**.

This RFC scopes the smallest change that enables that without
breaking the retail path.

## 2. Goals

1. **Multi-owner principals.** A JWT can carry **a list** of
   `EndClientId`s the bearer is authorized to act for. Retail tokens
   keep carrying exactly one (back-compat).
2. **Explicit per-request selection.** When a token authorizes more
   than one end-client, the request **must** name which end-client
   the action is for (header / query param / submit-body field —
   chosen below). Ambiguity → 400, not silent guess.
3. **Authorization invariant: caller ∈ token.end_clients.** The
   resolver verifies the requested end-client is in the JWT's
   authorized set. Out-of-set → 403. No code path falls through to
   "register on first use" the way `EndClientRegistry.Register` does
   today.
4. **Transparent fan-out for read surfaces.** WS hubs, history,
   positions, P&L, balance default to "all end-clients the bearer
   owns" so a multi-owner user gets a unified read view without
   passing N requests. Filtering by a single end-client remains
   possible via the same explicit selector.
5. **WAL and audit unchanged.** Persisted `OwnerId` is still a single
   `EndClientId` per event (the *acting* end-client of the order).
   No schema change. No replay/snapshot risk.
6. **No FIXP listener regression.** The FIXP credential model
   (PR #183, `Trading:Auth:UserBots`) already binds one credential
   to one end-client; this RFC does not change that. A user with N
   end-clients gets N FIXP credentials (one per end-client) — same as
   today, the credentials issuer just unlocks per end-client.

## 3. Non-goals

- **Cross-firm acting.** A token authorized for end-clients across
  two firms is explicitly out of scope. `firm` claim stays singular;
  multi-firm humans get two tokens.
- **Role / permission split per end-client.** All authorized
  end-clients in a token share the same scope (orders/cancel/read).
  Per-end-client RBAC is a follow-up if/when needed.
- **Sub-account multiplexing.** `SubAccountId` (already optional in
  `SubmitOrderRequest`) is a different axis — sub-account-of-an-
  end-client. Untouched.
- **Multi-end-client in a single algo.** Algos remain owned by one
  end-client at submission time. A user managing N portfolios opens
  N algos.
- **UI overhaul.** A minimal selector (dropdown when N>1) is in
  scope; theming/multi-tab layouts are not.

## 4. Token shape

### 4.1 Claim

Add **one** new claim, `end_clients`, holding a JSON array of
end-client logins (lowercased). The legacy `sub` claim is kept as
the **default** end-client when no selector is provided.

```json
{
  "sub": "alice.fund",
  "firm": "fund-x",
  "end_clients": ["alice.fund", "fund-x.cnpj-001", "fund-x.cnpj-002"]
}
```

Back-compat rule: if `end_clients` is absent, treat it as `[sub]`
implicitly. Retail tokens continue to work unmodified.

### 4.2 Issuer

`JwtIssuer` (config-driven, `Trading:Auth:Users[]`) gains an
optional `EndClients: string[]` per user. Default = `[Login]` so the
existing config stays valid.

```yaml
Trading:
  Auth:
    Users:
      - Login: alice.fund
        Firm: fund-x
        Password: ...
        EndClients: [alice.fund, fund-x.cnpj-001, fund-x.cnpj-002]
```

## 5. Selector

A per-request selector picks **one** end-client out of the
authorized set:

- **REST submit (`POST /orders`, `POST /algos`)**: optional
  `endClient` field in the body. If absent, default to `sub`. If
  present but not in the authorized set → 403.
- **REST cancel (`DELETE /orders/{clOrdId}`)**: end-client inferred
  from the order's persisted `OwnerId` (already the case). Reject
  with 403 if `OwnerId ∉ end_clients`.
- **REST reads (`GET /history`, `/positions`, `/pnl`, `/balance`,
  `/fills`)**: optional `?endClient=` query param. Default = union
  across the authorized set (multi-owner fan-out).
- **WS hub (`/hub`, `/dropcopy`)**: optional `?endClient=` on the
  upgrade URL. Default = subscribe to all authorized end-clients
  (server-side fan-out filter).
- **FIXP listener**: no change. Each FIXP credential is already
  scoped to a single end-client at issue-time.

Rationale for "body field on submit, query string on reads": submit
mutates state, the selector belongs with the request payload (and
travels in WAL audit log); reads are URL-bookmarkable.

## 6. Resolver changes

Centralize the today-scattered `ResolveOwner` into a single
`IPrincipalOwnerResolver` that all endpoints (and the WS hubs)
consume:

```csharp
public interface IPrincipalOwnerResolver
{
    // For mutating requests: caller MUST request a specific end-client (or rely on default = sub).
    EndClientId ResolveActing(HttpContext ctx, string? requested);

    // For read requests: returns the full authorized set so the caller can build a fan-out filter.
    IReadOnlyList<EndClientId> ResolveAuthorized(HttpContext ctx);
}
```

Implementation pulls `end_clients` from claims (fallback to `[sub]`),
validates `requested ∈ authorized`, calls `EndClientRegistry.Register`
for each on first use (keeps the registry hot path identical).

`EndClientRegistry.Register` itself does **not** change — it stays
the trust boundary. Its callers (the resolver) get tightened to only
register identities the JWT already vouches for.

## 7. Affected surfaces

| Surface | Today | With this RFC |
| --- | --- | --- |
| `POST /orders` | owner = `sub` | owner = body `endClient` ∈ authorized, default `sub` |
| `POST /orders/{clOrdId}/modify` | owner verified from persisted order | unchanged |
| `DELETE /orders/{clOrdId}` | owner verified from persisted order | unchanged + 403 if persisted owner ∉ authorized |
| `POST /algos` | owner = `sub` | owner = body `endClient`, same rule as submit |
| `GET /history`, `/positions`, `/pnl`, `/balance`, `/fills` | filter by `sub` | filter by `?endClient` if given, else union of authorized |
| WS `/hub` (ER, algo events) | route by `sub` | route by `?endClient` if given, else union |
| WS `/dropcopy` | route by `firm` (already firm-scoped) | unchanged; `?endClient` for additional narrow |
| FIXP listener | end-client bound to credential | unchanged |
| WAL `OwnerId` field | single end-client per event | unchanged |
| Snapshot files | per `OwnerId` | unchanged |
| CashKeeper / PositionsKeeper | keyed by `(firm, endClient)` | unchanged |

The persistence layer is untouched — the unit of ownership remains
one end-client per event/order. Only the **identity → end-client(s)**
edge changes.

## 8. Trader UI

When a token returns `end_clients.length > 1`:

- Header gains an **end-client selector** (dropdown). Default =
  `sub`. Selection is persisted in `localStorage` per user.
- The selector value is:
  - Sent as the `endClient` body field on submit/algos.
  - Sent as `?endClient=` on history/positions/pnl reads.
  - Sent as `?endClient=` on the WS hub upgrade URL.
- "View all" sentinel un-sets `?endClient` (fan-out across authorized
  set on reads + WS; submits in "view all" mode are blocked client-
  side with a clear inline message).

When `end_clients.length === 1` the selector is hidden — retail UI
is byte-identical to today.

## 9. Migration plan

1. **PR A — claim plumbing (small).** Extend `JwtIssuer` to accept
   optional `EndClients[]` and to emit the `end_clients` claim. No
   behavior change; default `[Login]` keeps every existing token
   identical.
2. **PR B — resolver + back-compat tests (medium).** Introduce
   `IPrincipalOwnerResolver`, swap each `ResolveOwner` call site to
   it. Selector accepted but optional. Reject `endClient ∉
   authorized` (403). Add `(end_clients=[sub])` back-compat suite +
   one multi-owner happy path.
3. **PR C — read fan-out (medium).** Update history/positions/pnl/
   balance/fills + WS hub default filters to "union over
   authorized". `?endClient=` filter narrows. Cover with multi-owner
   integration tests.
4. **PR D — UI selector (small/medium, FE only).** Dropdown wiring,
   `?endClient` on hub URL, sentinel handling, localStorage persist.
5. **PR E — docs + ops sample.** Update `docs/auth.md` + a
   `docker/real/users.example.json` showing a multi-owner user.

Each PR independently reversible. A revert of PR D leaves the
backend speaking the new contract while the UI behaves as
single-owner — safe.

## 10. Risks and rejects

- **Token bloat.** `end_clients` is a JSON array — keep it bounded
  (cap at 32 entries in `JwtIssuer`; reject longer configs at
  startup). Anyone needing >32 should be talking to us about a
  different auth model.
- **Authorization drift between resolver call sites.** Mitigated by
  the single `IPrincipalOwnerResolver` choke point; lints reject
  raw `ctx.User.FindFirstValue("end_clients")` outside that file
  (manual code-review rule, not a Roslyn analyzer yet).
- **Replay risk.** Zero. `OwnerId` remains a single end-client per
  event and is sourced from the resolver at submit time, so replay
  reconstructs ownership from the WAL without consulting claims.
- **Cross-end-client P&L leakage in fan-out reads.** Each end-client
  is keyed independently in `CashKeeper`/`PositionsKeeper`; the
  fan-out happens at the query layer (`UNION` of per-end-client
  queries), not by relaxing the storage key. Bug class is "endpoint
  forgot to filter by authorized set", caught by the multi-owner
  test pack added in PR C.
- **STP across end-clients.** Today STP (#103/#118) is per-end-
  client. Multi-end-client humans intentionally want to net across
  their own end-clients; that's beyond v0 — explicitly NOT changed
  here.
- **Compliance.** Per-firm credentials (#262/#287) already isolate
  firms. Multi-end-client inside a firm does not weaken that. Per-
  end-client audit trail is preserved by the unchanged WAL
  `OwnerId`.

## 11. Open questions

- Should `?endClient=` accept a CSV (`a,b`) for narrower-than-all
  reads? Default no — keep it single — but cheap to add later.
- Do we need a server-side cap on simultaneous WS subscriptions per
  bearer when fan-out is active? Probably yes; defer to PR C.
- Surfacing per-end-client kill-switch state in the UI when
  `endClient.length > 1` — small extension; track separately.

## 12. References

- PR #183 — user-bot FIXP listener (per-credential single end-client).
- PR #287 — per-firm credentials (firm claim).
- `backend/src/B3.Trading.Application/EndClientRegistry.cs`
- `backend/src/B3.Trading.Api/OrdersEndpoints.cs` (current `ResolveOwner`)
- `backend/src/B3.Trading.Api/Auth/JwtIssuer.cs`
