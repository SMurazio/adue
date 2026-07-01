# N — retire the web client (Mmo.Client.Web) — it never migrated to continuous movement

User decision (test-audit): the browser client is being RETIRED. It still runs the OLD TILE-STEPPED movement model —
never migrated to continuous like the Godot client. Flagged here; do NOT delete yet (confirm the retirement is final +
that nothing depends on the web transport first).

**What it is:** `src/Mmo.Client.Web` (a C# server-side bridge) + its `wwwroot/app.js` browser client, which uses the
retired tile model — `tileStepTweenMs`, `confirmedStepQueue`, `startNextConfirmedStep`, `updateEntityTileTween`
(pinned by `tests/Mmo.Server.Tests/WebClientAssetTests.cs::WebClientUsesTileTweenedMoveSteps`). The server also has
`WebBridgeSession` (`WebBridgeSessionTests`) translating the binary protocol for it.

**Retirement (when confirmed):**
- Remove the `Mmo.Client.Web` project + `wwwroot/app.js` + any server-side web-serving/hosting wiring.
- Remove `WebBridgeSession` + `WebClientAssetTests` + `WebBridgeSessionTests` (their SUT goes with the client).
- Check the server doesn't otherwise depend on the web transport (it shouldn't — the Godot/console clients use the
  binary protocol directly).
- Update `docs/protocol.md` / any web-client references.

**Alternative if it's kept later:** migrate `app.js` to the continuous model (predict/reconcile + swept-circle
collision + the v39+ combined-flags snapshot) to match the Godot client — a much bigger effort than retirement.

From docs/test-audit.md Tier 3 (scope decision). Not blocking. Relates to [[entity-collision-predicted]] /
[[monster-behavior-architecture]] (the continuous migration the web client missed).
