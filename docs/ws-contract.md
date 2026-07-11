# BOYS Engine — WebSocket Live Stream Contract (v0)

The real-time feed the board/frontend consumes. One origin (nginx fronts it in E5), so no CORS. Clients are **read-only** — the engine never expects an inbound message; stray frames are discarded.

## Connect
```
GET  ws://<host>/ws/live?goal=<commitmentId>
```
- `goal` selects the commitment whose replay you want. The demo serves one commitment.
- **Unknown goal** → the server closes immediately with WebSocket close code **4404**.
- On connect you receive a **`snapshot`** first (your catch-up state), then a live stream of **`tick`** and **`status`** messages.

## Message shape
Every message is JSON with a `type`. **NAV is always integer cents** (never a float), consistent with the ledger and brain.
```jsonc
{
  "type": "snapshot" | "tick" | "status",
  "position": 42,            // index into the replay timeline
  "date": "2021-09-24",      // the sim-date this point represents
  "navCents": 14710,         // action-pool value in integer cents
  "events": ["Lakers cover +6"], // human-readable events at this point (tick only)
  "running": true,           // is the replay currently playing
  "terminal": true,          // present on the final tick of a completed replay
  "status": "degraded"       // status messages only: "healthy" | "degraded"
}
```

## The three message types
- **`snapshot`** — sent once, immediately on connect. Carries the current `position`, `navCents`, `date`, and `running` so a **late joiner is caught up** to exactly where the replay is. After the snapshot you receive **only subsequent** ticks — nothing you'd already have seen, nothing missed.
- **`tick`** — one per replay step: the NAV moved to `navCents` on `date`, with any `events`. The final tick of a run carries `terminal: true`.
- **`status`** — a health change broadcast to everyone: `"degraded"` when the engine loses brain and serves the cached curve, `"healthy"` on recovery (B20). The tick stream is **uninterrupted** across a status change.

## Ordering & delivery guarantees
- **Per-client ordering** is guaranteed and identical across clients — two browsers see the same sequence.
- **Backpressure:** a client that can't keep up (its send buffer fills) is **dropped** (connection closed) rather than allowed to stall the stream — every other client is unaffected.
- **Reconnect** re-issues a fresh `snapshot`, so a dropped or refreshed client re-syncs instantly.
