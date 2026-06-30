# Test-Suite Audit — tile-era cruft + redundancy (read-only)

Branch: `feat/continuous-migration`. Scope: every test file under `tests/` across the 3 projects
(Mmo.Server.Tests, Mmo.Client.Core.Tests, Mmo.Shared.Tests). Method: for each file, identify the SUT
(system under test), grep `src/` to prove whether the SUT is live or gone, and categorize
KEEP / DELETE / MERGE / DECISION. **This audit changed nothing** — it is analysis only.

Drives the `todo/N-test-suite-audit-tile-era-cruft.md` item.

---

## Summary

| Project | Test files | Test cases (≈) |
|---|---:|---:|
| Mmo.Server.Tests | 56 | ~447 |
| Mmo.Client.Core.Tests | 23 | ~209 |
| Mmo.Shared.Tests | 11 | ~162 |
| **Total** | **90** | **~818 attribute-occurrences ≈ 781 runtime cases** |

(818 = count of `[Fact]`+`[Theory]`+`[InlineData]`; the 781 figure is the xUnit runtime case count —
`[Theory]` with `[MemberData]` expands differently. Same suite, different counting.)

### Verdict counts (by case, estimated)

| Category | ≈ cases | Notes |
|---|---:|---|
| **KEEP** | ~730 | Live, non-redundant coverage. The overwhelming majority. |
| **DELETE (SUT genuinely gone)** | **0** | Nothing's SUT is provably removed. The tile-era prune already happened *during* migration. |
| **MERGE (redundant consolidation)** | ~25–35 | Belt-and-suspenders in the new monster + codec layers + the WorldEntity movement pair. No coverage lost. |
| **DECISION (judgment calls)** | ~25–30 | Non-asserting measure probes, one dead-code island, the un-migrated web client, brittle source-string guards. |

### The honest "reducible" number

**Realistically ~30–50 cases (~4–6%), and almost none of it is a clean delete.** There is **no tile-era test
rot to cut** — every suspicious-by-name SUT (AOI tile-gather, `TryBeginMoveInput`, `*Tiles` knobs,
`RemotePositionInterpolator`, `HopHeightUnits`, `BallisticArc`, `MovementCadence`) proved **live**. The
deleted tile-step machinery (`LocalPlayerPredictor`, `TileInterpolator`, `MonsterHopInterpolator`,
`MoveInput`/`StepCommitRequest`/`StepCommitBatch`/`MovementMode` messages, turn-delay, the cosmetic
`HopHeight`) exists in `src/` **only inside "deleted/retired" explanatory comments**, and **no test names any
of them as a SUT** — their round-trip/behaviour tests were removed alongside the code (see
`ProtocolCodecTests.cs:755`, `MmoClientProtocolTests.cs:221`, `ZoneContinuousCollisionTests.cs:120`).

**781 is NOT bloated with tile cruft. The suite is healthy.** The reducible items are (a) a few non-asserting
*measurement harnesses* kept past their decision (zero regression value), (b) **one dead-code island**
(`CursorHeading.FromTileDelta` + its 15 tests) that needs the **method and tests removed together**, (c) the
**un-migrated web-client test** as a scope decision, and (d) modest redundancy consolidation in this session's
monster work and the protocol codec.

---

## Proof: the deleted tile-step SUTs are gone, and untested

`git grep` for the retired symbols across `src/` returns only comments documenting their removal:

- `src\Mmo.Shared\Protocol\ProtocolCodec.cs:43` — "dead tile-step machinery (MoveInput / StepCommitRequest /
  StepCommitBatch / MovementMode) is DELETED".
- `src\Mmo.Shared\Protocol\MessageType.cs:16-17` — tags 8–11 left as numeric GAPS, survivors not renumbered.
- `src\Mmo.Client.Core\MmoClient.cs:18-19` — "the per-kind TileInterpolator / MonsterHopInterpolator split was
  retired… the obsolete tile LocalPlayerPredictor + its dead plumbing were DELETED earlier".
- `src\Mmo.Client.Godot\MmoClientRoot.cs:555,1347,3295,3908` — predictor/reconcile HUD hotkeys "removed with
  LocalPlayerPredictor".

No production class for any of them. `ProtocolCodecTests.cs:755` documents that their round-trip tests were
removed with them, and `MmoClientProtocolTests.cs:221` documents that the trace test no longer asserts the
deleted `LastSent*` machinery. **There is no lingering test for removed tile code.**

The tile **MAP/collision** layer is intentionally KEPT (per `.shared/memory/tile-continuous-cleanup.md`), so
`BlockedTiles`, `SpawnTiles`, `TileGrid`, `TileCoord`, `TileWalls`, and the `*Tiles` config knobs
(`WorldWidthTiles`, `EntityHitRadiusTiles`, `FreeAimRadiusTiles`, `ResourceNodeDensityTilesPerNode`) are LIVE —
the tests touching them are KEEP, not cruft.

---

## Prioritized "reducible" shortlist (orchestrator can action — none is a blind delete)

Ordered highest-confidence first. **None meets the strict "SUT gone" delete bar**, so each carries a one-line
rationale rather than a pure proof-of-absence.

### Tier 1 — non-asserting measure probes (zero regression value; cannot fail)

These run real work but assert **nothing** (or near-nothing), so they cannot catch a regression — they only
print numbers and burn suite wall-time. They are "measure-first probes kept past their decision."

1. `tests\Mmo.Server.Tests\CombatLagMeasureTests.cs` — **1 test, no asserts.** Header says verbatim "Not an
   assertion suite … it surfaces deltas." Spins a **real GameServer over UDP loopback** and runs ~24s of
   combat + a 40-slime pack, printing tick-cost deltas. Pure investigation harness. The combat-lag decision is
   made; this provides no guard.
2. `tests\Mmo.Server.Tests\MonsterPerfMeasureTests.cs` — **3 tests, no asserts.** Header: "Not an assertion
   suite — it prints numbers." Each runs a 6000-tick (300s-sim) monster loop and prints wall-query / revision
   counts. Slime-lag probe past its decision.
3. `tests\Mmo.Client.Core.Tests\ContinuousDtBudgetRegressionTests.cs` →
   `Measure_DivergenceUnderHonestPlay` (4 `InlineData`, **no asserts** — pure logging) and
   `Repro_BuggyFixedTickCredit_HonestClampMeasured` (4, asserts only `snapCount==0`). Characterization probes;
   the real guards in this file (the ServerStall buggy/fix pair, `Fix_RealElapsedCredit`, the anti-speedhack
   test) **stay**.

Net Tier-1 reducible: **~12 cases.** Recommendation: these are the cleanest candidates to drop (or move to a
`[Trait("Category","Measure")]` excluded-by-default lane) — but it is a DECISION because they are deliberately
re-runnable diagnostics.

### Tier 2 — one dead-code island (remove method + tests together)

4. `CursorHeading.FromTileDelta` is **dead production code**: `git grep FromTileDelta` returns only its own
   definition at `src\Mmo.Client.Core\CursorHeading.cs:19`, a comment at line 38 ("This replaces the S56
   FromTileDelta path"), and the 3 test methods. **Zero production callers** — the live cursor path is
   `FromWorldVector` (`MmoClientRoot.cs:886`). The 15 test cases
   (`MapsUnitDeltaToCardinalAndDiagonalDirections` 8, `SameTile_ReturnsNull` 1,
   `MapsFarOffAxisDeltaToNearestSector` 6) in `tests\Mmo.Client.Core.Tests\CursorHeadingTests.cs:15-54` also
   duplicate the octant mapping already covered by `FromWorldVector_OutsideDeadZone`.
   **DECISION:** delete the `FromTileDelta` method AND its 15 tests **together** (do not orphan untested-but-present
   prod code by deleting only the tests). The `FromWorldVector` tests in the same file are excellent — KEEP.

### Tier 3 — scope decision (whole feature)

5. `tests\Mmo.Server.Tests\WebClientAssetTests.cs` → `WebClientUsesTileTweenedMoveSteps` (1 test) pins the web
   client's `app.js` still running the **OLD tile-stepped** model (`tileStepTweenMs`, `confirmedStepQueue`,
   `startNextConfirmedStep`, `updateEntityTileTween`). The Godot client migrated to continuous; **the web
   client was never migrated.** The asset exists, so the test passes and is NOT a provable delete — but it is a
   genuine **DECISION:** is the `Mmo.Client.Web` browser client still in scope? If it is being retired, this
   test + the asset go; if it stays, the test is correct and should KEEP.

### Tier 4 — clean MERGEs (consolidation, no coverage lost)

See the per-cluster redundancy section below. Highest-confidence: the 3 v38 codec tests subsumed by the v39
flag-combo tests (~40 lines, zero loss).

---

## Biggest redundancy clusters (MERGE — internal consolidation)

### A. ProtocolCodec — v38 VerticalOffset tests ⊂ v39 flag-combo tests (strongest, lowest-risk)
`tests\Mmo.Shared.Tests\ProtocolCodecTests.cs` — three Phase-B1/v38 tests are strict subsets of the v39
remote-walk flag-combo tests (which exercise the same VerticalOffset wire PLUS velocity in the combined flags
byte):
- `EntityStateGroundedVerticalOffsetRoundTripsAsZero` (L98) ⊂ `EntityStateRestingGroundedRoundTripsAsFlagsZero` (L165)
- `EntityStateAirborneVerticalOffsetRoundTripsWithinOneSixteenth` (L119) ⊂ `EntityStateAirborneNotMovingRoundTripsWithHeightNoVelocity` (L209)
- `EntityStateMixedGroundedAndAirborneRoundTrips` (L141) ⊂ `EntityStateAllFourFlagCombosInOneSnapshotRoundTripAligned` (L255)
→ Delete the 3 v38 tests; the v39 four-combo suite covers strictly more. **~40 lines, zero coverage loss.**
(Optional second pass: ~10 single-`Equal(original,decoded)` record round-trips could collapse into one
`[Theory]` — but that loses per-message named documentation; propose, don't force.)

### B. WorldEntityMovementTests ∩ WorldEntityIntegratorTests (~4 duplicated cases)
`tests\Mmo.Server.Tests\WorldEntityMovementTests.cs` and `…\WorldEntityIntegratorTests.cs` both pin: continuous
advance, instant-stop (no glide), zero-dir stop, and tile-cross StateRevision bump. Merge into one
`WorldEntityContinuousMovementTests`, keeping the integrator file's unique diagonal/double-speed/zero-dt cases
and the movement file's unique root-gate (`IsMovementFrozen`/`ApplyAttackMovementRoot`) cases; drop the ~4
duplicated crossing/stop assertions.

### C. Tile-gated-resend bug double-guarded (cross-file; keep the faithful one)
`tests\Mmo.Server.Tests\ContinuousServerStallRegressionTests.cs` (real `WorldEntity.IntegrateMovement` + real
Q12.4) and `…\ContinuousTileGatedReconcileTests.cs::Migration_TileGatedRevision_*` (hand-rolled `Sim`)
reproduce the **same** shipped fix. Keep the ServerStall version as the faithful guard; the TileGated file also
holds the unrelated `ContinuousStopTransitionTests`, so do not delete the file — just accept or demote the
modelled duplicate.

### D. ContinuousTileGatedReconcile internal twin
`Experiment_LivePosEveryTick_NoCorrection` and `Fix_PositionRidesEveryTick_CorrectionCollapses` build the same
`new Sim(tileGatedRevision:false)`, run `RunHeldEast(4)`, and assert the same MaxCorrection≤0.1 + no-backward —
the same toggle/path. Fold into one.

### E. Monster manifest/registry belt-and-suspenders (the densest new-session cluster: 38+18 = 56 cases)
`tests\Mmo.Server.Tests\MonsterTypeManifestTests.cs` + `…\MonsterTypeRegistryTests.cs`:
- `OmittedBehaviorIdDefaultsToBasicRoamer` + `OmittedLocomotionIdDefaultsToHop` ⊂
  `OmittedOptionalFieldsFallBackToTheTypeDefaults` → fold the two literal-string checks into the latter, delete
  the standalone pair.
- `DuplicateTypeIdIsRejected` + `DuplicateTypeIdIsRejectedCaseInsensitively` → one `[Theory]`, two JSON inlines.
- `DefaultHopAirborneIsShorterThanTheCadenceLeavingGroundedRest` ⊂ `EditingTypeHopDelayChangesTheHopCadence`
  (both assert the default 6/8/14 cadence + the hopDelay retune) → delete the former.
- `SnapshotFieldsAreDataDrivenIncludingHopKnobs` vs `SlimeSnapshotKeepsHopKnobsAndHidesGliderBehaviorAndChargeKnobs`
  both assert the slime show-hop/hide-moveSpeed-flee-charge membership → keep the one that also checks
  Min/Max/IsInteger bounds; drop the membership-only duplicate.
- Default hop reach (1.5u) re-pinned in `MonsterHopTuningTests.TuningHopDistanceChangesTheDistanceCovered`
  (default block) + `DefaultHopCoversHopDistance…` → drop the default block, assert only the tuned 4.0u.

Net E: **~5–8 cases.** The cross-entry-point clamp duplication (FromManifestJson clamps independently of
TryApply) is **defensible — keep.**

### F. Small/optional
- `FreeAimSectorResolverTests.SharedHelperReproducesResolverHitMiss` (7 tile-centre rows) ⊂ the fractional-position
  theory — keep as a labelled baseline or fold (low value).
- `CatoFacingFlipTests` 2 `UserRepro_*` Facts subsumed by the Theories (trivial wash).

---

## Other DECISION items (judgment calls for the human)

- **B2 measure-first probe** — `ServerActionExecutorTests.Measure_PredictionLeadAlongTheArc_UnderLatency`
  (commit 6881c71, "decides Model A"). Unlike Tier-1 probes this one **does assert** (linear-lead == rtt×0.5,
  determinism convergence) and the commit notes it "doubles as a determinism-convergence regression guard." But
  its landing-convergence asserts duplicate `ForwardArcJump_ReachesForwardTarget` and its determinism overlaps
  `Determinism_IdenticalTrigger_YieldsByteIdenticalPath`. DECISION: keep as a guard, or trim to just the
  unique lead-measurement asserts. The model decision is already recorded in `todo/`.
- **GodotClientProjectTests.cs** — `Assert.Contains("…source text…")` guards on `project.godot` +
  `MmoClientRoot.cs` strings (exact constructor args, monitor names). Break on innocuous refactors; assert code
  text, not behaviour. Highest-maintenance/lowest-robustness file. Candidate to thin to the load-bearing
  asserts (GL-compat renderer + admin-gating short-circuit). KEEP for now.
- **ActionIntentHandlerTests.cs** — `ActionIntentHandler` is a **test-only** name; the test reconstructs the
  private `GameServer.HandleActionIntent` gate order over live SUTs. If production reorders the gates the test
  won't notice. Low risk; KEEP, just aware it models the wire rather than driving it.
- **BasicRoamerBehaviorTests robustness (flaky-risk note from the todo):**
  `ChaseEuclideanConvergesThenAttacksAtAttackRangeUnits` places the player at Euclidean **exactly 6.0** with
  `aggroRadius` default **6.0** — it relies on the aggro comparison being inclusive (`<=`). Not RNG-flaky today
  (radius ≥ distance holds), but boundary-fragile: if aggro ever tightens to strict `<`, it silently falls back
  to RNG roam and the distance assertions flake. Widen the gap (player at ~5 tiles). All other chase tests
  correctly force aggro first — no other RNG-roam flake found.

---

## Per-file index (all files; category + one-line SUT note)

Legend: K=KEEP, M=KEEP-with-internal-MERGE, D=DECISION. **No file is a provable DELETE.**

### Mmo.Server.Tests
| File | Cat | SUT / note |
|---|---|---|
| ActionIntentHandlerTests | D | Reconstructs private HandleActionIntent over live executor/session. |
| AdminTuningIntegrationTests | K | Admin tuning end-to-end gate (live; 4 distinct paths). |
| AoiIntegrationTests | K | GameServer AOI replication / self-heal (live continuous). |
| AoiSelectionTests | K | `IsEntityInInterest` float gate (live). |
| BasicRoamerBehaviorTests | K/D | Roam/aggro/chase/hop-through-executor (live). One boundary-fragile case. |
| ClientSessionTests | K | ClientSession cursors / `TryBeginMoveInput` (LIVE method). |
| CombatLagMeasureTests | **D** | **Tier-1 probe — no asserts, real-server ~24s.** |
| ContinuousServerStallRegressionTests | K | Real-path tile-gated-resend fix guard (cross-file dup w/ TileGated). |
| ContributionLedgerTests | K | ContributionLedger (live). |
| CorpseTests | K | Corpse lifecycle (live). |
| DuplicateLoginIntegrationTests | K | Takeover/kick (live). |
| FreeAimSectorResolverTests | M | FreeAimSectorResolver + shared IsHit; one subset theory. |
| GameServerMonsterSaveTests | K | TrySaveMonsterTypes (real file seam). |
| GlideLocomotionTests | K | GlideLocomotion contract (live, 7 orthogonal). |
| GodotClientProjectTests | D | Brittle source-string guards (live but maintenance-heavy). |
| InteractHarvestIntegrationTests | K | Interact/harvest/loot/persistence (live). |
| InventoryTests | K | Inventory engine (live). |
| LaunchScriptTests | K | start-server script + safe-exec guard. |
| LogTests | K | Log infra (live). |
| LootTableTests | K | LootTableRegistry (200k seeded rolls; deterministic). |
| MonsterChargeTests | K | Charge ability (high-risk; well-targeted; aggro forced). |
| MonsterHopPacingIntegrationTests | K | Interp-cadence decoupling (real GameServer). |
| MonsterHopTuningTests | M | Executor hop from registry knobs; minor default re-pin. |
| MonsterPerfMeasureTests | **D** | **Tier-1 probe — 3 tests, no asserts, 6000-tick sims.** |
| MonsterSeparationTests | K | MonsterSeparation (7 orthogonal; deterministic). |
| MonsterSpawnerTests | K | Spawner lifecycle (5 tight). |
| MonsterTypeManifestTests | M | Manifest loader; selector-default + dup-id merges (see E). |
| MonsterTypeRegistryTests | M | Registry/clamp/snapshot; hop-cadence + slime-hiding dups (see E). |
| MovementSpeedCommandIntegrationTests | K | `/speed` admin command + AOI (live). |
| NeighborhoodWallsParityTests | K | `QueryNearbyWalls` == shared `TileWalls` (predict/server determinism). |
| NetworkIdPoolTests | K | Id pool (live). |
| PersistenceWriteBehindIntegrationTests | K | Write-behind + continuous WorldVector save. |
| PreciseTickSchedulerTests | K | Tick scheduler (live). |
| RawDirectionNormalizeIntegrationTests | K | Receive-path `rawDir.Normalized()` speed-exploit guard (live). |
| ResourceNodeTests | K | ResourceNode (live). |
| ResourceRespawnScheduleTests | K | Respawn schedule (live). |
| ServerActionExecutorTests | K/D | Ballistic-jump executor (live). B2 probe inside = DECISION. |
| ServerMetricsTests | K | Metrics (live). |
| ServerMovementTraceTests | K | `ServerMovementTrace` tick-hitch diagnostic (LIVE; src exists). |
| ServerOptionsTests | K | `ServerOptions.FromEnvironment` (`*Tiles` env vars live). |
| ServerRuntimeGuardTests | K | Runtime guard (live). |
| ServerTuningTests | K | ServerTuning/Registry; `StepCooldownIsPinned` is a retired-key *guard*. |
| SkirmisherBehaviorTests | K | SkirmisherBehavior (live; flee overlap is intentional). |
| SpatialAoiParityTests | K | Gather superset vs float filter parity (anti-cheat gate). |
| SqliteCharacterItemsTests | K | Item persistence (live). |
| SqliteCharacterRepositoryTests | K | Repo + migrations (Phase-10 pos columns). |
| TickBudgetRecorderTests | K | Budget recorder (live). |
| WebBridgeSessionTests | K | `WebBridgeSession.TryParseDirection` (live). |
| WebClientAssetTests | **D** | **Web client still tile-stepped — scope decision (Tier 3).** |
| WorldEntityCombatTests | K | WorldEntity combat / swing-root (live server side). |
| WorldEntityIntegratorTests | M | `IntegrateMovement` (live); ~4 dup w/ MovementTests (see B). |
| WorldEntityMovementTests | M | Continuous player path + root-gate (live); ~4 dup w/ Integrator. |
| WorldEntityStatsTests | K | Vitals (live). |
| WorldStateTests | K | WorldState add/remove/copy (live). |
| ZoneContinuousCollisionTests | K | Zone swept-circle collision (Phase-2 flip; live). |
| ZoneTests | K | Zone spawn/scatter/collision (live; tile MAP layer KEPT). |

### Mmo.Client.Core.Tests
| File | Cat | SUT / note |
|---|---|---|
| AttackCooldownFractionTests | K | `ComputeCooldownFraction` HUD math (live). |
| CameraFocusTrackerTests | K | `CameraFocusTracker` (live; tile follow-blend already removed). |
| CatoFacingFlipTests | M | `CatoFacingFlip.Resolve` (live); 2 UserRepro facts subsumed. |
| ClientInventoryTests | K | ClientInventory (live). |
| ContinuousDtBudgetRegressionTests | K/D | dt-budget arithmetic (live); 8 weak/no-assert cases = Tier-1. |
| ContinuousPredictorActionTests | K | Real ContinuousPredictor + ServerActionExecutor (B2 jump gate). |
| ContinuousPredictorTests | K | Foundational `ContinuousPredictor` unit tests (live). |
| ContinuousReconcileHarnessTests | K | Timing-faithful reconcile harness (live). |
| ContinuousTileGatedReconcileTests | M | Stop-edge fix + tile-gated repro; Experiment≡Fix twin (see D). |
| CursorHeadingTests | D | `FromWorldVector` KEEP; **`FromTileDelta` 15 cases = dead-code island (Tier 2).** |
| HarvestTargetingTests | K | `HarvestTargeting` Euclidean gate + client/server parity (live). |
| MmoClientGatherTests | K | Interact/inventory/loot/corpse mirror (live). |
| MmoClientIntegrationTests | K | Real server+client UDP; continuous PredictAndSendMove (live). |
| MmoClientProtocolTests | K | Snapshot reassembly/ack/cadence (live; L221 comment verified). |
| MmoClientReattachSeqTests | K | Phase-4a re-attach seq high-water guard (real bug). |
| MovementCadenceTests | K | `MovementCadence.EffectiveStepCadenceMs` LIVE (comment notes its origin). |
| MovementSpeedOptionsTests | K | `MovementSpeedOptions` F6 dropdown (live). |
| NetLatencySimulatorTests | K | `NetLatencySimulator` diagnostic (live). |
| RemotePositionInterpolatorTests | K | LIVE float interpolator; `TileSteppedSource…` pins the new glide, not dead hop. |
| ScreenRelativeDirectionMapperTests | K | `FromInputAxes` LIVE WASD seam (not a retired mapper). |
| SnapshotContiguityTrackerTests | K | Tracker ring/window edges (live). |
| SnapshotGapConvergenceTests | K | Real tracker + ClientSession ack (live). |
| TerrainParityTests | K | Server Zone ↔ client ZoneModel parity (KEPT tile-map). |

### Mmo.Shared.Tests
| File | Cat | SUT / note |
|---|---|---|
| BallisticArcTests | K | `BallisticArc` real ballistic-Z (live; NOT cosmetic HopHeight). |
| CharacterStatsTests | K | Vitals clamp (live). |
| CombatTuningTests | K | `CombatTuning` ms→ticks server/client parity (live). |
| ContinuousCollisionTests | K | Swept-circle resolver + determinism (migration core). |
| Direction8Tests | K | `ToUnitVector` (continuous integrator bridge). |
| ItemRegistryTests | K | ItemRegistry (live). |
| PositionEncodingTests | K | Q12.4 snapshot codec primitive (live). |
| ProtocolCodecTests | M | 55 cases; v38⊂v39 merge (see A) + optional theory collapse. |
| TerrainGeneratorTests | K | Generator determinism + independent content-hash (live). |
| TileWallsTests | K | `TileWalls` tile→collision derivation (KEPT; parity linchpin). |
| WorldVectorTests | K | `WorldVector` algebra + tile bridges (live). |

---

## Bottom line

781 is **roughly right, not bloated.** The continuous migration was accompanied by disciplined test deletion —
the dead tile-step machinery left no orphan tests. The only true reducible weight is:
- **~12 non-asserting measure probes** (CombatLag, MonsterPerf, 2 DtBudget) — cannot regress; safest to drop or
  quarantine to a non-default lane.
- **One dead-code island** — `CursorHeading.FromTileDelta` + 15 tests, removed together.
- **One scope decision** — the un-migrated tile-stepped web client (`WebClientAssetTests`).
- **~25–35 MERGE cases** of belt-and-suspenders, concentrated in this session's monster manifest/registry pair,
  the WorldEntity movement/integrator pair, and the protocol codec — all consolidation, no coverage lost.

Everything else is cheap green insurance on live, high-risk netcode and server-authoritative logic. Do **not**
manufacture deletions to lower the number.
