# Frontend (Phase 5)

Vanilla JS + Web Worker trader console for the B3TradingPlatform.
Mirrors the layout convention of `B3MarketDataPlatform/frontend`. No
build step, no framework, no bundler.

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
  Dockerfile        # nginx:alpine static server
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

The frontend can be served from any static HTTP server. Two easy options:

```bash
# Python
python3 -m http.server 8080 --directory frontend
# Node (no install)
npx --yes serve frontend -l 8080
# Docker
docker build -t b3trading-frontend frontend && docker run --rm -p 8080:80 b3trading-frontend
```

Then visit <http://localhost:8080>. Default backend
(`http://localhost:5000`) can be overridden in the login form.

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
- On reconnect (any disconnect: network, server restart, token expiry),
  the worker drops all caches and re-subscribes; the UI resets, then
  refills from fresh snapshots. v1 has no replay buffer.

## Auth

Login uses `POST /auth/login`; the JWT + expiry land in
`sessionStorage` (cleared on tab close). The worker passes the token
via `?access_token=` because browsers cannot attach `Authorization`
headers on the WebSocket handshake. See
[`docs/WEBSOCKET-PROTOCOL.md`](./WEBSOCKET-PROTOCOL.md) for the
log-hygiene caveat.

A timer fires `logout()` automatically when the token expiry passes;
returning a `401` from REST also triggers logout.

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
