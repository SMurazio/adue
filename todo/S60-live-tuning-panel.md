# S60 — Live tuning panel (admin) + generic server-side tuning mechanism

Severity: feature (tooling). Replace ad-hoc chat commands + edit-code-and-relaunch with an in-client
**admin tuning panel**: a window of named parameters with inputs + an Apply that changes behavior **live** —
client-local params instantly, server-authoritative params via a generic admin-gated message. Generalizes
the bespoke `/speed` command. **Ephemeral by design** (no persistence): the panel is for *finding* values;
the Orchestrator bakes winners into code/config defaults afterward.

## Protocol (bump from current version)
- New client→server message **`AdminSetTuning(string key, double value)`** — reliable-ordered. The server
  **validates the session is Admin** (same gate as `/speed` / `/metrics`); a non-admin request is ignored
  (+ a `ServerError` or silent drop — pick one, document it).
- (Optional, nice-to-have) server→client echo **`AdminTuningApplied(string key, double value)`** with the
  applied/clamped value so the panel can show the authoritative result. If you skip it for v1, the client
  shows the value it sent — note the choice.
- Update `docs/protocol.md` + the version.

## Server — generic tuning registry + a mutable tuning holder (the key design)
The tunable server params currently come from **immutable `ServerOptions`**, so live tuning needs a small
**mutable runtime tuning holder** (seeded from `ServerOptions` at startup) that the game loop READS for
those params, instead of reading `ServerOptions` directly.
- Introduce e.g. `ServerTuning` (mutable: step cooldown, interest radius, …), seed from options.
- Route the relevant reads through it: the step loop's cooldown, the AOI interest radius. (Find every read
  of these in `GameServer`/`Zone` and point them at the holder.)
- A **registry**: `key -> { clamp/validate, apply to the holder }`. `AdminSetTuning` looks up the key,
  validates/clamps, applies. Unknown key → ignored + logged.
- **Starter keys (v1):**
  - `move.stepCooldownMs` (global walk speed; clamp to the existing [50,5000]).
  - `aoi.interestRadius` (clamp > 0, sane max).
  - (Stretch if cheap: `move.turnCostTicks` (S59), `aoi.maxVisible`.)
- Applying live must be safe (these are read each tick; changing them mid-run is fine). No persistence.

## Client — Godot tuning panel (`MmoClientRoot.cs`)
- **Toggle key (e.g. F4), admin-only** (the client knows its role from `LoginResult`). A panel listing
  parameters, each: label + current value + an input (a `LineEdit`/`SpinBox`; a slider is a bonus), and an
  **Apply** button (apply-all, or per-field on Enter).
- **Two groups:**
  - **Client-local** (applied instantly, no server): camera zoom min/max, `RockModelScale`, label
    pixel-size/height. Wire these to the existing fields/consts (make the consts fields if needed).
  - **Server** (send `AdminSetTuning` on Apply): `move.stepCooldownMs`, `aoi.interestRadius` (+ stretch).
- Seed the fields with current values (client knows camera/rock/label values; for server params, use what
  it knows — `ServerHello` carries the step cooldown + interest radius — or just the last-applied).
- Keep it simple/functional; this is a dev tool, not shipped UI. Don't break existing input (WASD, mouse
  hold-to-move, E, chat, F3).

## Tests
- Codec round-trips for `AdminSetTuning` (+ `AdminTuningApplied` if added); version bumped.
- Server: an `AdminSetTuning` from an admin applies + clamps the value live (e.g. step cooldown changes the
  stepping cadence; interest radius changes AOI); a non-admin request is rejected/ignored and changes
  nothing; an unknown key is ignored. Existing movement/AOI tests still pass with the read routed through
  the holder (default values unchanged).
- `run-checks` + `godot-build` green; the panel itself is a human visual/feel check.

## Files
- `src/Mmo.Shared/Protocol/` (new message(s) + codec + version), `src/Mmo.Server/Runtime/` (ServerTuning
  holder + registry + handler; route reads), `src/Mmo.Client.Godot/MmoClientRoot.cs` (the panel),
  `docs/protocol.md`.

## Acceptance
- F4 opens an admin tuning panel; changing a client param + Apply updates it instantly; changing a server
  param + Apply changes server behavior live (verify: set step cooldown lower → everyone walks faster; set
  AOI radius → visible density changes). Non-admins can't tune the server. `run-checks` + `godot-build`
  green. Extensible (new key = one registry entry / one field). Do NOT commit — Orchestrator reviews.
