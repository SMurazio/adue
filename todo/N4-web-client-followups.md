# N4 — Web debug client (app.js) follow-ups

Severity: nit (low; debug client only)

These are independent small fixes in `src/Mmo.Client.Web/wwwroot/app.js`. They do not affect the
server or protocol. Each can be its own commit or grouped — your call.

## Items

1. **Unbounded `entityRegistry`.** The registry (~`app.js:429`) is added to on every spawn and never
   evicted, so it grows over a long session. Bound it or evict on true logout — but NOT on AOI
   despawn (the cached metadata is what lets a re-entering entity rehydrate from a state-only
   snapshot; evicting on despawn would regress re-entry to placeholder names).
2. **Large-delta tween glide.** Tween from current render position to the new tile always lerps over
   the fixed step duration (~`app.js:716-741`). A re-entry or teleport (Δtile > 1) glides smoothly
   across the map / through walls. Snap instead of tween when `|Δtile| > 1`.
3. **Defensive `entities`.** Guard against a snapshot whose `entities` is absent: use
   `message.entities ?? []` before `.length` / iteration (~`app.js:455`). (The server does not
   currently emit empty-payload snapshots, so this is hardening, not a live bug.)
4. **Self-identification by display name.** Self is identified by matching `name` (~`app.js:430`),
   which collides if two players share a display name. If the `LoginResult` / spawn flow can carry
   the self network id to the client, prefer that; otherwise leave a comment noting the limitation.

## Acceptance

- Manual web check still works (tile grid, walls, 8-way + right-click movement, no local movement
  before server confirmation, metrics panel).
- `run-checks.cmd` green (the `WebClientAssetTests` asset assertions still pass).
