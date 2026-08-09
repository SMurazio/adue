# S — P2-B: in-context onboarding of the 4 duo verbs + pairing

Part B of the P2 demo (`docs/duo-p2-demo-plan.md`): teach the duo verbs + pairing WITHOUT a manual, so
a stranger duo can reach the Perfect version unprompted. The practice room (P2-A, landed) is the
tutorial space; this task adds the teaching layer. CLIENT work (Godot) — build it to compile + smoke
clean; the VISUAL/feel quality is a human feel-test (flag it, don't claim it).

## The verbs to teach (from the client map — 4 verbs, TWO mechanisms)
- **Q = fusion skillshot** — HOLD to aim, RELEASE to fire; "fusion" is the emergent CROSSING of BOTH
  partners' shots (server-side), the game's signature. Teach the cross, not just "shoot".
- **R = Shield**, **G = Tether**, **V = Detonate** (the 3-value `DuoAbilityKind`).

## Hooks (all exist — see the client map)
- **"Am I in the practice room?"** — `PracticeRoom` is in `Mmo.Shared`, so the client can call
  `PracticeRoom.ContainsInterior(ownTile)` on its own replicated tile (`MmoClient.OwnTile`/the local
  player's snapshot). This is the trigger to show the verb-teaching layer.
- **Pair state** — `MmoClient.IsPaired` / `PartnerNetworkId` / `PairVersion` (the lobby run panel
  already nudges "/pair <name>"; make pairing a clear, unmissable prompt when unpaired).
- **Existing teach pattern** — `Visuals/EntityVisual.cs:313` billboarded `Label3D` (today only the
  boss-plating cue). Reuse the pattern for in-world cues; a screen-space HUD hint layer is greenfield.
- The duo-input handlers (`MmoClientRoot.cs:806-817` R/G/V; `:4868-4961` Q) — so a hint can react to
  "you fired Q" / "you shielded" (e.g. dim a hint once the verb is used).

## Scope (keep it a FIRST PASS; feel-tuning comes after the live test)
- In the practice room: a clear, legible teaching layer for the 4 verbs — a screen-space hint panel
  and/or world-space labels near the dummy — that names each key and what it does, and specifically
  teaches the Q CROSS→fuse (the thing a pair must discover). Honest, calm, non-flashing (Law 7 tone;
  match the existing teach-label style).
- A prominent PAIRING prompt when unpaired (in the lobby / on entry), so two strangers pair without
  being told.
- Reasonable "learned it" behaviour is optional (dim/retire a hint once the verb is used) — nice, not
  required for a first pass.

## Guardrails
- CLIENT-only; no protocol/wire change expected (reads existing replicated state). If you need a wire
  change, STOP and surface it.
- Do NOT claim the visual/feel is validated — it is NOT (no live client in the loop). List exactly
  what a human must feel-test.
- Compile via `godot-build`; a headless `godot-run` smoke must not crash. Any client-logic that CAN be
  unit-tested (e.g. a pure "which hints show for this state" selector) — test it in `Mmo.Client.Core.Tests`.

## Acceptance
- In the practice room, the 4 verbs + the Q-cross are taught legibly; a pairing prompt shows when
  unpaired. `godot-build` compiles; `godot-run` smoke is clean; any extracted hint-selection logic is
  unit-tested. Delete this file in the landing commit. HUMAN feel-test owed (flagged in todo/README).
