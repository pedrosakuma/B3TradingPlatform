# Frontend (Phase 5)

Vanilla JS + Web Worker trader console for the B3TradingPlatform.
The runtime is still static nginx-served HTML/CSS/JS, but the source is bundled
with esbuild so the maintained `@azure/msal-browser` package can be used for
Authorization Code + PKCE.

## Layout

```
frontend/
  index.html        # login + trader shell, both views in one document
  css/styles.css    # all styling
  js/
    app.js          # entry point: wires login → worker → state → UI
    protocol.js     # REST client (login, submit, cancel)
    state.js        # main-thread cache + subscriber bus
    ui.js           # DOM rendering + user-action hooks
    worker.js       # Web Worker: WebSocket, snapshot/delta merge, reconnect
  package.json      # pinned MSAL + esbuild build/test scripts
  build.mjs         # bundles app/worker entries into dist/
  Dockerfile        # multi-stage build, small nginx:alpine runtime
```

## Message flow

```
                    ┌────────────────────────┐
                    │  index.html / ui.js    │  render-only
                    │  forms + tables + log  │
                    └──────┬───────────┬─────┘
            user gestures  │           │  postMessage events
                           ▼           ▲
                    ┌────────────────────────┐
                    │  app.js                │  orchestrator
                    │  REST + worker control │
                    └──────┬───────────┬─────┘
              fetch(REST)  │           │  postMessage(start/stop)
                           ▼           ▼
                  ┌──────────────┐  ┌─────────────┐
                  │  Backend     │  │  worker.js  │  WebSocket holder
                  │  /auth /ord  │  │  reconnect  │
                  │  ers /pos    │  │  apply ER   │
                  └──────────────┘  └──────┬──────┘
                                           │  postMessage(snapshot/delta)
                                           ▼
                                   ┌──────────────┐
                                   │  state.js    │  main-thread cache
                                   │  subscribers │
                                   └──────┬───────┘
                                          │  notify(slice)
                                          ▼
                                       ui.js renderers
```

The worker holds the WebSocket so the UI thread is never blocked by
incoming frames or reconnect timers. State lives on the main thread (the
worker is an event pipe, not a separate cache) — for the volumes
participant-side trading produces (one client's orders), this is more
than enough and keeps the rendering code synchronous.

## Run locally

Build once, then serve the generated static files from any HTTP server:

```bash
cd frontend
npm ci
npm run build
python3 -m http.server 8080 --directory dist

# Docker (builds with Node, runs on nginx:alpine)
docker build -t b3trading-frontend frontend && docker run --rm -p 8080:80 \
  -e APP_TITLE='Acme Trader' \
  b3trading-frontend
```

Then visit <http://localhost:8080>. Default backend
(`http://localhost:5000`) can be overridden in the login form.

When served from the Docker image, `frontend/js/env.js` is rendered at
container boot from deploy-time env vars. `MARKETDATA_WS_URL` optionally
seeds the Market Data panel's default WebSocket endpoint, `APP_TITLE`
overrides the browser/login/app-shell brand text, and the `AUTH_*` variables
configure Local/Hybrid/Entra login. Outside Docker, the checked-in
`frontend/js/env.js` defaults to Local mode, an empty market-data URL and
the `B3TradingPlatform` title.

CORS for the backend is configured by `Trading:Cors:AllowedOrigins` in
`appsettings.json`; default ships with `http://localhost:8080` and
`http://127.0.0.1:8080`. Add your origin there if you serve the
frontend elsewhere.

## State semantics

- **`orders.me`** is keyed by `clOrdId`; deltas replace the whole row.
- **`positions.me`** is keyed by `symbol`; rows with `netQuantity == 0`
  are removed.
- **`executions.me`** is append-only; ring-buffered at 500 entries
  client-side. The backend ships an empty snapshot on initial subscribe
  (no historical log in v1) — the worker preserves whatever has
  accumulated locally on a frame-level snapshot so that re-subscribe
  doesn't visibly clear the log mid-session, while a full reconnect
  clears all caches and refills from snapshots.
- On reconnect, only trader-WebSocket slices (orders, positions, executions,
  balance, phase and auction snapshots) reset before re-subscribe. REST,
  history, compliance, risk-policy, algorithm and operator-console state stays
  visible and is marked independently by its own loading/stale/error state.
- Deep-link startup passes the desired `pnl.me` / `algo.me` subscriptions in
  the worker start message, so the first socket open subscribes without
  requiring a tab-navigation side effect.
- The ticket's **Trading readiness** strip derives only from canonical
  trader-WebSocket, `/health.exchange`, market-data WebSocket, and
  per-symbol phase state. It explains degraded or blocked conditions but does
  not add a Submit gate: existing in-flight, validation, and `Reserved`-phase
  rules remain the client-side authority, with the server authoritative for
  intake and routing.
- A successful `POST /orders` first reports **platform acceptance**. The toast
  advances to **live order update received** only after the corresponding
  order appears in WebSocket state; the same delta drives the fresh-row cue in
  Working Orders.
- Chart, depth, tape, Working Orders, and Executions render contextual empty
  states. Empty first-use state is kept distinct from active filters returning
  no results, while market surfaces distinguish waiting-for-snapshot from a
  feed timeout or an empty snapshot.

## Operator and account controls

The authenticated ticket loads the caller's firm-scoped `/sub-accounts/` list;
admins can create or deactivate those same firm-scoped accounts in **Admin**.
The Admin console also exposes the existing authorized backend controls for
session phases, stale-order marking, resolved risk limits and reload, reference
prices, and cash deposits/withdrawals. Each mutation renders pending,
success/error feedback and refreshes its read model where the API provides one.

Option-chain selection carries the serialized instrument `securityId`,
`lotSize`, `tickSize`, and `contractMultiplier` into ticket validation and the
order payload. Missing metadata disables the series cell; selected metadata
older than five minutes must be refreshed before submit.

## Auth

Local mode (the default) uses `POST /auth/login`; the internal JWT + expiry
land in `sessionStorage`, with the legacy remember-me option mirroring only
local sessions into `localStorage`. The worker passes the internal token via
`?access_token=` because browsers cannot attach `Authorization` headers on the
WebSocket handshake. See [`docs/WEBSOCKET-PROTOCOL.md`](./WEBSOCKET-PROTOCOL.md)
for the log-hygiene caveat.

Hybrid/Entra mode uses MSAL Browser with Authorization Code + PKCE. Public
config is rendered from:

| Variable | Meaning |
| --- | --- |
| `AUTH_MODE` | `Local` (default), `Hybrid`, or `Entra`. |
| `AUTH_AUTHORITY` | Tenant-specific Entra External ID authority. |
| `AUTH_ISSUER`, `AUTH_TENANT_ID` | Backend exact issuer and tenant checks; required with Hybrid/Entra compose deployments. |
| `AUTH_CLIENT_ID` | Public SPA application client id. |
| `AUTH_API_SCOPE` | Delegated trading API scope requested by MSAL. |
| `AUTH_API_AUDIENCE`, `AUTH_REQUIRED_SCOPE` | Backend access-token audience and exact `scp` value for `/auth/exchange`. |
| `AUTH_REDIRECT_URI` / `AUTH_LOGOUT_URI` | SPA redirect and post-logout URLs. |
| `AUTH_KNOWN_AUTHORITIES` | Comma-separated trusted authority hosts for MSAL. |

No client secret is present in the SPA, image or compose examples. The Entra
access token is used only for `POST /auth/exchange`; the existing REST and `/ws`
modules consume only the returned internal JWT. Entra mode removes local
password/signup/TOTP/remember-me controls, ignores internal JWTs in
`localStorage`, stores the internal JWT in `sessionStorage`, and renews by
`acquireTokenSilent` + exchange. Firm/role UI continues to decode only the
internal token.

A timer renews Entra sessions before internal-token expiry when possible, or
falls back to an interactive MSAL redirect. Logout clears local state and then
invokes MSAL logout redirect without broadcasting token material between tabs.

## What's intentionally out of scope (v1)

- **Market data.** A second WebSocket directly to
  `B3MarketDataPlatform`, merged in-browser, lands in a follow-up. The
  market-data panel is reserved in the layout as a placeholder.
- **Charting.** Not a participant-side concern in v1.
- **i18n.** English only.
- **Mobile.** Desktop-first; the layout collapses gracefully on narrow
  screens but isn't optimized for touch.

## Where to extend

- **New WS channel.** Add the constant in `state.js` (snapshot+delta
  appliers), forward it in `worker.js` `handleFrame`, route it in
  `app.js` `onWorkerMessage`, render it in `ui.js`.
- **Toast / notification system.** Currently `setTicketFeedback` is
  the only inline status surface. A real toast queue would live in
  `ui.js`.
- **Optimistic order rendering.** Today the blotter waits for the WS
  delta; for higher latencies, push a "PendingNew" row from
  `handleSubmitOrder` and let the real delta replace it.
