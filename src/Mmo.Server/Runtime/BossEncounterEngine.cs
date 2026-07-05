using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// BOSS-1 (docs/boss-encounter-sunderer-design.md): the Sunderer encounter's lifecycle engine — a lightweight
// injected-seam tick engine in the TelegraphScheduler mould (own file, delegates for every world touch, so it is
// headlessly unit-testable against a bare WorldState + lambdas, and GameServer stays the single wiring point). It
// owns the arena's state machine and NOTHING about combat: the boss's Cleave/Lunge are the monster behavior's
// (the manifest "slam"/"charge" abilities), player damage is the PlayerDamageGate's, and the world-mutations
// (spawn/despawn/teleport/chat) are injected. This engine only decides WHEN — countdown, boss spawn (HP scaled by
// participant count), and the reset/victory rules.
//
// STATES:
//   * Idle      — no encounter. (May transiently hold post-VICTORY stragglers still standing in the arena until they
//                 /boss out — see the victory branch; a NEW /boss is refused until the arena clears.)
//   * Countdown — the pair has been teleported to the entry tiles; a 3 s chat countdown runs, then the boss spawns.
//   * Active    — the boss is alive; the engine watches for victory (boss dead), wipe (all participants dead), and
//                 empty (arena vacated → reset after a grace window).
//
// LIFECYCLE (design "Trigger + lifecycle"):
//   * /boss OUTSIDE the arena → TryBegin: store each entrant's return tile, teleport the issuer + (if paired &
//     online) their duo partner to the fixed entry tiles, run the countdown, spawn the boss at centre.
//   * /boss INSIDE the arena → TryLeave: teleport that player back to their stored return tile, drop them.
//   * all participants dead → immediate reset (boss despawn; adds cleared — none in BOSS-1).
//   * arena empties (everyone left / disconnected) → reset after EmptyResetSeconds.
//   * boss death → victory fanfare, boss cleaned up by its killer (KillMonster), encounter back to Idle; the victors
//     walk out via /boss (no auto-eject — return tiles are retained for them).
//
// TELEPORT SNAP (BOSS-1's highest-risk seam, verified by code-read): the teleports ride the NORMAL snapshot stream
// (WorldEntity.TeleportTo bumps StateRevision). The LOCAL player's ContinuousPredictor.Reconcile re-bases
// unconditionally (no anti-speedhack rejection) and its SnapThresholdUnits(4u) zeroes the cosmetic offset ⇒ a hard
// SNAP, and CameraFocusTracker hard-snaps past its 4-tile threshold ⇒ no cross-map chase. A teleported REMOTE entity
// (the partner as seen from the issuer's client) needed the one client change this task carries:
// RemotePositionInterpolator.Confirm now Resets on a jump beyond its teleport threshold instead of sliding.
public sealed class BossEncounterEngine
{
    public enum EncounterState : byte
    {
        Idle = 0,
        Countdown = 1,
        Active = 2,
    }

    // Spawn the boss at `tile` with `maxHealth` (scaled by participant count) and return its entity. GameServer wires
    // SpawnMonsterCore(sunderer type, tile, maxHealth) — NOT a world/region spawner, so the boss has no auto-respawn.
    public delegate WorldEntity SpawnBossDelegate(TileCoord tile, int maxHealth);

    // Despawn + fully clean up the boss by id (Zone.Despawn + executor/ledger/behavior/type-map/network-id cleanup),
    // NO corpse/loot. MUST be idempotent — a no-op if the boss is already gone (a player kill runs KillMonster first).
    public delegate void DespawnBossDelegate(ulong bossId);

    // Resolve an entity id to its live WorldEntity, or null if it is gone (despawned / disconnected). Used for boss
    // liveness (victory) and per-participant liveness/position (wipe / empty / walked-out).
    public delegate WorldEntity? TryResolveDelegate(ulong entityId);

    // Teleport a player entity to a tile (Zone.Teleport + clear its held move intent so it doesn't walk off from a
    // pre-teleport key-hold — the RespawnPlayers precedent).
    public delegate void TeleportPlayerDelegate(WorldEntity player, TileCoord tile);

    // Send a system/chat line to a participant entity's owning session (a no-op if the entity has no live session).
    public delegate void NotifyDelegate(ulong entityId, string text);

    // BOSS-2 (P1): spawn the interposer drone at `tile` and return its entity. GameServer wires SpawnMonsterCore(the
    // "interposer" type) — like the boss, NOT a world/region spawner, so the drone has no auto-respawn (this engine
    // owns the 6 s respawn cadence). The drone is a normal EntityKind.Monster with the "interposer" behavior/type, so
    // it steers itself (InterposerBehavior) and takes damage/dies through the shared paths with no special-casing here.
    public delegate WorldEntity SpawnDroneDelegate(TileCoord tile);

    // BOSS-2 (P1): despawn + fully clean up an encounter ADD (the interposer drone) by id — the SAME leak-free teardown
    // the boss uses (Zone.Despawn + executor/ledger/behavior/type-map/network-id cleanup, NO corpse/loot). Idempotent:
    // a no-op if the add is already gone (a player kill runs KillMonster first). Wired to DespawnBossEntity, which is
    // generic by id — the "adds list is cleaned everywhere the boss is" invariant the design requires.
    public delegate void DespawnAddDelegate(ulong addId);

    // BOSS-2 (P1) LEGIBILITY (Laws 4/7): broadcast the boss's PLATING state to AOI viewers — `platingActive` true when
    // the cold steel shell is up (damage reduced), false when it is shattered (a vulnerability window is open) or has
    // permanently crumbled below 70%. GameServer wires an AOI-scoped BossPlatingMessage broadcast (the BroadcastShield-
    // Status pattern). The engine fires it on plating-on (boss spawn), shatter (window open), reform (window close),
    // and permanent-off at 70%.
    public delegate void BroadcastPlatingDelegate(ulong bossId, bool platingActive);

    // BOSS-3 (P2 SUNDER): damage a PARTICIPANT through THE player-damage choke point (PlayerDamageGate.TryDamagePlayer)
    // — the ONLY player-damage path (the user's damage-choke invariant), so the dodge-roll i-frames + the Unison-Shield
    // absorb apply to every field/lash/pop hit for FREE. Returns true iff damage actually landed. NEVER mutate Stats
    // directly here.
    public delegate bool DamagePlayerDelegate(WorldEntity victim, int amount, uint serverTick, string source);

    // BOSS-3 (P2 Repel): server-authoritative DISPLACEMENT — set a participant's continuous position to `target`,
    // wall-resolved, migrating the spatial bucket + bumping StateRevision (Zone.DisplaceResolved). The reconcile snap on
    // the shoved local player is the accepted v1 (design "Knockback vs prediction").
    public delegate void DisplacePlayerDelegate(WorldEntity player, WorldVector target);

    // BOSS-3 (P2 Echo Lash): play the shield ECHO CUE on a participant's own client — REUSE the wave-2 EchoCueMessage
    // wire exactly as the shield upgrade path sends it (EchoCueKind.ShieldPress), so NO protocol change.
    public delegate void EchoCueDelegate(ulong entityId);

    // BOSS-3 (P2 Splinter ring): spawn a splinter ADD at `tile` and return its entity — the "splinter" type (glide
    // body, splinter brain, 15 HP, no loot), like the drone via SpawnMonsterCore (NOT a spawner — the engine owns the
    // re-ring cadence + teardown + the pop).
    public delegate WorldEntity SpawnSplinterDelegate(TileCoord tile);

    // BOSS-3 (P2 Repel/Bind): schedule a NO-DAMAGE telegraph ring as the field's VISUAL — REUSE the existing decal wire
    // (TelegraphScheduler.Schedule) with damage 0, a harmless auto-removing ring. The field RESOLVE is encounter-side
    // and judged on PAIR DISTANCE (not shape membership), so the visual carries zero gameplay weight — it only tells the
    // pair "a field is charging on you." `center`/`radiusUnits` size one ring; the engine calls it once per participant.
    public delegate void ScheduleFieldVisualDelegate(WorldVector center, double radiusUnits, uint startTick, uint resolveTick);

    // BOSS-4 (P3 CORE, docs/boss-encounter-sunderer-design.md "P3 — CORE"): ROOT the boss at the arena centre at the
    // 40% edge — teleport it to `tile` ONCE, cancel any in-flight action (a lunge dash mid-cast), and zero its velocity.
    // The ONGOING chase suppression is GameServer's IsBossRooted gate in StepMonsterAi (it skips the boss's brain +
    // re-zeroes velocity each tick), so this delegate is the one-shot re-centre; the engine owns WHEN (the P3 arm edge).
    public delegate void RootBossDelegate(WorldEntity boss, TileCoord tile);

    // BOSS-4 (P3 rotating sweep beam): schedule a LINE telegraph from the boss through the scheduler's NORMAL gate path
    // (real damage, dodgeable at the resolve tick — NOT the damage-0 field visual). GameServer wires
    // TelegraphScheduler.Schedule(bossId, TelegraphShape.Line(origin, length, aim, halfWidth), start, resolve, damage,
    // "Sunder beam"), so the beam rides the SAME player-damage choke point (i-frames + shield absorb apply for free).
    public delegate void ScheduleBeamDelegate(
        WorldVector origin, double lengthUnits, double aimRadians, double halfWidthUnits, int damage, uint startTick, uint resolveTick);

    // HP scaled at spawn by participant count (design "Boss stats"): 1200 duo / 700 solo.
    public const int DuoBossHealth = 1200;
    public const int SoloBossHealth = 700;

    // BOSS-2 (P1 "Sundered Plating", docs/boss-encounter-sunderer-design.md): while the encounter is Active, the boss's
    // HP is ABOVE this fraction of max, and NO vulnerability window is open, ALL damage the boss takes is reduced —
    // DuoDamageReduction (duo) / SoloDamageReduction (solo, the Law-2 degradation: weaker, never nullified). At/below
    // this fraction the plating is OFF permanently for the run (P2 mechanics arrive in BOSS-3).
    public const double PlatingHealthFraction = 0.70d;
    public const double DuoDamageReduction = 0.75d;
    public const double SoloDamageReduction = 0.40d;

    // BOSS-2 (P1): a fused skillshot (SkillshotEngine's merge) SHATTERS the plating — a full-damage vulnerability
    // window whose length is driven by the fusion tier (Law 8, tiered timing): Good = 6 s, Perfect = 9 s. Solo
    // fallback (Law 2): SoloShatterHitCount skillshot HITS on the boss within SoloShatterWindowSeconds shatter it for
    // the Good (6 s) window — no fusion is possible solo, so it degrades to a hit-count gate, never nullifies.
    private const double FusionGoodWindowSeconds = 6d;
    private const double FusionPerfectWindowSeconds = 9d;
    private const int SoloShatterHitCount = 3;
    private const double SoloShatterWindowSeconds = 6d;

    // BOSS-2 (P1 "Interposer drone"): ONE drone at a time. The engine spawns the first DroneFirstSpawnSeconds after the
    // boss, and respawns a new one DroneRespawnSeconds after the current one dies — only while the plating mechanics are
    // live (Active AND above 70%). Its 40 HP / 1.6 u/s / no-attack stats + midline-seek behaviour are the "interposer"
    // monster type + InterposerBehavior; this engine owns only the WHEN (spawn/respawn cadence + teardown).
    private const double DroneFirstSpawnSeconds = 5d;
    private const double DroneRespawnSeconds = 6d;

    // ==== BOSS-3 (P2 SUNDER, docs/boss-encounter-sunderer-design.md "P2 — SUNDER") ====
    // P2 mechanics are live while the encounter is Active AND the boss's HP is in (P3HealthFraction, PlatingHealthFraction]
    // — i.e. between 40% and 70%. Crossing 70% (the P1 crumble) ARMS them with staggered first-fire delays ANCHORED at
    // that tick; crossing 40% DISARMS them (adds cleared + P3 teaser) — P3 itself arrives in BOSS-4.
    public const double P3HealthFraction = 0.40d;

    // Repel / Bind FIELDS (S1 distance contest). First field FieldFirstDelaySeconds after the crumble, then every
    // FieldIntervalSeconds, ALTERNATING Repel↔Bind (duo). A FieldTelegraphSeconds ring decal telegraphs each; the
    // resolve is PAIR DISTANCE (not shape membership). REPEL: pair within FieldRepelTriggerRangeUnits at resolve →
    // FieldDamage each + knocked FieldRepelKnockbackUnits directly apart. BIND: pair farther than FieldBindTriggerRangeUnits
    // apart → FieldDamage each (no displacement). The lace's home band (8–12u) sits BETWEEN the asks (Law 6). SOLO (Law
    // 2): a single move-out ring, REPEL-only vs the BOSS (within range of the boss → damage + knockback away from it);
    // no Bind solo. FieldTelegraphRingRadiusUnits is cosmetic-only (the ring is a "brace" indicator, not the hit test).
    private const double FieldFirstDelaySeconds = 6d;
    private const double FieldIntervalSeconds = 9d;
    private const double FieldTelegraphSeconds = 1.2d;
    private const double FieldTelegraphRingRadiusUnits = 2.0d;
    private const int FieldDamage = 15;
    private const double FieldRepelTriggerRangeUnits = 6d;
    private const double FieldBindTriggerRangeUnits = 4d;
    private const double FieldRepelKnockbackUnits = 3d;

    // ECHO LASH (T1 sync pressure). Every LashIntervalSeconds (first LashFirstDelaySeconds after the crumble): the shield
    // ECHO CUE + a chat brace-line play for every participant, then LashPulseCountDuo (solo: LashPulseCountSolo) pulses of
    // LashPulseDamage each — the first LashCueLeadSeconds after the cue (reaction lead), the rest LashPulseSpacingSeconds
    // apart. EVERY pulse routes through the PlayerDamageGate, so i-frames + shield absorb apply naturally (an upgraded
    // unison shield eats both pulses; two solo shields eat one each; no shield = eat both). NEVER lethal from full HP BY
    // DESIGN — pressure, not a wipe check — so there is deliberately NO lethality special-casing here.
    private const double LashFirstDelaySeconds = 11d;
    private const double LashIntervalSeconds = 14d;
    private const double LashCueLeadSeconds = 1.25d;
    private const double LashPulseSpacingSeconds = 0.5d;
    private const int LashPulseDamage = 18;
    private const int LashPulseCountDuo = 2;
    private const int LashPulseCountSolo = 1;

    // SPLINTER RING (S7 vulnerability). RingFirstDelaySeconds after the crumble, then every RingIntervalSeconds:
    // SplinterCountDuo (solo: SplinterCountSolo) splinters spawn evenly on a radius-SplinterRingRadiusUnits ring around
    // the boss + creep toward the nearest living participant (SplinterBehavior); a splinter within SplinterPopRangeUnits
    // of one POPS for SplinterPopDamage (through the gate) + despawns. The tether orbit-sweep clears the ring — its
    // showcase moment. The splinter's 15 HP / 1.2 u/s / no-attack stats + the seek brain are the "splinter" monster
    // type; this engine owns the WHEN (ring cadence) + the POP.
    private const double RingFirstDelaySeconds = 8d;
    private const double RingIntervalSeconds = 20d;
    private const double SplinterRingRadiusUnits = 7d;
    private const int SplinterCountDuo = 6;
    private const int SplinterCountSolo = 3;
    private const double SplinterPopRangeUnits = 1d;
    private const int SplinterPopDamage = 12;

    // STAGGER-BY-CONSTRUCTION (task contract): with these constants @20 Hz, relative to the crumble anchor T0, a field
    // RESOLVES at T0 + 144t (≡ T0+4 mod 10), a ring SPAWNS at T0 + 160t (≡ T0+0 mod 10), and each lash PULSE lands at
    // T0 + {245,255}t (≡ T0+5 mod 10). Every cadence interval is a whole multiple of 10 ticks (9s=180 / 14s=280 /
    // 20s=400), so those residues never shift — the three resolve streams occupy DISJOINT residue classes {4},{0},{5}
    // mod 10 for ANY anchor, i.e. no two ever resolve on the same tick. (StaggerConstants_… pins this empirically.)

    // ==== BOSS-4 (P3 CORE, docs/boss-encounter-sunderer-design.md "P3 — CORE") ====
    // P3 is live while the encounter is Active AND the boss's HP has crossed <=40% (the P3HealthFraction edge P2 disarms
    // at). At that edge the boss ROOTS at centre and gains the CORE WARD: ALL damage to it is reduced to ZERO
    // (ModifyIncomingDamage returns 0) UNLESS a burst window is open. A midpoint DETONATION whose blast centre lands
    // within WardBreakRadius of the boss BREAKS the ward → an 8 s burst window (full damage) → then it REFORMS. Fusion
    // does NOT break the ward (verbs stay distinct — that is the P1 gate). The phase's contest is the ROTATING SWEEP
    // BEAM (sequential line telegraphs) + KNOCKBACK PULSES (radial shoves that move the pair's midpoint while they aim
    // the detonation — the S3 contest).

    // CORE WARD break (design "Ward break"): a midpoint blast centre within this radius of the boss breaks the ward.
    // Duo 2.5u; solo 3.5u (receiver-forgives generosity, Law 3). The mode is fixed at spawn (_participantsAtSpawn).
    public const double WardBreakRadiusDuoUnits = 2.5d;
    public const double WardBreakRadiusSoloUnits = 3.5d;
    private const double BurstWindowSeconds = 8d;

    // ROTATING SWEEP BEAM (line telegraphs — the honest-telegraph form of a rotating beam). The first beam BeamFirstDelay
    // after the root, then every BeamInterval, a line from the boss (length BeamLengthUnits reaches the walls from centre,
    // half-width BeamHalfWidthUnits, BeamWindup windup, BeamDamage through the scheduler's gate) at a bearing that ADVANCES
    // BeamBearingAdvance per beam in a CONSISTENT rotation direction (players learn to walk WITH it). Staggered off the
    // knockback by construction — see the residue note below.
    private const double BeamFirstDelaySeconds = 4d;
    private const double BeamIntervalSeconds = 3d;
    private const double BeamWindupSeconds = 1.2d;
    private const double BeamLengthUnits = 11d;
    private const double BeamHalfWidthUnits = 1d;
    private const int BeamDamage = 25;
    private const double BeamBearingAdvanceRadians = 40d * Math.PI / 180d; // ~40° per beam.

    // KNOCKBACK PULSES (S3 midpoint contest). The first pulse cue PulseFirstDelay after the root, then every
    // PulseInterval: an announce + a PulseCueLead brace window, then every LIVING participant is shoved PulseShoveUnits
    // radially AWAY from the boss (wall-swept via DisplaceResolved, NO damage). The interval is FIXED (the soft enrage
    // scales the beam, not the shove — design), so the shove residue never shifts.
    private const double PulseFirstDelaySeconds = 6d;
    private const double PulseIntervalSeconds = 10d;
    private const double PulseCueLeadSeconds = 1d;
    private const double PulseShoveUnits = 3d;

    // SOFT ENRAGE (design "below 10%"): once the boss's HP crosses <=10%, the BEAM cadence speeds up by ~30%
    // (EnrageCadenceScale = 1/1.3 on the interval) and a SPLINTER TRICKLE (one splinter every EnrageTrickleInterval,
    // reusing the P2 add machinery + pop) begins, with one announce. DEVIATION (documented in the review request): the
    // design lists "cleave cadence +30%" too, but the rooted boss's melee kit is dormant (its chase is suppressed), so
    // the enrage scales the BEAM only — the task's explicit fallback ("enrage the BEAM only and note it").
    private const double EnrageHealthFraction = 0.10d;
    private const double EnrageCadenceScale = 1d / 1.3d;
    private const double EnrageTrickleIntervalSeconds = 10d;

    // STAGGER-BY-CONSTRUCTION (task contract, the BOSS-3 residue pattern). @20 Hz relative to the P3 arm anchor T0: the
    // first beam CASTS at T0+80 and RESOLVES at T0+104, then every 60t → resolves ≡ 4 mod 10 for ANY anchor (60 is a
    // whole multiple of 10, so the residue never shifts). The first pulse SHOVES at T0+140 (cue T0+120 + 20t lead),
    // then every 200t → shoves ≡ 0 mod 10. {4} and {0} are DISJOINT, so a beam resolve and a shove never share a tick.
    // (Pinned by BeamAndPulse_StaggerByConstruction_NeverShareATick.) NB: the soft enrage re-paces the beam to a
    // non-multiple-of-10 interval, so the residue may drift under enrage — HARMLESS (the shove deals no damage, so a
    // beam+shove coincidence is at worst one tick where a player is both moved and beam-tested, never double damage).

    // The chat countdown before the boss spawns, and the grace window after the arena empties before it resets.
    private const double CountdownSeconds = 3d;
    private const double EmptyResetSeconds = 10d;

    // REVIEW BOSS-1 (MEDIUM): the arena is one shared, non-instanced room, and a fresh /boss is refused while any
    // participant lingers — so a connected victor who never types /boss would soft-lock the encounter for the whole
    // server. Victory therefore arms a bounded grace window: victors get this long to savor/loot-nothing/walk out,
    // then anyone still inside is teleported home and the arena clears. Mirrors the EmptyResetSeconds pattern.
    private const double VictoryEjectSeconds = 15d;

    private readonly int _tickRate;
    private readonly SpawnBossDelegate _spawnBoss;
    private readonly DespawnBossDelegate _despawnBoss;
    private readonly TryResolveDelegate _tryResolve;
    private readonly TeleportPlayerDelegate _teleport;
    private readonly NotifyDelegate _notify;
    private readonly SpawnDroneDelegate _spawnDrone;
    private readonly DespawnAddDelegate _despawnAdd;
    private readonly BroadcastPlatingDelegate _broadcastPlating;
    private readonly DamagePlayerDelegate _damagePlayer;
    private readonly DisplacePlayerDelegate _displacePlayer;
    private readonly EchoCueDelegate _echoCue;
    private readonly SpawnSplinterDelegate _spawnSplinter;
    private readonly ScheduleFieldVisualDelegate _scheduleFieldVisual;
    private readonly RootBossDelegate _rootBoss;
    private readonly ScheduleBeamDelegate _scheduleBeam;

    private readonly uint _countdownTicks;
    private readonly uint _emptyResetTicks;
    private readonly uint _victoryEjectTicks;
    private readonly uint _fusionGoodWindowTicks;
    private readonly uint _fusionPerfectWindowTicks;
    private readonly uint _soloShatterWindowTicks;
    private readonly uint _droneFirstSpawnTicks;
    private readonly uint _droneRespawnTicks;
    private readonly uint _fieldFirstDelayTicks;
    private readonly uint _fieldIntervalTicks;
    private readonly uint _fieldTelegraphTicks;
    private readonly uint _lashFirstDelayTicks;
    private readonly uint _lashIntervalTicks;
    private readonly uint _lashCueLeadTicks;
    private readonly uint _lashPulseSpacingTicks;
    private readonly uint _ringFirstDelayTicks;
    private readonly uint _ringIntervalTicks;
    private readonly uint _burstWindowTicks;
    private readonly uint _beamFirstDelayTicks;
    private readonly uint _beamIntervalTicks;
    private readonly uint _enragedBeamIntervalTicks;
    private readonly uint _beamWindupTicks;
    private readonly uint _pulseFirstDelayTicks;
    private readonly uint _pulseIntervalTicks;
    private readonly uint _pulseCueLeadTicks;
    private readonly uint _enrageTrickleIntervalTicks;

    // A participant: the entity + the tile it should be teleported back to on leave. Value struct, held in a list
    // (never more than 2 — issuer + partner — so a list is cheaper than a dictionary).
    private readonly record struct Participant(ulong EntityId, TileCoord ReturnTile);

    private readonly List<Participant> _participants = [];

    private EncounterState _state = EncounterState.Idle;
    private ulong _bossId;
    private bool _bossSpawned;

    private uint _countdownEndTick;
    private int _lastCountdownSecondAnnounced;

    private bool _emptyTimerArmed;
    private uint _emptyStartTick;

    private bool _victoryEjectArmed;
    private uint _victoryEjectDeadlineTick;

    // BOSS-2 (P1) plating/window state. `_participantsAtSpawn` fixes the duo/solo mode for the whole run (Law-2 solo
    // degradation reads it, not the live count). `_platingPermanentlyOff` latches once HP first crosses <=70% (the
    // plating never comes back this run). `_windowOpen`/`_windowEndTick` are the live vulnerability window (full
    // damage). `_platingTauntSaid` is the one-shot "your blows turn" chat latch. `_soloHitTicks` are the recent
    // skillshot-hit ticks on the boss for the solo 3-in-6 s shatter (pruned to the window each hit).
    private int _participantsAtSpawn;
    private bool _platingPermanentlyOff;
    private bool _windowOpen;
    private uint _windowEndTick;
    private bool _platingTauntSaid;
    private readonly List<uint> _soloHitTicks = [];

    // BOSS-2 (P1) interposer drone tracking (the encounter's ONE add). `_droneAlive` + `_droneId` track the live drone;
    // `_droneSpawnScheduled`/`_droneSpawnTick` schedule the first spawn (5 s post-boss) and each 6 s post-death respawn.
    private bool _droneAlive;
    private ulong _droneId;
    private bool _droneSpawnScheduled;
    private uint _droneSpawnTick;

    // BOSS-3 (P2 SUNDER) state. `_p2Active` latches ON at the 70% crumble, OFF at the 40% edge; `_p3Reached` latches
    // once HP first crosses <=40% (diagnostic — P2 never re-arms this run regardless). Field: `_nextFieldTick` schedules
    // the next FIRE; `_fieldPending`/`_fieldResolveTick` carry the armed field to its distance-resolve; `_fieldPendingIsRepel`
    // is the pending field's kind; `_fieldAlternator` toggles each fire (Repel↔Bind). Lash: `_nextLashTick` schedules the
    // next cue; `_lashPulsesRemaining`/`_lashNextPulseTick` drive the armed pulses. Ring: `_nextRingTick` schedules the
    // next ring. `_adds` is the GENERALIZED encounter-adds ledger (BOSS-2): EVERY live add id — the interposer drone (P1)
    // and the splinters (P2) — so ONE teardown loop clears them on every end path; anything in it that is NOT `_droneId`
    // is a splinter (the drone keeps its own respawn-cadence fields above).
    private bool _p2Active;
    private bool _p3Reached;
    private uint _nextFieldTick;
    private bool _fieldPending;
    private uint _fieldResolveTick;
    private bool _fieldPendingIsRepel;
    private bool _fieldAlternator;
    private uint _nextLashTick;
    private int _lashPulsesRemaining;
    private uint _lashNextPulseTick;
    private uint _nextRingTick;
    private readonly List<ulong> _adds = [];

    // BOSS-4 (P3 CORE) state. `_p3Active` latches ON at the 40% edge (armed by ArmP3, cleared on every end path +
    // re-init at spawn) — it drives the ward gate (ModifyIncomingDamage), the P3 pump, and IsBossRooted.
    // `_burstWindowOpen`/`_burstWindowEndTick` are the live ward-break window (full damage). `_beamBearing` is the
    // rotating sweep beam's current heading (advances a fixed step per beam); `_nextBeamCastTick` schedules the next
    // beam CAST. `_nextPulseCueTick` schedules the next knockback cue; `_pulsePending`/`_pulseShoveTick` carry the armed
    // shove to its resolve. `_enraged` latches once HP crosses <=10% (speeds the beam + starts the splinter trickle);
    // `_nextTrickleTick`/`_trickleAngle` drive the trickle spawn cadence + its spread.
    private bool _p3Active;
    private bool _burstWindowOpen;
    private uint _burstWindowEndTick;
    private double _beamBearing;
    private uint _nextBeamCastTick;
    private uint _nextPulseCueTick;
    private bool _pulsePending;
    private uint _pulseShoveTick;
    private bool _enraged;
    private uint _nextTrickleTick;
    private double _trickleAngle;

    public BossEncounterEngine(
        int tickRate,
        SpawnBossDelegate spawnBoss,
        DespawnBossDelegate despawnBoss,
        TryResolveDelegate tryResolve,
        TeleportPlayerDelegate teleport,
        NotifyDelegate notify,
        SpawnDroneDelegate spawnDrone,
        DespawnAddDelegate despawnAdd,
        BroadcastPlatingDelegate broadcastPlating,
        DamagePlayerDelegate damagePlayer,
        DisplacePlayerDelegate displacePlayer,
        EchoCueDelegate echoCue,
        SpawnSplinterDelegate spawnSplinter,
        ScheduleFieldVisualDelegate scheduleFieldVisual,
        RootBossDelegate rootBoss,
        ScheduleBeamDelegate scheduleBeam)
    {
        _tickRate = tickRate;
        _spawnBoss = spawnBoss ?? throw new ArgumentNullException(nameof(spawnBoss));
        _despawnBoss = despawnBoss ?? throw new ArgumentNullException(nameof(despawnBoss));
        _tryResolve = tryResolve ?? throw new ArgumentNullException(nameof(tryResolve));
        _teleport = teleport ?? throw new ArgumentNullException(nameof(teleport));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        _spawnDrone = spawnDrone ?? throw new ArgumentNullException(nameof(spawnDrone));
        _despawnAdd = despawnAdd ?? throw new ArgumentNullException(nameof(despawnAdd));
        _broadcastPlating = broadcastPlating ?? throw new ArgumentNullException(nameof(broadcastPlating));
        _damagePlayer = damagePlayer ?? throw new ArgumentNullException(nameof(damagePlayer));
        _displacePlayer = displacePlayer ?? throw new ArgumentNullException(nameof(displacePlayer));
        _echoCue = echoCue ?? throw new ArgumentNullException(nameof(echoCue));
        _spawnSplinter = spawnSplinter ?? throw new ArgumentNullException(nameof(spawnSplinter));
        _scheduleFieldVisual = scheduleFieldVisual ?? throw new ArgumentNullException(nameof(scheduleFieldVisual));
        _rootBoss = rootBoss ?? throw new ArgumentNullException(nameof(rootBoss));
        _scheduleBeam = scheduleBeam ?? throw new ArgumentNullException(nameof(scheduleBeam));
        _countdownTicks = SecondsToTicks(CountdownSeconds);
        _emptyResetTicks = SecondsToTicks(EmptyResetSeconds);
        _victoryEjectTicks = SecondsToTicks(VictoryEjectSeconds);
        _fusionGoodWindowTicks = SecondsToTicks(FusionGoodWindowSeconds);
        _fusionPerfectWindowTicks = SecondsToTicks(FusionPerfectWindowSeconds);
        _soloShatterWindowTicks = SecondsToTicks(SoloShatterWindowSeconds);
        _droneFirstSpawnTicks = SecondsToTicks(DroneFirstSpawnSeconds);
        _droneRespawnTicks = SecondsToTicks(DroneRespawnSeconds);
        _fieldFirstDelayTicks = SecondsToTicks(FieldFirstDelaySeconds);
        _fieldIntervalTicks = SecondsToTicks(FieldIntervalSeconds);
        _fieldTelegraphTicks = SecondsToTicks(FieldTelegraphSeconds);
        _lashFirstDelayTicks = SecondsToTicks(LashFirstDelaySeconds);
        _lashIntervalTicks = SecondsToTicks(LashIntervalSeconds);
        _lashCueLeadTicks = SecondsToTicks(LashCueLeadSeconds);
        _lashPulseSpacingTicks = SecondsToTicks(LashPulseSpacingSeconds);
        _ringFirstDelayTicks = SecondsToTicks(RingFirstDelaySeconds);
        _ringIntervalTicks = SecondsToTicks(RingIntervalSeconds);
        _burstWindowTicks = SecondsToTicks(BurstWindowSeconds);
        _beamFirstDelayTicks = SecondsToTicks(BeamFirstDelaySeconds);
        _beamIntervalTicks = SecondsToTicks(BeamIntervalSeconds);
        _enragedBeamIntervalTicks = SecondsToTicks(BeamIntervalSeconds * EnrageCadenceScale);
        _beamWindupTicks = SecondsToTicks(BeamWindupSeconds);
        _pulseFirstDelayTicks = SecondsToTicks(PulseFirstDelaySeconds);
        _pulseIntervalTicks = SecondsToTicks(PulseIntervalSeconds);
        _pulseCueLeadTicks = SecondsToTicks(PulseCueLeadSeconds);
        _enrageTrickleIntervalTicks = SecondsToTicks(EnrageTrickleIntervalSeconds);
    }

    // Test/diagnostic visibility (like BasicRoamerBehavior's TryGetPhase) — never drives replication.
    public EncounterState State => _state;
    public int ParticipantCount => _participants.Count;
    public ulong BossId => _bossId;
    public bool BossSpawned => _bossSpawned;

    // BOSS-2 (P1) test/diagnostic visibility. PlatingActive = the shell is currently up (damage reduced): Active, above
    // 70%, no window open. WindowOpen = a vulnerability window is live (full damage). PlatingPermanentlyOff = the
    // plating has crumbled for good (HP crossed <=70%). DroneAlive/DroneId track the live interposer add.
    public bool PlatingActive => _state == EncounterState.Active && _bossSpawned && !_platingPermanentlyOff && !_windowOpen;
    public bool WindowOpen => _windowOpen;
    public bool PlatingPermanentlyOff => _platingPermanentlyOff;
    public bool DroneAlive => _droneAlive;
    public ulong DroneId => _droneId;

    // BOSS-3 (P2) test/diagnostic visibility. P2Active = the SUNDER mechanics are live (armed at the 70% crumble, off
    // at 40%). P3Reached = HP has crossed <=40% (P2 disarmed for good this run). AddCount = live encounter adds (drone
    // in P1, splinters in P2).
    public bool P2Active => _p2Active;
    public bool P3Reached => _p3Reached;
    public int AddCount => _adds.Count;

    // BOSS-4 (P3) test/diagnostic visibility. P3Active = the CORE phase is live (armed at the 40% edge). WardUp = the
    // Core Ward is currently sealing (P3 live, no burst window) — the tick the ward zeroes all boss damage. BurstWindow-
    // Open = a ward-break window is live (full damage). Enraged = HP crossed <=10% (beam sped up + splinter trickle).
    public bool P3Active => _p3Active;
    public bool WardUp => _p3Active && !_burstWindowOpen;
    public bool BurstWindowOpen => _burstWindowOpen;
    public bool Enraged => _enraged;

    // Seconds → ticks, the canonical windup quantization (Math.Ceiling, floored at 1) GameServer uses everywhere.
    private uint SecondsToTicks(double seconds) => (uint)Math.Max(1, (int)Math.Ceiling(seconds * _tickRate));

    // /boss OUTSIDE the arena → begin the encounter. Stores the issuer's (and partner's) return tile, teleports them
    // to the entry tiles, and starts the countdown. `partner` is the issuer's duo partner entity when paired AND
    // online (GameServer resolves that), else null — the encounter works solo. Returns false + a denial message when
    // an encounter is already in progress (or the arena still holds post-victory stragglers). `message` is the chat
    // line for the ISSUER; the partner gets its own line here (no consent flow, per the design).
    public bool TryBegin(WorldEntity issuer, WorldEntity? partner, uint serverTick, out string message)
    {
        if (_state != EncounterState.Idle)
        {
            message = "The Sunderer is already engaged — wait for the arena to clear.";
            return false;
        }

        if (_participants.Count > 0)
        {
            // Idle but not clear: victors from the last kill are still standing in the arena. Refuse a fresh run
            // until they /boss out (their return tiles are retained for that) — never mix them into a new fight.
            message = "The arena is still occupied — wait for it to clear.";
            return false;
        }

        AddParticipant(issuer, BossArena.IssuerEntryTile);
        var hasPartner = partner is not null && partner.Id != issuer.Id;
        if (hasPartner)
        {
            AddParticipant(partner!, BossArena.PartnerEntryTile);
            _notify(partner!.Id, $"{issuer.DisplayName} pulled you into the Sunderer's arena!");
        }

        _state = EncounterState.Countdown;
        _countdownEndTick = serverTick + _countdownTicks;
        _lastCountdownSecondAnnounced = int.MaxValue; // so the first whole-second boundary announces.
        _emptyTimerArmed = false;

        message = hasPartner
            ? "You and your partner enter the Sunderer's arena. Steel yourselves."
            : "You enter the Sunderer's arena. Steel yourself.";
        return true;
    }

    // /boss INSIDE the arena → leave. Teleports the issuer back to its stored return tile and drops it from the
    // participant list. Returns false (with a message) if the issuer is not a tracked participant — GameServer then
    // falls back to ejecting them to a spawn point (a defensive path; normally unreachable — the arena is sealed).
    public bool TryLeave(WorldEntity issuer, out string message)
    {
        for (var i = 0; i < _participants.Count; i++)
        {
            if (_participants[i].EntityId == issuer.Id)
            {
                var returnTile = _participants[i].ReturnTile;
                _participants.RemoveAt(i);
                _teleport(issuer, returnTile);
                message = "You leave the Sunderer's arena.";
                return true;
            }
        }

        message = "You are not in the Sunderer's arena.";
        return false;
    }

    // The per-tick pump (GameServer calls it once per tick, right after TelegraphScheduler.ResolveDue so a boss
    // telegraph that just killed the last participant is seen as a wipe THIS tick, before RespawnPlayers moves the
    // bodies to town).
    public void Step(uint serverTick)
    {
        switch (_state)
        {
            case EncounterState.Idle:
                // Post-victory straggler cleanup: drop anyone who has since left the arena or disconnected, so a
                // fresh /boss (which requires an empty participant list) becomes possible once the victors clear.
                if (_participants.Count > 0)
                {
                    PruneDepartedParticipants();
                }

                if (_participants.Count == 0)
                {
                    _victoryEjectArmed = false;
                }
                else if (_victoryEjectArmed && serverTick >= _victoryEjectDeadlineTick)
                {
                    // Victory-eject deadline (review BOSS-1 MEDIUM): teleport every remaining victor back to its
                    // stored return tile — the same trip /boss-inside takes, just no longer optional — and clear the
                    // list so the shared arena frees up for the next pair.
                    foreach (var participant in _participants)
                    {
                        if (_tryResolve(participant.EntityId) is { } straggler)
                        {
                            _notify(participant.EntityId, "The arena's magic fades and returns you home.");
                            _teleport(straggler, participant.ReturnTile);
                        }
                    }

                    _participants.Clear();
                    _victoryEjectArmed = false;
                }

                break;

            case EncounterState.Countdown:
                StepCountdown(serverTick);
                break;

            case EncounterState.Active:
                StepActive(serverTick);
                break;
        }
    }

    private void StepCountdown(uint serverTick)
    {
        PruneDepartedParticipants();
        if (_participants.Count == 0)
        {
            // Everyone bailed before the boss spawned — cancel cleanly (no boss to despawn), back to Idle.
            Reset();
            return;
        }

        // Announce each whole second as the countdown ticks down (3.. 2.. 1..).
        var remainingTicks = _countdownEndTick > serverTick ? _countdownEndTick - serverTick : 0u;
        var remainingSeconds = (int)((remainingTicks + (uint)_tickRate - 1) / (uint)_tickRate); // ceil to seconds.
        if (remainingSeconds >= 1 && remainingSeconds < _lastCountdownSecondAnnounced)
        {
            _lastCountdownSecondAnnounced = remainingSeconds;
            AnnounceAll($"The Sunderer stirs... {remainingSeconds}");
        }

        if (serverTick >= _countdownEndTick)
        {
            SpawnBossNow(serverTick);
        }
    }

    private void SpawnBossNow(uint serverTick)
    {
        _participantsAtSpawn = _participants.Count;
        var health = _participantsAtSpawn >= 2 ? DuoBossHealth : SoloBossHealth;
        var boss = _spawnBoss(BossArena.BossSpawnTile, health);
        _bossId = boss.Id;
        _bossSpawned = true;
        _state = EncounterState.Active;
        _emptyTimerArmed = false;

        // BOSS-2 (P1): fresh plating state — the shell is UP (Laws 4/7: broadcast it LOUD), the first interposer drone
        // is scheduled 5 s out (anchored at THIS spawn tick), and any stale window/hit-count/taunt from a prior run is
        // cleared.
        _platingPermanentlyOff = false;
        _windowOpen = false;
        _windowEndTick = 0;
        _platingTauntSaid = false;
        _soloHitTicks.Clear();
        _droneAlive = false;
        _droneId = 0;
        _droneSpawnScheduled = true;
        _droneSpawnTick = serverTick + _droneFirstSpawnTicks;

        // BOSS-3 (P2): fresh P2 state — the SUNDER mechanics arm only at the 70% crumble, so they are latched OFF here.
        _p2Active = false;
        _p3Reached = false;
        _fieldPending = false;
        _lashPulsesRemaining = 0;
        _adds.Clear();

        // BOSS-4 (P3): fresh P3 state — the CORE phase arms only at the 40% edge, so it is latched OFF here.
        _p3Active = false;
        _burstWindowOpen = false;
        _pulsePending = false;
        _enraged = false;
        _beamBearing = 0d;
        _trickleAngle = 0d;

        _broadcastPlating(_bossId, true);
        AnnounceAll("THE SUNDERER awakens. Break its bond-hunger together!");
    }

    private void StepActive(uint serverTick)
    {
        // VICTORY first: the boss is dead. A player kill runs KillMonster (which despawns it) BEFORE this tick's
        // Step, so it resolves to null; the Health<=0-but-present case is the defensive one-tick window. Either way,
        // fanfare + ensure it is gone (idempotent). Participants are KEPT so the victors can /boss home (no auto-
        // eject); the encounter returns to Idle and a new run is refused until they clear (see TryBegin).
        var boss = _tryResolve(_bossId);
        if (_bossSpawned && (boss is null || boss.Stats.Health <= 0))
        {
            if (boss is not null)
            {
                _despawnBoss(_bossId);
            }

            // BOSS-2 (P1): tear down the encounter add (interposer drone) + clear the plating mechanics on victory
            // (the victory path does NOT call Reset — it retains the victors — so it must clean the add itself).
            TearDownEncounterMechanics();
            AnnounceAll("The Sunderer shatters! Victory. Leave with /boss.");
            _bossSpawned = false;
            _bossId = 0;
            _state = EncounterState.Idle;
            _emptyTimerArmed = false;
            // Bounded straggler window (see VictoryEjectSeconds): victors who don't /boss out are sent home when it
            // elapses, so the shared arena can never be held indefinitely.
            _victoryEjectArmed = true;
            _victoryEjectDeadlineTick = serverTick + _victoryEjectTicks;
            return;
        }

        PruneDepartedParticipants();

        if (_participants.Count == 0)
        {
            // EMPTY: everyone left / disconnected. Reset after the grace window (they may /boss back before then —
            // but a fresh /boss is a new encounter, so an empty arena just tidies up the abandoned boss).
            if (!_emptyTimerArmed)
            {
                _emptyTimerArmed = true;
                _emptyStartTick = serverTick;
            }
            else if (serverTick - _emptyStartTick >= _emptyResetTicks)
            {
                Reset();
            }

            return;
        }

        _emptyTimerArmed = false; // participants present → any pending empty-timer is void.

        // WIPE: participants remain but none are alive-in-arena (all are dead bodies awaiting the town respawn). A
        // full-party death resets the encounter immediately (boss despawn; adds cleared — none in BOSS-1). The dead
        // players respawn to town via the normal RespawnPlayers pass, so this need not teleport them.
        var aliveInArena = 0;
        foreach (var p in _participants)
        {
            var e = _tryResolve(p.EntityId);
            if (e is not null && e.Stats.Health > 0 && BossArena.ContainsInterior(e.TileCoord))
            {
                aliveInArena++;
            }
        }

        if (aliveInArena == 0)
        {
            AnnounceAll("The party has fallen. The Sunderer subsides.");
            Reset();
            return;
        }

        // BOSS-2 (P1): the encounter is genuinely still being fought (boss alive, at least one participant alive in the
        // arena). `boss` is non-null here — the victory branch returned on a dead/gone boss. Step the plating window +
        // permanent-off boundary + the interposer drone cadence.
        StepPlatingAndAdds(serverTick, boss!);
    }

    // Drop participants who are no longer present: disconnected (unresolvable) or alive-but-outside-the-arena (walked
    // out — TryLeave already teleported them — or respawned to town after dying). A DEAD body still inside the arena
    // is KEPT, so the wipe check can see "all participants dead" before RespawnPlayers moves it out.
    private void PruneDepartedParticipants()
    {
        for (var i = _participants.Count - 1; i >= 0; i--)
        {
            var e = _tryResolve(_participants[i].EntityId);
            if (e is null || (e.Stats.Health > 0 && !BossArena.ContainsInterior(e.TileCoord)))
            {
                _participants.RemoveAt(i);
            }
        }
    }

    // Store the entrant's CURRENT tile as its return position, THEN teleport it to the entry tile (order matters —
    // capture before the jump).
    private void AddParticipant(WorldEntity player, TileCoord entryTile)
    {
        _participants.Add(new Participant(player.Id, player.TileCoord));
        _teleport(player, entryTile);
    }

    // Reset to Idle: despawn the boss (if one is up) and clear all participants. The victory path deliberately does
    // NOT call this (it keeps the participants so victors can walk out); wipe / empty / countdown-cancel do.
    private void Reset()
    {
        // BOSS-2 (P1): clean the encounter add (interposer drone) + plating mechanics on EVERY reset path (wipe /
        // empty / countdown-cancel) — the "adds list is cleaned everywhere the boss is" invariant.
        TearDownEncounterMechanics();

        if (_bossSpawned)
        {
            _despawnBoss(_bossId);
        }

        _bossSpawned = false;
        _bossId = 0;
        _participants.Clear();
        _state = EncounterState.Idle;
        _emptyTimerArmed = false;
    }

    // ==== BOSS-2 (P1 HUSK): Sundered Plating + fusion shatter + interposer drone ====

    // The damage-taken MODIFIER GameServer applies at the monster-damage seam(s) for EVERY source (melee, skillshot,
    // tether, midpoint blast) — one uniform hook. Returns `rawAmount` unchanged unless `monsterId` is THIS run's boss
    // AND the plating shell is currently up (Active, above 70%, no vulnerability window open, not permanently
    // crumbled), in which case damage is reduced by DuoDamageReduction (duo) / SoloDamageReduction (solo — the mode is
    // fixed at spawn). During a shatter window (or below 70%, or off-encounter) damage passes through at full. Also
    // fires the one-shot "your blows turn" chat line on the first plated hit (Laws 4/7 legibility).
    public int ModifyIncomingDamage(ulong monsterId, int rawAmount)
    {
        if (rawAmount <= 0 || monsterId != _bossId || !_bossSpawned || _state != EncounterState.Active)
        {
            return rawAmount;
        }

        // BOSS-4 (P3 CORE WARD): below 40% the boss is rooted + sealed — ALL damage is reduced to ZERO unless a burst
        // window (a midpoint detonation broke the ward) is open, in which case it passes at FULL (no P1 reduction below
        // 40%). This gate takes precedence over the P1 plating branches (which are moot here — the plating crumbled at
        // 70%). The one-knob law: fusion does NOT reach here to break the ward; only OnMidpointBlast opens the window.
        if (_p3Active)
        {
            return _burstWindowOpen ? rawAmount : 0;
        }

        if (_platingPermanentlyOff || _windowOpen)
        {
            return rawAmount; // shattered or permanently crumbled → full damage.
        }

        if (!_platingTauntSaid)
        {
            _platingTauntSaid = true;
            AnnounceAll("The Sunderer's plating turns your blows!");
        }

        var reduction = _participantsAtSpawn >= 2 ? DuoDamageReduction : SoloDamageReduction;
        var reduced = (int)Math.Round(rawAmount * (1d - reduction), MidpointRounding.AwayFromZero);
        return Math.Max(0, reduced);
    }

    // SkillshotEngine reports a FUSION (its Good/Perfect merge classification) here. A fused skillshot of ANY tier
    // SHATTERS the plating (Law 3, receiver-forgives: the fusion event itself shatters — it need not hit the boss),
    // opening a full-damage window whose length is the tier (Law 8): Perfect = 9 s, Good = 6 s. Ignored unless the
    // plating is live (during countdown/idle, or below 70%, OnFusion is a no-op — see OpenWindow).
    public void OnFusion(ProjectileTier tier, uint serverTick)
    {
        var windowTicks = tier == ProjectileTier.Perfect ? _fusionPerfectWindowTicks : _fusionGoodWindowTicks;
        OpenWindow(serverTick, windowTicks);
    }

    // SkillshotEngine reports EVERY skillshot monster hit here (it doesn't know which monster is the boss); the engine
    // filters to its boss id. SOLO fallback (Law 2): SoloShatterHitCount skillshot hits on the boss within
    // SoloShatterWindowSeconds shatter it for the Good window. In DUO the gate is fusion, so solo hit-counting is
    // inert. No-op while a window is already open or the plating has crumbled.
    public void OnSkillshotMonsterHit(ulong monsterId, uint serverTick)
    {
        if (monsterId != _bossId || !_bossSpawned || _state != EncounterState.Active)
        {
            return;
        }

        if (_participantsAtSpawn >= 2 || _platingPermanentlyOff || _windowOpen)
        {
            return;
        }

        _soloHitTicks.Add(serverTick);
        // Prune hits older than the window (ticks are appended in ascending order, so drop from the front).
        while (_soloHitTicks.Count > 0 && serverTick - _soloHitTicks[0] > _soloShatterWindowTicks)
        {
            _soloHitTicks.RemoveAt(0);
        }

        if (_soloHitTicks.Count >= SoloShatterHitCount)
        {
            _soloHitTicks.Clear();
            OpenWindow(serverTick, _fusionGoodWindowTicks); // solo shatter grants the Good (6 s) window.
        }
    }

    // The interpose target the InterposerBehavior steers its drone toward each think-tick: DUO — the midpoint of the
    // two participants' segment (it body-blocks fusion crossings, the B1 contest); SOLO — the midpoint of the boss<->
    // player line (orchestrator ruling: still an interposer solo). Robust to a participant that died/left: falls back
    // to the remaining participant<->boss midpoint. False (drone idles) when there is nothing sensible to seek.
    public bool TryGetInterposeTarget(out WorldVector target)
    {
        target = default;
        if (_state != EncounterState.Active || !_bossSpawned)
        {
            return false;
        }

        WorldVector? first = null;
        WorldVector? second = null;
        foreach (var p in _participants)
        {
            if (_tryResolve(p.EntityId) is { Stats.Health: > 0 } e)
            {
                if (first is null)
                {
                    first = e.Position;
                }
                else
                {
                    second = e.Position;
                    break;
                }
            }
        }

        if (_participantsAtSpawn >= 2 && first is { } a && second is { } b)
        {
            target = (a + b) * 0.5d; // duo: midline of the pair's segment.
            return true;
        }

        // Solo, OR a duo down to one live participant: midpoint of the (only) participant and the boss.
        if (first is { } lone && _tryResolve(_bossId) is { } boss)
        {
            target = (lone + boss.Position) * 0.5d;
            return true;
        }

        return false;
    }

    // Per-tick plating + adds pump, called from StepActive while the fight is genuinely ongoing (boss alive, a
    // participant alive in-arena). Order: (1) permanent-off boundary at 70%; (2) window expiry (reform); (3) drone
    // spawn/respawn cadence. `boss` is the live boss entity (non-null).
    private void StepPlatingAndAdds(uint serverTick, WorldEntity boss)
    {
        // (1) Permanent-off boundary: the first tick the boss's HP is at/below 70% of max, the plating crumbles for
        // good this run (P2 mechanics arrive in BOSS-3; the boss just fights its baseline kit below 70%).
        if (!_platingPermanentlyOff && boss.Stats.MaxHealth > 0
            && boss.Stats.Health <= (int)Math.Round(boss.Stats.MaxHealth * PlatingHealthFraction, MidpointRounding.AwayFromZero))
        {
            CrumbleForGood(serverTick); // the P1→P2 edge: plating gone for good, P2 (SUNDER) mechanics arm here.
            return;
        }

        if (_platingPermanentlyOff)
        {
            // BOSS-3/BOSS-4: below 70% the P1 plating + drone are done. Pump the P2 SUNDER mechanics until the 40% edge,
            // then (armed at that edge) the P3 CORE mechanics — the two never run on the same tick (the edge disarms P2
            // and arms P3 in one call; _p3Active flips the pump the following tick).
            if (_p3Active)
            {
                StepP3(serverTick, boss);
            }
            else
            {
                StepP2(serverTick, boss);
            }

            return;
        }

        // (2) Window expiry → the plating REFORMS (Laws 4/7: broadcast + chat).
        if (_windowOpen && serverTick >= _windowEndTick)
        {
            _windowOpen = false;
            _broadcastPlating(_bossId, true);
            AnnounceAll("The plating reforms.");
        }

        // (3) Interposer drone cadence — only while the plating mechanics are live (guaranteed here: Active + above
        // 70%). A dead drone (unresolvable) arms a 6 s respawn; a due schedule spawns the next one.
        if (_droneAlive)
        {
            if (_tryResolve(_droneId) is null)
            {
                _adds.Remove(_droneId); // the add ledger drops the dead drone (killed by a player) before the respawn.
                _droneAlive = false;
                _droneId = 0;
                _droneSpawnScheduled = true;
                _droneSpawnTick = serverTick + _droneRespawnTicks;
            }
        }
        else if (_droneSpawnScheduled && serverTick >= _droneSpawnTick)
        {
            var drone = _spawnDrone(BossArena.BossSpawnTile);
            _droneId = drone.Id;
            _droneAlive = true;
            _droneSpawnScheduled = false;
            _adds.Add(_droneId); // track the drone in the generalized add ledger (torn down with the splinters).
        }
    }

    // Open (or extend) a vulnerability window of `windowTicks` full-damage. IGNORED unless the plating is live (Active,
    // spawned, above 70%) — a fusion during countdown/idle or below 70% is a no-op (the "ignored during countdown/
    // idle" contract). A fresh open broadcasts the shatter (plating off) + chats; a fusion landing while a window is
    // already open just extends the end tick (no duplicate chat/broadcast).
    private void OpenWindow(uint serverTick, uint windowTicks)
    {
        if (_state != EncounterState.Active || !_bossSpawned || _platingPermanentlyOff)
        {
            return;
        }

        var end = serverTick + windowTicks;
        if (_windowOpen)
        {
            _windowEndTick = Math.Max(_windowEndTick, end);
            return;
        }

        _windowOpen = true;
        _windowEndTick = end;
        _soloHitTicks.Clear(); // a shatter consumes the solo hit-count progress.
        _broadcastPlating(_bossId, false);
        AnnounceAll("The plating SHATTERS — strike now!");
    }

    // The plating crumbles for good at the 70% boundary (the P1→P2 edge): latch it off, drop any open window, stop +
    // tear down the interposer drone (the P1 add is done this run), broadcast + chat the permanent-off (Laws 4/7), and
    // ARM the P2 (SUNDER) mechanics anchored at this tick.
    private void CrumbleForGood(uint serverTick)
    {
        _platingPermanentlyOff = true;
        _windowOpen = false;
        _soloHitTicks.Clear();
        TearDownDrone();
        _broadcastPlating(_bossId, false);
        AnnounceAll("The plating crumbles for good!");
        ArmP2(serverTick);
    }

    // Despawn the live interposer drone (leak-free, idempotent), drop it from the add ledger, and clear its schedule —
    // used by CrumbleForGood (the P1 add ends at the crumble; no splinters exist yet, so this touches only the drone).
    private void TearDownDrone()
    {
        if (_droneAlive)
        {
            _despawnAdd(_droneId);
            _adds.Remove(_droneId);
        }

        _droneAlive = false;
        _droneId = 0;
        _droneSpawnScheduled = false;
    }

    // Full teardown on any encounter end (victory / wipe / empty / countdown-cancel): despawn EVERY live add (drone +
    // splinters) in one loop, and clear the plating window + P2 pending state so nothing lands after the fight ended.
    // The on/off latches are re-initialised fresh in SpawnBossNow, so this only clears what could leak a live entity or
    // a stale pending resolve across runs.
    private void TearDownEncounterMechanics()
    {
        DespawnAllAdds();
        _windowOpen = false;
        _soloHitTicks.Clear();
        _p2Active = false;
        _fieldPending = false;
        _lashPulsesRemaining = 0;
        // BOSS-4 (P3): clear the CORE phase so no beam/pulse/ward activity lands after the fight ended (victory / wipe /
        // empty / countdown-cancel). The scheduled beam telegraphs already in flight are the TelegraphScheduler's to
        // resolve — but with the boss gone they gather no boss and hit only whoever is still standing where they land;
        // the encounter stops SCHEDULING new ones here. The on/off latches re-init fresh in SpawnBossNow.
        _p3Active = false;
        _burstWindowOpen = false;
        _pulsePending = false;
        _enraged = false;
    }

    // BOSS-3: despawn EVERY live encounter add (the drone + all splinters) leak-free (idempotent), clear the ledger,
    // and reset the drone's cadence bookkeeping (its id lived in the ledger). Used by the full teardown + the 40% P2
    // disarm — the "adds cleaned everywhere the boss is" invariant, now covering the whole add ledger.
    private void DespawnAllAdds()
    {
        foreach (var id in _adds)
        {
            _despawnAdd(id);
        }

        _adds.Clear();
        _droneAlive = false;
        _droneId = 0;
        _droneSpawnScheduled = false;
    }

    // ==== BOSS-3 (P2 SUNDER): Repel/Bind fields + Echo Lash + splinter ring ====

    // Arm the P2 mechanics at the 70% crumble tick — every cadence is offset from THIS anchor (the staggered-by-
    // construction first-fire delays). The first duo field is REPEL (the home band 8–12u is already Repel-safe; the ask
    // then alternates to Bind and back).
    private void ArmP2(uint serverTick)
    {
        _p2Active = true;
        _fieldPending = false;
        _fieldAlternator = true; // first field Repel.
        _lashPulsesRemaining = 0;
        _nextFieldTick = serverTick + _fieldFirstDelayTicks;
        _nextLashTick = serverTick + _lashFirstDelayTicks;
        _nextRingTick = serverTick + _ringFirstDelayTicks;
        AnnounceAll("The Sunderer sunders your bond — mind the distance!");
    }

    // The per-tick P2 pump (called from StepPlatingAndAdds once the plating has crumbled). Order is fixed for
    // determinism (field resolve → lash → ring → splinter pops); the three damage streams occupy disjoint tick
    // residue classes so no two ever resolve on the same tick anyway.
    private void StepP2(uint serverTick, WorldEntity boss)
    {
        // 40% edge → DISARM P2 (adds cleared + P3 teaser). P3 itself is BOSS-4; below 40% the boss fights its baseline
        // kit. Latched (_p2Active off) so the mechanics never re-arm this run.
        if (_p2Active && boss.Stats.MaxHealth > 0
            && boss.Stats.Health <= (int)Math.Round(boss.Stats.MaxHealth * P3HealthFraction, MidpointRounding.AwayFromZero))
        {
            DisarmP2(serverTick, boss);
            return;
        }

        if (!_p2Active)
        {
            return;
        }

        StepFields(serverTick);
        StepEchoLash(serverTick);
        StepSplinterRing(serverTick, boss);
        StepSplinterPops(serverTick);
    }

    // The 40% edge (the P2→P3 transition): stop the P2 mechanics for good, clear every splinter (the ring dies at 40%
    // too), cancel any pending field/lash so nothing lands after, chat the transition, then ARM P3 (root + ward + the
    // beam/pulse cadences anchored at this tick). Does NOT reset the encounter — the boss lives on into the Core phase.
    private void DisarmP2(uint serverTick, WorldEntity boss)
    {
        _p2Active = false;
        _p3Reached = true;
        _fieldPending = false;
        _lashPulsesRemaining = 0;
        DespawnAllAdds(); // splinters die at 40% (the drone is already long gone from the crumble).
        AnnounceAll("The Sunderer draws its shattered mass inward...");
        ArmP3(serverTick, boss);
    }

    // ==== BOSS-4 (P3 CORE): root + Core Ward + rotating sweep beam + knockback pulses + soft enrage ====

    // Arm the P3 mechanics at the 40% edge (anchor T0). ROOT the boss at centre (teleport once + cancel any in-flight
    // action + zero velocity — the ongoing chase suppression is GameServer's IsBossRooted gate), raise the CORE WARD
    // (legibility rides BossPlatingMessage per the orchestrator ruling: ward up = plating-message TRUE = steel tint),
    // and schedule the first beam / pulse off THIS anchor (the staggered-by-construction first-fire delays).
    private void ArmP3(uint serverTick, WorldEntity boss)
    {
        _p3Active = true;
        _burstWindowOpen = false;
        _pulsePending = false;
        _enraged = false;
        _beamBearing = 0d;
        _trickleAngle = 0d;
        _nextBeamCastTick = serverTick + _beamFirstDelayTicks;
        _nextPulseCueTick = serverTick + _pulseFirstDelayTicks;
        _rootBoss(boss, BossArena.BossSpawnTile);
        _broadcastPlating(_bossId, true); // ward SEALS → steel tint (ward up = plating-message true).
        AnnounceAll("The Sunderer roots — its core seals!");
    }

    // The per-tick P3 pump (called from StepPlatingAndAdds once P3 is armed). Order: ward-window expiry → enrage edge →
    // beam → knockback pulse → splinter trickle + pops. The beam RESOLVE and the pulse SHOVE occupy disjoint tick
    // residues by construction (see the stagger note), so no two ever land on the same tick at the base cadence.
    private void StepP3(uint serverTick, WorldEntity boss)
    {
        // (1) Burst-window expiry → the ward REFORMS (broadcast steel tint back on + chat). The victory branch fires
        // FIRST in StepActive, so a boss killed during the window never reaches here.
        if (_burstWindowOpen && serverTick >= _burstWindowEndTick)
        {
            _burstWindowOpen = false;
            _broadcastPlating(_bossId, true);
            AnnounceAll("The core reseals — break it open again!");
        }

        // (2) SOFT ENRAGE edge: the first tick HP is at/below 10%, speed the beam + start the splinter trickle + chat.
        if (!_enraged && boss.Stats.MaxHealth > 0
            && boss.Stats.Health <= (int)Math.Round(boss.Stats.MaxHealth * EnrageHealthFraction, MidpointRounding.AwayFromZero))
        {
            _enraged = true;
            _nextTrickleTick = serverTick + _enrageTrickleIntervalTicks;
            AnnounceAll("The Sunderer rages!");
        }

        StepBeam(serverTick, boss);
        StepKnockbackPulse(serverTick, boss);
        if (_enraged)
        {
            StepSplinterTrickle(serverTick, boss);
        }

        // Trickle splinters POP like the P2 ring (within 1u of their nearest participant → damage + despawn); the drone
        // id is 0 in P3 so StepSplinterPops treats every ledger entry as a splinter. A no-op when the ledger is empty.
        StepSplinterPops(serverTick);
    }

    // ROTATING SWEEP BEAM: on cadence, schedule a LINE telegraph from the boss's CURRENT position at the current bearing
    // (real damage through the scheduler's gate), then advance the bearing a fixed step (consistent rotation) and arm
    // the next cast — the interval speeds up under enrage. The line resolves BeamWindup later at positions AT that tick
    // (dodgeable — walk with the rotation).
    private void StepBeam(uint serverTick, WorldEntity boss)
    {
        if (serverTick < _nextBeamCastTick)
        {
            return;
        }

        _scheduleBeam(boss.Position, BeamLengthUnits, _beamBearing, BeamHalfWidthUnits, BeamDamage, serverTick, serverTick + _beamWindupTicks);
        _beamBearing = WrapTwoPi(_beamBearing + BeamBearingAdvanceRadians);
        _nextBeamCastTick = serverTick + (_enraged ? _enragedBeamIntervalTicks : _beamIntervalTicks);
    }

    // KNOCKBACK PULSES: on cadence, announce the brace cue + arm the shove; PulseCueLead later, shove EVERY living
    // participant PulseShoveUnits radially away from the boss (wall-swept, no damage — the S3 midpoint contest). The
    // interval is FIXED (the enrage scales the beam, not the shove), so the shove residue never shifts.
    private void StepKnockbackPulse(uint serverTick, WorldEntity boss)
    {
        if (!_pulsePending && serverTick >= _nextPulseCueTick)
        {
            AnnounceAll("The Sunderer heaves — brace, the floor throws you outward!");
            _pulsePending = true;
            _pulseShoveTick = serverTick + _pulseCueLeadTicks;
            _nextPulseCueTick = serverTick + _pulseIntervalTicks;
        }

        if (_pulsePending && serverTick >= _pulseShoveTick)
        {
            foreach (var e in GetLivingParticipants())
            {
                var away = DirectionApart(e.Position, boss.Position);
                _displacePlayer(e, e.Position + away * PulseShoveUnits);
            }

            _pulsePending = false;
        }
    }

    // SOFT-ENRAGE splinter trickle: one splinter every EnrageTrickleInterval, spawned on the P2 ring radius at a rotating
    // angle (spread), tracked in the shared add ledger + creeping toward the nearest participant (SplinterBehavior) and
    // popping via StepSplinterPops — the "straggler splinters that trickle in below 10%" the tether clears.
    private void StepSplinterTrickle(uint serverTick, WorldEntity boss)
    {
        if (serverTick < _nextTrickleTick)
        {
            return;
        }

        var point = boss.Position + new WorldVector(Math.Cos(_trickleAngle), Math.Sin(_trickleAngle)) * SplinterRingRadiusUnits;
        var splinter = _spawnSplinter(ClampToArenaInterior(point));
        _adds.Add(splinter.Id);
        _trickleAngle = WrapTwoPi(_trickleAngle + BeamBearingAdvanceRadians); // reuse the 40° step for an even spread.
        _nextTrickleTick = serverTick + _enrageTrickleIntervalTicks;
    }

    // BOSS-4 (P3 Ward break): MidpointDetonationEngine reports EVERY resolved blast here (center + tick). A blast whose
    // center lands within WardBreakRadius of the boss BREAKS the ward → an 8 s burst window (full damage). IGNORED
    // unless P3 is live (a blast during P1/P2/idle is a no-op — the fusion-ignored precedent) or while a window is
    // already open (no re-open/extend — one detonation, one window). The radius is the fixed duo/solo mode.
    public void OnMidpointBlast(WorldVector center, uint serverTick)
    {
        if (_state != EncounterState.Active || !_bossSpawned || !_p3Active || _burstWindowOpen)
        {
            return;
        }

        if (_tryResolve(_bossId) is not { } boss)
        {
            return;
        }

        var radius = _participantsAtSpawn >= 2 ? WardBreakRadiusDuoUnits : WardBreakRadiusSoloUnits;
        if ((center - boss.Position).Length > radius)
        {
            return;
        }

        _burstWindowOpen = true;
        _burstWindowEndTick = serverTick + _burstWindowTicks;
        _broadcastPlating(_bossId, false); // ward BROKEN → tint off (burst = plating-message false).
        AnnounceAll("The core is EXPOSED — burn it!");
    }

    // BOSS-4 (P3): whether `monsterId` is THIS run's boss AND it is currently rooted (P3 live). GameServer's StepMonsterAi
    // queries this to SKIP the boss's chase brain + zero its velocity each tick, so the boss holds at the centre it was
    // teleported to at the P3 edge (its melee kit is dormant — the beam/pulse are the P3 contest). False otherwise, so a
    // non-boss monster / a pre-P3 boss steps normally.
    public bool IsBossRooted(ulong monsterId) =>
        _p3Active && _bossSpawned && _state == EncounterState.Active && monsterId == _bossId;

    // Reduce an angle to [0, 2π) so the rotating bearing never grows unbounded across a long fight.
    private static double WrapTwoPi(double radians)
    {
        var twoPi = 2d * Math.PI;
        radians %= twoPi;
        return radians < 0d ? radians + twoPi : radians;
    }

    // Repel/Bind fields: resolve a pending field at its telegraph deadline (distance-judged), then fire the next when
    // its cadence arrives. A field is never pending and re-firing at once (cadence 9s ≫ telegraph 1.2s).
    private void StepFields(uint serverTick)
    {
        if (_fieldPending && serverTick >= _fieldResolveTick)
        {
            ResolveField(serverTick);
            _fieldPending = false;
        }

        if (!_fieldPending && serverTick >= _nextFieldTick)
        {
            FireField(serverTick);
            _nextFieldTick = serverTick + _fieldIntervalTicks;
        }
    }

    // Announce the ask + schedule the cosmetic ring decal around each living participant, and arm the pending field
    // (its kind captured now; the alternator toggles for the next fire). The RESOLVE (distance test) lands
    // FieldTelegraphSeconds later in ResolveField.
    private void FireField(uint serverTick)
    {
        _fieldPendingIsRepel = _fieldAlternator;
        _fieldAlternator = !_fieldAlternator;
        _fieldResolveTick = serverTick + _fieldTelegraphTicks;
        _fieldPending = true;

        var living = GetLivingParticipants();
        foreach (var e in living)
        {
            _scheduleFieldVisual(e.Position, FieldTelegraphRingRadiusUnits, serverTick, _fieldResolveTick);
        }

        if (living.Count >= 2)
        {
            AnnounceAll(_fieldPendingIsRepel
                ? "The Sunderer REPELS — break apart!"
                : "The Sunderer BINDS — come together!");
        }
        else
        {
            AnnounceAll("The Sunderer's field charges — move clear of it!");
        }
    }

    // Resolve the pending field on PAIR DISTANCE (not shape membership). Two living participants → the duo Repel/Bind
    // ask; one → the solo move-out ring (REPEL-only vs the boss, the Law-2 degradation — a duo down to one live member
    // resolves as solo too); zero → skip (a wipe is imminent). Damage ALWAYS routes through the gate.
    private void ResolveField(uint serverTick)
    {
        var living = GetLivingParticipants();
        if (living.Count >= 2)
        {
            var a = living[0];
            var b = living[1];
            var separation = (a.Position - b.Position).Length;
            if (_fieldPendingIsRepel)
            {
                if (separation <= FieldRepelTriggerRangeUnits)
                {
                    _damagePlayer(a, FieldDamage, serverTick, "Repel field");
                    _damagePlayer(b, FieldDamage, serverTick, "Repel field");
                    var apart = DirectionApart(a.Position, b.Position);
                    _displacePlayer(a, a.Position + apart * FieldRepelKnockbackUnits);
                    _displacePlayer(b, b.Position - apart * FieldRepelKnockbackUnits);
                }
            }
            else if (separation > FieldBindTriggerRangeUnits)
            {
                _damagePlayer(a, FieldDamage, serverTick, "Bind field");
                _damagePlayer(b, FieldDamage, serverTick, "Bind field");
            }

            return;
        }

        if (living.Count == 1 && _tryResolve(_bossId) is { } boss)
        {
            var solo = living[0];
            if ((solo.Position - boss.Position).Length <= FieldRepelTriggerRangeUnits)
            {
                _damagePlayer(solo, FieldDamage, serverTick, "Sunder field");
                var away = DirectionApart(solo.Position, boss.Position);
                _displacePlayer(solo, solo.Position + away * FieldRepelKnockbackUnits);
            }
        }
    }

    // Echo Lash: fire the cue + arm the pulses when the cadence arrives, then deliver each armed pulse to every LIVING
    // participant through the gate at its spaced tick. The cue plays for ALL tracked participants (harmless on a downed
    // one); only the living take pulse damage.
    private void StepEchoLash(uint serverTick)
    {
        if (_lashPulsesRemaining == 0 && serverTick >= _nextLashTick)
        {
            foreach (var p in _participants)
            {
                _echoCue(p.EntityId);
            }

            AnnounceAll("The Sunderer inhales — brace as one!");
            _lashPulsesRemaining = _participantsAtSpawn >= 2 ? LashPulseCountDuo : LashPulseCountSolo;
            _lashNextPulseTick = serverTick + _lashCueLeadTicks;
            _nextLashTick = serverTick + _lashIntervalTicks;
        }

        if (_lashPulsesRemaining > 0 && serverTick >= _lashNextPulseTick)
        {
            foreach (var e in GetLivingParticipants())
            {
                _damagePlayer(e, LashPulseDamage, serverTick, "Echo Lash");
            }

            _lashPulsesRemaining--;
            if (_lashPulsesRemaining > 0)
            {
                _lashNextPulseTick = serverTick + _lashPulseSpacingTicks;
            }
        }
    }

    // Splinter ring: on cadence, spawn N splinters evenly on a radius-7 ring around the boss (6 duo / 3 solo) and track
    // them in the add ledger. They creep toward the nearest participant (SplinterBehavior) and pop in StepSplinterPops.
    private void StepSplinterRing(uint serverTick, WorldEntity boss)
    {
        if (serverTick < _nextRingTick)
        {
            return;
        }

        var count = _participantsAtSpawn >= 2 ? SplinterCountDuo : SplinterCountSolo;
        for (var i = 0; i < count; i++)
        {
            var angle = (2d * Math.PI * i) / count;
            var point = boss.Position + new WorldVector(Math.Cos(angle), Math.Sin(angle)) * SplinterRingRadiusUnits;
            // CLAMP into the arena interior: the boss CHASES around the 22×22 room, so a ring fired near a wall would
            // otherwise place a point outside the interior — and the real spawn seam (Zone.SpawnTransient) THROWS on a
            // non-walkable tile. Every interior tile is walkable floor, so clamping keeps the spawn safe (a ring near a
            // wall piles onto the wall-side edge — acceptable; the splinters still creep out).
            var splinter = _spawnSplinter(ClampToArenaInterior(point));
            _adds.Add(splinter.Id);
        }

        AnnounceAll("Splinters erupt from the Sunderer — sweep them clear!");
        _nextRingTick = serverTick + _ringIntervalTicks;
    }

    // Splinter pops: a splinter within SplinterPopRangeUnits of its nearest living participant POPS — damage that
    // participant through the gate + despawn the splinter. A splinter already gone (killed by the tether/skillshot)
    // just drops from the ledger. Iterated backwards for safe in-loop removal; the drone id (if any lingered) is skipped
    // — only splinters pop.
    private void StepSplinterPops(uint serverTick)
    {
        for (var i = _adds.Count - 1; i >= 0; i--)
        {
            var id = _adds[i];
            if (id == _droneId)
            {
                continue; // never a splinter.
            }

            var splinter = _tryResolve(id);
            if (splinter is null)
            {
                _adds.RemoveAt(i); // killed — the tether/skillshot cleared it.
                continue;
            }

            var nearest = NearestLivingParticipant(splinter.Position, out var distance);
            if (nearest is not null && distance <= SplinterPopRangeUnits)
            {
                _damagePlayer(nearest, SplinterPopDamage, serverTick, "Splinter");
                _despawnAdd(id);
                _adds.RemoveAt(i);
            }
        }
    }

    // BOSS-3 (P2): the target the SplinterBehavior steers each splinter toward — its nearest LIVING participant's
    // position. False (the splinter holds) when the encounter is not active or no participant is alive.
    public bool TryGetSplinterTarget(WorldEntity splinter, out WorldVector target)
    {
        target = default;
        if (_state != EncounterState.Active || !_bossSpawned)
        {
            return false;
        }

        var nearest = NearestLivingParticipant(splinter.Position, out _);
        if (nearest is null)
        {
            return false;
        }

        target = nearest.Position;
        return true;
    }

    // The living (Health > 0) participant entities, resolved fresh. Small (<=2) — allocated on the rare field/lash
    // ticks only; the per-tick splinter pop pass uses the alloc-free NearestLivingParticipant instead.
    private List<WorldEntity> GetLivingParticipants()
    {
        var living = new List<WorldEntity>(_participants.Count);
        foreach (var p in _participants)
        {
            if (_tryResolve(p.EntityId) is { Stats.Health: > 0 } e)
            {
                living.Add(e);
            }
        }

        return living;
    }

    // The nearest living participant to `from`, and its distance (double.MaxValue when none). Alloc-free (iterates the
    // <=2 participant records) — the per-splinter, per-tick primitive.
    private WorldEntity? NearestLivingParticipant(WorldVector from, out double distance)
    {
        WorldEntity? best = null;
        distance = double.MaxValue;
        foreach (var p in _participants)
        {
            if (_tryResolve(p.EntityId) is { Stats.Health: > 0 } e)
            {
                var d = (e.Position - from).Length;
                if (d < distance)
                {
                    distance = d;
                    best = e;
                }
            }
        }

        return best;
    }

    // Clamp a continuous point's rounded tile into the arena interior (every interior tile is walkable floor), so a
    // splinter ring fired while the boss is near a wall never asks the spawn seam for a non-walkable tile.
    private static TileCoord ClampToArenaInterior(WorldVector point)
    {
        var tile = point.ToTileRounded();
        return new TileCoord(
            Math.Clamp(tile.X, BossArena.InteriorMinX, BossArena.InteriorMaxX),
            Math.Clamp(tile.Y, BossArena.InteriorMinY, BossArena.InteriorMaxY));
    }

    // The unit heading from `other` to `point` (the direction `point` is pushed AWAY from `other`); an arbitrary axis
    // (+x) for coincident positions so a knockback never produces a zero shove.
    private static WorldVector DirectionApart(WorldVector point, WorldVector other)
    {
        var dir = (point - other).Normalized();
        return dir.LengthSquared > 0d ? dir : new WorldVector(1d, 0d);
    }

    private void AnnounceAll(string text)
    {
        foreach (var p in _participants)
        {
            _notify(p.EntityId, text);
        }
    }
}
