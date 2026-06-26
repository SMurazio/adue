# Phase 7 — Positional / Continuous Combat (implementation spec)

Part of the continuous migration. Base: Phases 0–6 (movement/collision/AOI continuous). Combat still resolves on
integer `.TileCoord`. **The crux:** the combat geometry is ALREADY continuous + shared (`Mmo.Shared.Domain.FreeAimSector.IsHit`),
and the client ALREADY tests against continuous render positions — but the SERVER resolver feeds `(double)attacker.TileCoord`
+ `candidate.TileCoord` (the ROUNDED position) into the hit test. That systematic up-to-0.7-tile rounding divergence is the
"hit on client, miss on server" bug (the existing parity test passes only because its entities sit on tile centres). Phase 7
deletes the server rounding so both sides consume identical continuous positions. Near-mechanical; risk is concentrated in PARITY.

## Stage A — server resolver: continuous Position, not tile centres
`src/Mmo.Server/Runtime/FreeAimSectorResolver.cs`:
- Attacker origin (~:73-74): `(double)attacker.TileCoord.X/.Y` → `attacker.Position.X/.Y`.
- Candidate (~:91-92): `candidate.TileCoord.X/.Y` → `candidate.Position.X/.Y`.
- **Gather widen (fixes a latent under-gather):** the candidate gather is a superset tile-box keyed on `attacker.TileCoord`;
  today `Max(1, ceil(radiusTiles))` OMITS the body radius AND the attacker's own sub-tile offset → a true hit can be silently
  dropped server-side once attackers are off-grid. Change to `gatherRadiusTiles = Max(1, (int)ceil(radiusTiles + EntityHitRadiusTiles) + 1)`.
  Keep the box centred on the rounded tile (a proven superset; the precise `IsHit` filters). Over-gather is free; under-gather drops hits.
- `FreeAimSector.IsHit` is pure double geometry — UNCHANGED. Update the resolver's stale "tile-centre" header comment.

## Stage B — aim & facing (confirm, no logic change)
Aim = the client-sent quantized angle (`AimAngle.ToRadians`, continuous, NO tile dependency — the resolver never reads `Facing`).
`Direction8` facing is animation/fallback-aim only, NOT in the hit test. Assert this with a facing-independence test (Stage F).

## Stage C — retire the dead tile-fan melee (verify-dead-first)
`MeleeCone` + `MeleeConeResolver.ResolveAndDamage` are TEST-ONLY/dead (live path is `HandleAttack → FreeAimSectorResolver`).
**But `MeleeConeResolver` hosts two LIVE gates** — `IsAttackableEnemy` (friendly-fire gate, called from `FreeAimSectorResolver.cs:79`
+ `GameServer.cs:1339,1850`) and `IsRegeneratingEnemy` (`GameServer.cs:1357`). **RELOCATE those two verbatim** to a neutral home
(`CombatTargeting` static, or onto `FreeAimSectorResolver`); update the live call sites; THEN delete `MeleeCone.cs`,
`MeleeConeResolver.cs`, `MeleeConeTests.cs`, `MeleeConeResolverTests.cs`. **KEEP `AttackKind.MeleeCone`** — it's the live WIRE enum
value (not the geometry); renaming it is a protocol concern, out of scope. Separate commit from Stage A (bisect-friendly).

## Parity (Stage D/E — the crux, document it)
After Stage A, client + server AGREE: one shared `FreeAimSector.IsHit`, identical body radius (shared const), identical
half-angle/radius/damage (replicated `CombatTuningSnapshot`), identical aim (one `AimAngle` type), identical world convention,
and now identical CONTINUOUS positions. **The ONE residual divergence is TIMING (inherent to authoritative netcode, NOT fixable
here):** the client predicts at the swing with its predicted-present attacker + interpolation-delayed targets; the server resolves
at message-receive with confirmed positions. At a sector EDGE this flips occasional hits — but it's now sub-tile/symmetric jitter,
not the old systematic rounding. **Server-authoritative HP is always correct (the bar never lies); only the cosmetic floating
number can briefly mispredict.** STANCE: keep server-authoritative; keep the rounding deleted; do NOT add lag-comp/rewind (defer —
its own feature + exploit surface); a conservative client epsilon (under-predict) is a FLAGGED tuning lever, do NOT implement blind.

## Stage F — tests
`tests/Mmo.Server.Tests/FreeAimSectorResolverTests.cs`: keep the tile-centre cases; ADD sub-tile cases the tile math couldn't
express (target 0.1-tile inside vs outside the radius; attacker at a FRACTIONAL position — FAILS pre-Stage-A, the regression
guard; angular-edge fractional bearing). **Facing-independence** test (same hit across several `Direction8` → identical hit set).
**The client/server parity `[Theory]`** (the crux): over a sub-tile grid of attacker/target `WorldVector` + aim, assert
`FreeAimSector.IsHit(attacker.Position, …, target.Position)` (the client's exact call) == `FreeAimSectorResolver.ResolveAndDamage`
with entities at those continuous positions — generalizes the existing tile-centre-only parity theory to FRACTIONAL positions
(the bite point). A gather-superset test (body-clipping target at the box edge isn't dropped). Update `WorldEntityCombatTests`
tile-hit asserts → continuous. May need a test helper to place an entity at a fractional `Position` (`ApplyResolvedMove`/`TeleportTo`).
Delete `MeleeConeResolverTests`/`MeleeConeTests` (Stage C).

## Sub-commits
1. **A** — resolver continuous-position + gather widen (+ the new sub-tile/parity/facing tests). The parity-sensitive core.
2. **C** — retire dead tile-fan melee + relocate the live enemy gates (+ delete their tests). Pure dead-code/move.

## Risks
- **Parity at the edge (by design):** rounding deleted → only timing jitter remains (sub-tile, symmetric); HP always correct,
  only the cosmetic number can briefly lie. Document; conservative-epsilon only after live feel; lag-comp deferred.
- **Gather under-fetch (silent):** the old `ceil(radiusTiles)` omitted body+offset → a real hit dropped server-side; Stage A
  fixes it; the gather-superset test guards it.
- **Gate-relocation regression:** `IsAttackableEnemy`/`IsRegeneratingEnemy` are live (friendly-fire + regen) — move VERBATIM,
  keep `NoFriendlyFireAgainstOtherPlayers`/`AttackerDoesNotDamageItself` + the regen-set-narrowing tests pointed at the new home.
- **`AttackKind.MeleeCone` is a live WIRE value** — do NOT delete/rename in a "delete all MeleeCone" sweep.
- Fairness couples to the Phase-4 reconcile budget (a loose budget grows edge-mispredicts) — re-check under real latency.
