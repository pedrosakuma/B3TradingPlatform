# WebSocket protocol (Phase 2)

The platform exposes a single authenticated WebSocket endpoint that
streams order, execution, and position events to subscribed end-clients.

## Endpoint

```
GET /ws
```

Requires a valid JWT (see `docs/ARCHITECTURE.md` § Auth).

### Authentication

Bearer token can be supplied via either:

1. `Authorization: Bearer <token>` header — standard for non-browser
   clients (HTTP libraries, server-to-server).
2. `?access_token=<token>` query string — required for browsers, which
   cannot attach `Authorization` headers to WebSocket handshakes.

Query-string tokens are accepted only when the request path is `/ws`.
Operators must:

- Run `/ws` over `wss://` in production.
- Redact `access_token=` from request logs and reverse-proxy access logs.
- Use short token lifetimes (default 60 minutes; configurable).
- Avoid pasting full WS URLs into bug reports.

## Channels

Logical channels available to every authenticated client:

| Channel          | Contents                                                               |
| ---------------- | ---------------------------------------------------------------------- |
| `orders.me`      | Order state changes (new ack, replace, cancel, fill — current state)   |
| `executions.me`  | Each ExecutionReport (atomic event with ExecKind: Fill/Canceled/...)    |
| `positions.me`   | Net position changes (only fired by Fill / PartialFill)                |
| `algo.me`        | Algo parent state changes (PendingNew/Working/Cancelling/terminal)     |

Unknown channel names are rejected with an `error` frame.

## Frames

All frames are JSON. The envelope is:

```json
{
  "type":    "snapshot" | "delta" | "error",
  "channel": "orders.me" | ... | null,
  "seq":     <int>,
  "data":    <any>,
  "code":    "<error code>",
  "message": "<error message>"
}
```

`code` and `message` are only present on `error` frames.

### Subscribe

Client → server:

```json
{ "type": "subscribe", "channels": ["orders.me", "executions.me", "positions.me"] }
```

Server response: one `snapshot` frame per requested channel with
`seq = 0`. Snapshot data:

- `orders.me`: array of `OrderDto` for every working / completed order
  currently in memory for the authenticated end-client.
- `positions.me`: array of `PositionDto` for every symbol with non-empty
  position.
- `executions.me`: empty array (the platform does not retain a historical
  execution log in v1; only future events are streamed).
- `algo.me`: array of `AlgoDto` for every non-terminal algo parent owned
  by the caller (firm-scoped: a parent created under firm A is invisible
  to a connection authenticated as firm B even for the same end-client).
  Each `AlgoDto` carries one of `iceberg` / `twap` per the discriminator
  in `type`; the unused parameter block is `null`.

### Unsubscribe

```json
{ "type": "unsubscribe", "channels": ["positions.me"] }
```

Silent on the server side; subsequent deltas for the unsubscribed
channel(s) are simply not delivered to that connection.

### Delta

Server → client. Pushed whenever an `ExecutionEvent` is processed. `seq`
is monotonic per `(connection, channel)` pair, starting at `1` (the
snapshot is always `seq = 0`).

```json
{
  "type": "delta",
  "channel": "orders.me",
  "seq": 1,
  "data": {
    "clOrdId": "abcd-000000000001",
    "symbol": "PETR4",
    "side": "Buy",
    "type": "Limit",
    "quantity": 100,
    "leavesQuantity": 0,
    "cumulativeQuantity": 100,
    "price": 30.0,
    "status": "Filled"
  }
}
```

### Error

Server → client. Used for protocol-level rejections and for the
slow-consumer disconnect signal.

```json
{ "type": "error", "code": "unknown_channel", "message": "Channel 'foo' is not supported." }
```

Defined codes:

| Code                              | Meaning                                                     |
| --------------------------------- | ----------------------------------------------------------- |
| `invalid_json`                    | Inbound payload was not valid JSON.                         |
| `invalid_command`                 | Inbound command was missing the `type` field.               |
| `unknown_command`                 | `type` is not one of `subscribe` / `unsubscribe`.           |
| `unknown_channel`                 | Channel name is not in the documented whitelist.            |
| `frame_too_large`                 | Inbound frame exceeded the configured maximum.              |
| `slow_consumer_resync_required`   | Outbound buffer overflowed; reconnect and re-snapshot.       |

## Snapshot + delta semantics

- `snapshot` is a complete state replacement for the channel.
- `delta` is a state replacement for a single key (`clOrdId` for
  `orders.me`, `symbol` for `positions.me`, the `clOrdId` of the
  execution for `executions.me`).
- Clients MUST apply deltas idempotently — the platform may emit a delta
  whose state is already reflected in the snapshot when an ER is
  processed concurrently with a fresh subscription. Replacing-by-key
  with monotonically-non-decreasing fields (`cumulativeQuantity`,
  `seq`) handles this naturally.

## Reconnect

v1 has no replay buffer. On reconnect the client re-subscribes and
receives a fresh snapshot for each channel. A `since_seq` query parameter
is reserved for Phase 3 (resilience / ER-replay) and ignored today.

## Backpressure

Each connection has a bounded outbound queue (1024 messages). If the
queue is ever full when a new delta arrives, the platform refuses to
silently drop the message: it sends an `error` frame with
`slow_consumer_resync_required` and closes the socket. The client should
reconnect and re-subscribe.

## Limits

- Maximum inbound frame size: 8 KiB. Larger frames trigger
  `frame_too_large` and the connection is closed.
