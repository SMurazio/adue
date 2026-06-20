# S94 — Live lever for the cosmetic lead distance (how far model B leads)

Severity: S (movement feel — user request; B is now the chosen model). Client-only; no protocol/server change.
Live F5 control (NOT a launch flag). Builds on S89/S91/S92.

## Why

Model B (cosmetic lead) is the chosen model. The user wants to tune **how far the cosmetic lead glides ahead**
of the confirmed tile — a shorter lead is less responsive but overshoots less (smaller release snap / camera
pop); a fuller lead is snappier. Today the lead distance is the hard-coded const `CosmeticLeadTiles = 1.0` in
`LocalPlayerCosmetic`. Make it a **live-tunable F5 value** so the user can find the sweet spot by feel.

## What to build

- `src/Mmo.Client.Core/LocalPlayerCosmetic.cs`: replace the `const double CosmeticLeadTiles = 1.0d` (`:35`) with
  a settable `public double MaxLeadTiles { get; set; } = 1.0d;` used by `ClampLead` (`:258-263`) exactly as the
  const is today. The forward glide still targets the adjacent tile center and `ClampLead` caps the SAMPLED
  render at `MaxLeadTiles` from the confirmed tile, so the value controls how far the visible lead reaches
  before holding. Clamp the setter input to `[0.0, 1.0]` (0 ≈ no visible lead — like accept/deny; 1.0 = one full
  tile, the current behavior and the max meaningful lead in the single-tile cosmetic model; note in a comment
  that >1 tile would require multi-tile cosmetic prediction, out of scope here). `LeadEnabled` gating is
  unchanged (when false there is no glide regardless of this value).
- `src/Mmo.Client.Core/MmoClient.cs`: add `public void SetCosmeticLeadTiles(double tiles)` and a
  `CosmeticLeadTiles` getter that route to the local entity's cosmetic driver (`_cosmetic.MaxLeadTiles`). Apply
  it to the cosmetic driver when it is (re)attached too (so a value set before the entity attaches, or after a
  respawn, is honoured — mirror how cadence/turn-delay are threaded). No-op safely when no cosmetic driver yet.
- `src/Mmo.Client.Godot/MmoClientRoot.cs`: add a live F5 field **"Cosmetic lead (tiles)"** (same `AddTuningField`
  + Apply pattern as the camera/zoom fields), range `[0.0, 1.0]`, default 1.0, seeded from the current value on
  panel open, admin-gated. On Apply/Enter it calls `_client.SetCosmeticLeadTiles(value)` — live, no restart.

## Tests

- `tests/Mmo.Client.Core.Tests/LocalPlayerCosmeticTests.cs`: with `MaxLeadTiles = 0.5`, glide east with no
  confirm and assert the held render settles at ~0.5 tile from the confirmed center (not ~1.0) — i.e. the lever
  bounds the visible lead. With `MaxLeadTiles = 0.0`, assert the render stays on the confirmed center while
  moving (no visible lead). Keep the existing `MaxLeadTiles == 1.0` (default) invariants green.
- `.\.shared\skills\mmo-dev\scripts\run-checks.cmd` green before/after.

## Constraints

- Client-only (client-core + Godot). No protocol/server/wire change; `Tile`/`LocalTile` stays confirmed-only.
  Default (1.0) keeps model B byte-for-byte. Live F5 control only — no restart to change the value.
- **Safe Local Execution** binds you (scripts only; if a live session locks `Mmo.Shared.dll`, stop via
  `stop-mmo.cmd` and note it). You cannot run Godot — the Orchestrator runs the live check.
- Do NOT commit, push, or delete the task file — leave the tree dirty + write
  `review/review-request-s94-cosmetic-lead-lever.md`; the Orchestrator verifies and commits. (Same loop as prior.)

## Acceptance

- A live F5 "Cosmetic lead (tiles)" field tunes how far model B leads, `[0,1]`, default 1.0 (= current), no
  restart. Lower values visibly shorten the lead (and the release snap). Tests as above; run-checks green.
