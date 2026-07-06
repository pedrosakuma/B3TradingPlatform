# Conformance scenario inventory

The conformance suite (`backend/tests/B3.Trading.Conformance/`) is the
executable contract for the **participant-side platform** — the API and
WebSocket surface that end-clients and the trader UI consume.

It is the sister of upstream
[`B3.EntryPoint.Conformance`](https://github.com/pedrosakuma/B3EntryPointClient/blob/bootstrap/issue-1/docs/CONFORMANCE.md)
(which is wire-puro, against the FIXP/SBE peer). Same operator
ergonomics — drop env vars, run the same suite against any deployed
instance, ship.

> Component-level tests (in-process `WebApplicationFactory<Program>`)
> live in `backend/tests/B3.Trading.Api.Tests`. Conformance only targets
> a real running process behind real HTTP.

## Configuration

Platform connection is read from environment variables. Tests use
`[ConformanceFact]` and are auto-skipped at discovery time when these
are absent so CI stays green without a deployed instance.

| Variable        | Description                                               |
| --------------- | --------------------------------------------------------- |
| `B3T_BASE_URL`  | Absolute base URL of the platform (e.g. `https://trading.uat.local`) |
| `B3T_AUTH_USER` | Username of a smoke-test account configured on the target |
| `B3T_AUTH_PASS` | Password for that account                                 |

To run the suite against a locally-running host:

```bash
export B3T_BASE_URL=http://localhost:5000
export B3T_AUTH_USER=alice
export B3T_AUTH_PASS=correcthorsebatterystaple
dotnet test backend/tests/B3.Trading.Conformance --filter "Category=Conformance"
```

## Inventory

### Bootstrap (this PR)

- **`Spec_HTTP_Auth/HelloLoginTests`** — single happy-path scenario:
  `POST /auth/login` with valid credentials returns a JWT, and that JWT
  is accepted on a protected endpoint (`GET /orders`). Smallest
  possible end-to-end: platform up, JWT pipeline wired, user store
  loaded.

### Real-stack recovery

- **`Spec_FIXP_SessionRoll/SuspendedTimeoutBoundarySpecTests`** —
  transport-fault boundary coverage for the venue FIXP suspend window:
  disconnect the matching-platform TCP leg **within**
  `SuspendedTimeoutMs` and assert the order re-syncs without a stale
  flag; disconnect **past** the timeout and assert the surviving order
  is flagged stale after renegotiation. Both recovery paths then submit
  a fresh post-reconnect crossed pair and assert trading is genuinely
  back: the new order becomes `Working` in `GET /orders`, then both legs
  transition to `Filled` (note: `GET /orders` is full history, so
  "leaves the book" is asserted as `Working` → terminal, not literal
  disappearance from the response). Requires the real-stack sandbox
  (`B3T_REAL_STACK_CONFORMANCE=true`) plus docker CLI/socket access for
  the test process (`B3T_DOCKER_CONTROL=true`; the
  `docker-compose.real-conformance.yml` overlay wires this automatically).

### Backlog (separate scenarios; add as the contract solidifies)

- **`Spec_HTTP_Orders/`** — `POST /orders` happy path + validation
  errors (missing fields, invalid side/type, `securityId == 0`,
  negative qty), `GET /orders` listing, `DELETE /orders/{id}` flow.
- **`Spec_HTTP_Risk/`** — kill-switch toggle round-trip, fat-finger
  rejection (price collar / max qty / max notional), position-limit
  rejection, all surfacing as synthetic ERs with the same shape as
  exchange rejections.
- **`Spec_WS_Subscribe/`** — connect with `?access_token=`, subscribe
  to `executions.me`, receive snapshot-then-deltas with monotonic
  sequence numbers; reconnect with `lastSeq` resumes losslessly.
- **`Spec_WS_Backpressure/`** — slow-consumer disconnect when the
  outbound ring saturates.
- **`Spec_Multi_Firm/`** — orders submitted under a JWT scoped to
  firm A do not appear in WebSocket fan-out subscribed under firm B.
- **`Spec_Lifecycle/`** — `/health`, `/ready`, `/live` shape;
  SIGTERM drain (`/ready` flips to 503; in-flight `POST /orders`
  completes; new `POST /orders` returns 503; WAL flushes; final
  snapshot lands).
- **`Spec_Recovery/`** — kill the process mid-flight, restart, assert
  the restored state matches what was last acknowledged on the wire
  (working orders, positions, kill-switch).

Add a new scenario by:

1. Picking the right `Spec_<area>/` folder (create one if needed).
2. Writing one `[ConformanceFact]` per testable requirement; one
   assertion per scenario, contract-level only — no white-box.
3. Updating this inventory.
