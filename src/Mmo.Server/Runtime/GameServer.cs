using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using LiteNetLib;
using Mmo.Server.Configuration;
using Mmo.Server.Data;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Actions;
using Mmo.Shared.Domain.Population;
using Mmo.Shared.Protocol;

namespace Mmo.Server.Runtime;

public sealed class GameServer
{
    private const string ServerName = "mmo-learning-server";
    private const int MaxSequencedSnapshotBytes = 1000;
    private const int ProtocolHeaderBytes = 7;
    // serverTick(4) + snapshotSeq(4) + recipientStepSeq(4) + lastInputSeq(4, v36) + totalEntities(2) + isComplete(1)
    // + chunkIndex(2) + chunkCount(2). The +4 over v35 is the new continuous-migration LastInputSeq header field.
    private const int SnapshotHeaderBytes = 21;
    // Per-entity snapshot state wire size: networkId(2) + qx(2) + qy(2) + facing(1) + depleted(1) = 8, plus
    // COMBAT-S2A's public HP Health(2) + MaxHealth(2) = 12. CONTINUOUS MIGRATION (v36): qx/qy are the fixed-point
    // Q12.4 continuous position (two shorts) — same 4 bytes as the v35 tile shorts, so the per-entity size is unchanged.
    // MOVEMENT-ACTIONS Phase B1 (v38): + the airborne VerticalOffset — a flag byte (1, grounded) plus an optional
    // Q12.4 ushort (2, airborne).
    // REMOTE-WALK Phase 1 (v39): the airborne flag byte is now a COMBINED flags byte that can also gate an optional
    // Velocity — velX,velY signed shorts (4, moving). This is a CHUNK-BUDGET estimate (packets must not overflow), so
    // use the WORST case flags(1) + height(2) + velocity(4) = 7: 12 + 7 = 19. (A resting grounded entity is really 13;
    // over-estimating only chunks a hair earlier, never overflows.)
    private const int EntityStateFixedBytes = 19;
    private const int MaxBadPacketsBeforeDisconnect = 5;
    private const int DefaultStressClientCount = 120;
    private static readonly TimeSpan DefaultStressDuration = TimeSpan.FromSeconds(60);
    private const float InterestExitHysteresisTiles = 1f;
    private const float SnapshotRetentionBonusDistanceSquared = 144f;

    // Effective per-entity step cooldown is clamped to this ms range — the same [50, 5000] ms bounds the
    // global StepCooldownMs is validated against — so an arbitrary SpeedMultiplier (e.g. /speed 0.001 or
    // /speed 10000) can never produce a degenerate cadence that stalls or floods the tick loop. (S51)
    private const int MinEffectiveStepCooldownMs = 50;
    private const int MaxEffectiveStepCooldownMs = 5000;

    // Authored-tick clamp window for the attack-movement-ROOT (ApplyAttackMovementRootAuthored). The swing root is
    // anchored on the CLIENT's AUTHORED tick (carried on the wire) so the server and the client predictor compute the
    // identical root window under latency, but the authored tick is clamped to [serverTick - AuthoredTickPastWindow,
    // serverTick + AuthoredTickFutureLead] first so a far-past/far-future (tamper / clock skew) authored tick can't
    // poison the schedule. (Phase 1: formerly also bounded the NET3 authored-tick STEP COMMIT, which is now retired —
    // these constants remain solely for the swing-root anchor.)
    private const uint AuthoredTickPastWindow = 64;
    private const uint AuthoredTickFutureLead = 4;

    // COMBAT-S2B / FREEAIM / COMBAT-TUNING: the free-aim attack feel-knobs (per-entity attack cooldown, swing-root
    // duration, sector half-angle + radius, damage per hit) are no longer hard constants here — they now live on the
    // mutable ServerTuning holder, are LIVE-tunable via the combat.* AdminSetTuning keys, and are REPLICATED to each
    // client (CombatTuningSnapshot) so the client's wedge/predictor/cooldown-viz match the server's resolution
    // instead of duplicating their own constants. HandleAttack + FreeAimSectorResolver read _tuning each attack.

    // Keepalive safety timeout for held movement intents (~1 s). The client resends its current intent
    // every ~500 ms; if a "moving" session goes silent for longer than this (a wedged-but-connected
    // client), the tick loop clears its intent so it stops walking. A real disconnect already despawns
    // the entity, so this only guards the wedged-client edge case. See docs/movement-input-model.md.
    private static readonly TimeSpan MoveIntentKeepaliveTimeout = TimeSpan.FromSeconds(1);

    // CONTINUOUS MIGRATION (Phase 3, v36): anti-speedhack clamps for the per-input continuous move. The client now
    // sends DtSeconds (how much sim-time a frame represents), which the server integrates by — so it must be bounded
    // two ways:
    //   * MaxMoveInputDtSeconds: a per-input SANITY clamp. One frame's dt is tiny (~1/60s); 0.25s caps a single
    //     input to ~5 server ticks of motion so a lone huge-dt packet can't teleport (and a legitimately laggy
    //     frame still integrates fully).
    //   * MoveDtBurstAllowanceSeconds: the per-peer wall-clock dt BUDGET ceiling. The budget accrues REAL elapsed
    //     time each tick (capped at this) and each integrated input debits it; an input may consume only the
    //     remaining budget. So the TOTAL integrated sim-time over any window cannot exceed real elapsed time + this
    //     allowance — a flood of max-dt inputs advances ≈ real-time distance, never faster. The allowance (~0.4s)
    //     absorbs network/frame jitter (a burst of buffered inputs after a hitch) without ever permitting a
    //     sustained over-rate.
    // CONTINUOUS MIGRATION (Phase 4): the per-input dt SANITY clamp is now the SHARED Mmo.Shared.Domain.ContinuousMovement
    // .MaxInputDtSeconds — the SAME constant the client predictor clamps its predicted (and buffered) dt to AND the send
    // path clamps the frame dt to, so buffered == sent == server-integrated dt under normal play (the dt-alignment
    // linchpin; replay reproduces the server path with no correction).
    private const double MaxMoveInputDtSeconds = ContinuousMovement.MaxInputDtSeconds;
    private const double MoveDtBurstAllowanceSeconds = 0.4d;

    private readonly ServerOptions _options;
    // S60 live-tuning holder, seeded from _options. The game loop reads the tunable params (step cooldown,
    // interest radius) through THIS, not _options, so an admin AdminSetTuning can change them mid-run.
    private readonly ServerTuning _tuning;
    private readonly ICharacterRepository _characters;
    // ECOLOGY E3 (docs/ecology-v1-design.md D8, S8 E3): the region_populations persistence seam. Defaults to
    // NullEcologyRepository (see GameServer's ctor) when the caller doesn't supply one — every existing test
    // suite constructs GameServer with just an ICharacterRepository, and this keeps them compiling/passing
    // unchanged instead of forcing an ecology stub onto each one.
    private readonly IEcologyRepository _ecologyRepository;
    private readonly ItemRegistry _itemRegistry = ItemRegistry.Default;
    private readonly ResourceNodeRegistry _resourceNodes;
    // NODE-FIELD N1/N2 (docs/node-field-design.md): the shared deterministic catalogue (built once from the
    // SAME (seed, authored map) the client independently derives) and its per-index mutable state. See the
    // ctor for the build (right after _zone) and NodeField's own doc for the respawn-sweep design.
    private readonly NodeCatalog _nodeCatalog;
    private readonly NodeField _nodeField;
    private readonly PersistenceWriteBehindWorker _persistence;
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _netManager;
    private readonly Dictionary<NetPeer, ClientSession> _sessions = new();
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();
    private readonly SyntheticClientLoad _syntheticLoad = new();
    private readonly ServerMetrics _metrics = new();
    private readonly ServerRuntimeGuard _runtimeGuard;
    private readonly ServerMovementTrace _movementTrace;
    private readonly ServerCadenceTrace _cadenceTrace = ServerCadenceTrace.FromEnvironment();
    private readonly NetworkIdPool _networkIds = new();
    private readonly Zone _zone;

    // AUTHORED-MAP M3: test seam — lets the boot-wiring tests (authored prop spawning, spawn-anchor
    // login) inspect the constructed world without a network round-trip. Never used by product code.
    internal Zone ZoneForTests => _zone;

    // NODE-FIELD N2: test seam — lets tests inspect the live per-index depleted/respawn state (and, via
    // NodeCatalog, the tile/type each index resolved to) without a network round-trip. Never used by
    // product code.
    internal NodeField NodeFieldForTests => _nodeField;

    // TELEGRAPH T2 REVIEW FOLLOWUP (item 4b, the forget-on-resolve pin): test seams — let a HEADLESS test (no
    // RunAsync, so no live tick thread — same single-threaded "boot wiring" pattern as ZoneForTests) drive the
    // REAL per-recipient telegraph AOI diff (SyncTelegraphs) and the real scheduler directly, so the assertion
    // exercises production code, not a re-implementation of the forget rule in a test-local lambda. Never used by
    // product code.
    internal TelegraphScheduler TelegraphsForTests => _telegraphs;

    internal void SyncTelegraphsForTests(ClientSession recipient, WorldEntity recipientEntity) =>
        SyncTelegraphs(recipient, recipientEntity);

    // ECOLOGY E2 test seams — same "no live tick thread" boot-wiring discipline as ZoneForTests/TelegraphsForTests:
    // construct a GameServer, never call RunAsync, and drive the REAL production methods directly + tick-count-
    // agnostic (serverTick is a parameter, not the live _serverTick field), so materialization pacing/skip-near-
    // player/overgrown-modifier/kill-hook/no-leak behavior is testable in a plain deterministic loop with no real-
    // time wait and no data race against a live tick thread. Never used by product code.
    internal EcologyState EcologyForTests => _ecology;
    internal IReadOnlyList<RegionSpawner> RegionSpawnersForTests => _regionSpawners;
    internal void MaterializeRegionSpawnersForTests(uint serverTick) => MaterializeRegionSpawners(serverTick);
    internal void KillMonsterForTests(WorldEntity monster) => KillMonster(monster);
    internal int ClearRegionSpawnerMonstersForTests() => ClearRegionSpawnerMonsters();

    // ECOLOGY E3 test seam: drives the SAME save path the checkpoint cadence/graceful shutdown call, without
    // needing a live tick thread — lets a persistence test save deterministically after driving EcologyForTests
    // directly (EcologyTick/RecordKill/TrySetStock), then boot a SECOND GameServer against the same repository to
    // assert the restart-survival acceptance (§5.3).
    internal Task SaveEcologyPopulationsForTests() => SaveEcologyPopulationsAsync(CancellationToken.None);

    // The raw reverse-map size — the direct "no leak" pin (distinct from RegionSpawner.LiveCount, which is a
    // PER-SPAWNER set that a bug in the reverse map alone wouldn't show up in).
    internal int RegionSpawnerOfMonsterCountForTests => _regionSpawnerOfMonster.Count;

    private readonly List<ClientSession> _authenticatedScratch = [];
    private readonly List<WorldEntity> _entityScratch = [];
    private readonly List<WorldEntity> _aoiCandidateScratch = [];
    private readonly List<WorldEntity> _aoiInteractCandidateScratch = [];
    // COMBAT-S2B: reused candidate buffer for the melee-cone occupancy query (entities in the cells overlapping the
    // attacker's cone), filtered to the exact cone tiles at the call site. Single-threaded tick loop, so reuse is safe.
    private readonly List<WorldEntity> _attackCandidateScratch = [];
    // COMBAT-QOL: reused buffer of the victims a resolved attack actually damaged (entity + amount), so HandleAttack
    // can emit one AOI-gated cosmetic DamageEventMessage per real hit without allocating per attack. Single-threaded
    // tick/handler path, so reuse across attacks is safe.
    private readonly List<FreeAimSectorResolver.DamagedVictim> _damagedVictimScratch = [];
    // LIVING-ENEMIES P2: reused candidate buffer for a monster's throttled aggro scan (players in the cells around
    // the monster, then filtered to alive players within aggroRadius). Single-threaded tick loop (StepMonsterAi runs
    // in TickCore, not concurrently with the snapshot pass), so reusing one buffer across all monsters is safe.
    private readonly List<WorldEntity> _monsterAggroScratch = [];
    private readonly List<VisibleEntity> _visibleCandidateScratch = [];
    private readonly List<WorldEntity> _visibleEntityScratch = [];
    private readonly HashSet<uint> _visibleNetworkIdScratch = [];
    private readonly List<uint> _despawnScratch = [];
    private readonly List<WorldEntity> _payloadEntityScratch = [];
    private readonly List<EntityStateSnapshot> _snapshotChunkScratch = [];
    private readonly VisibleEntityComparer _visibleEntityComparer = new();
    private readonly ProtocolEncodeBuffer _messageEncodeBuffer = new();
    private readonly ProtocolEncodeBuffer _snapshotEncodeBuffer = new();
    private readonly Dictionary<Guid, PendingTileSave> _dirtyDurableTiles = [];

    // LOOT P4b: reusable buffer of corpse entity ids due to decay this tick (collected, then despawned outside the
    // dictionary enumeration so we don't mutate _corpses while iterating it). Cleared each pass; no per-tick alloc.
    private readonly List<ulong> _corpseDecayScratch = [];

    // LIVING-ENEMIES P1: the server-side leashed-roam brain for EntityKind.Monster. Owns every monster's per-AI
    // state + a seeded PRNG (seeded off the map seed so a given world's roaming is reproducible in tests/repro
    // runs), and hops each monster through the continuous HopLocomotion (the same shared swept-circle wall collision
    // players integrate against). Constructed after _zone since it closes over _zone.IsWalkable / _zone.QueryNearbyWalls.
    // Stepped each tick by StepMonsterAi (a sibling pass to StepHeldMovementIntents), paced off the hop cadence — so a
    // monster never hops every tick.
    // MONSTER-BEHAVIOR P3 (docs/monster-behavior-design.md): the per-type BEHAVIOR ("brain") registry — id -> the
    // IMonsterBehavior a monster of that BehaviorId runs its roam/chase/attack decisions through. Only "basicRoamer"
    // exists today (the single shared BasicRoamerBehavior, formerly the standalone _monsterAi); P4 adds a second brain
    // (the gnoll's Skirmisher) as a new entry. ResolveBehavior maps a MonsterType to its behavior on spawn/death/step
    // (loud-but-safe fallback to "basicRoamer" on an unknown id). Mirrors the locomotion registry exactly.
    private readonly Dictionary<string, IMonsterBehavior> _behaviors;

    // The default behavior ("basicRoamer") every type falls back to when its BehaviorId isn't registered. Cached so the
    // resolver never re-looks-it-up. Always present (seeded alongside the registry below).
    private readonly IMonsterBehavior _defaultBehavior;

    // One-time loud warning de-dup: an unknown BehaviorId logs ONCE (per distinct id) then silently falls back to
    // basicRoamer. Mirrors _warnedUnknownLocomotionIds (the manifest philosophy: don't crash on a typo'd id; warn).
    private readonly HashSet<string> _warnedUnknownBehaviorIds = new(StringComparer.OrdinalIgnoreCase);

    // MONSTER-BEHAVIOR P1 (docs/monster-behavior-design.md): the per-type LOCOMOTION registry — id -> the
    // IMonsterLocomotion ("body") a monster of that LocomotionId moves through. Only "hop" exists today (the single
    // shared HopLocomotion, moved here from the AI's ctor); P2 adds GlideLocomotion as a second entry. ResolveLocomotion
    // maps a MonsterType to its locomotion each tick (loud-but-safe fallback to "hop" on an unknown id). GameServer owns
    // this (like it owns _monsterTypeOf) so the AI stays locomotion-agnostic — told its body per step.
    private readonly Dictionary<string, IMonsterLocomotion> _locomotions;

    // The default locomotion ("hop") every type falls back to when its LocomotionId isn't registered. Cached so the
    // resolver never re-looks-it-up. Always present (seeded alongside the registry below).
    private readonly IMonsterLocomotion _defaultLocomotion;

    // One-time loud warning de-dup: an unknown LocomotionId logs ONCE (per distinct id) instead of every tick, then
    // silently falls back to hop. Mirrors the manifest philosophy (don't crash on a typo'd id; warn + carry on).
    private readonly HashSet<string> _warnedUnknownLocomotionIds = new(StringComparer.OrdinalIgnoreCase);

    // MOVEMENT-ACTIONS (Phase A): the server-side movement-action executor (ballistic jump etc.). Holds each entity's
    // active action instance and advances it each tick (StepActions, a sibling pass to StepMonsterAi). Constructed
    // after _zone since it closes over _zone.QueryNearbyWalls / _zone.ApplyMonsterLanding (the SAME shared collision +
    // apply seams the player integrator and the hop use). Phase A has NO trigger source — the set stays empty (and
    // the pass is ~free) until the wire (Phase B) / AI (Phase C) trigger actions; it exists + is driven now so the
    // executor is exercised headlessly and is ready to wire.
    private readonly ServerActionExecutor _actionExecutor;

    // TELEGRAPH T1 (closes todo/N-iframe-gate-choke-point.md): THE single player-damage choke point — dead-guard +
    // dodge-roll i-frame gate + ApplyDamage live inside it; the landed tail (damage-event broadcast + the death edge)
    // is this class's OnPlayerDamageLanded. BOTH player-damage paths (ApplyMonsterAttack's melee + the telegraph
    // resolve) route through TryDamagePlayer, so no current or future path can bypass the gate. Constructed after
    // _actionExecutor (it closes over HasActiveIFrames).
    private readonly PlayerDamageGate _playerDamage;

    // TELEGRAPH T1 (docs/ability-telegraph-sync-design.md): the scheduled-telegraph engine — pending {shape locked at
    // cast, resolveTick, damage} entries resolved each tick by TickCore (a sibling pass to StepMonsterAi/StepAll)
    // against positions AT the resolve tick, damaging players through the choke point above. Server-only this phase
    // (NO wire, NO rendering — T2 replicates the telegraph event). Constructed after _zone + _playerDamage (it closes
    // over the world's spatial gather + the gate).
    private readonly TelegraphScheduler _telegraphs;

    // BOSS-1 (docs/boss-encounter-sunderer-design.md): the Sunderer encounter lifecycle engine (Step() runs each tick
    // in TickCore, right after _telegraphs.ResolveDue). Owns the arena state machine — /boss enter/leave, the
    // countdown, boss spawn (HP scaled by participant count) + despawn, and the reset/victory rules — through injected
    // seams (SpawnMonsterCore / a leak-free despawn / Zone.Teleport / SendSystem). Constructed after _monsterTypes +
    // _zone + the behavior registry (its spawn delegate resolves the "sunderer" type + registers its behavior).
    private readonly BossEncounterEngine _bossEncounter;

    // DUO-SKILLSHOT (exp/duo-abilities): the fusion-skillshot engine (Step() runs each tick in TickCore, a sibling of
    // _telegraphs.ResolveDue). Constructed after _zone + _contributionLedger since it closes over the projectile
    // spawn/move/despawn seams, the world's spatial gather, the shared monster-damage path, and the pairing gate.
    private readonly SkillshotEngine _skillshots;

    // DUO-SKILLSHOT: the per-projectile tier, keyed by the projectile entity id — read by EnsureEntitySpawns to map the
    // tier to the replicated visual (tint + scale), the SAME channel monsters use. Set in the projectile spawn seam,
    // dropped in the despawn seam. A small transient map (projectiles live < ~1.2s), never leaked.
    private readonly Dictionary<ulong, ProjectileTier> _projectileTierOf = new();

    // DUO-WAVE2 (exp/duo-abilities) ability 3 (Laser Tether) + ability 4 (Midpoint Detonation): the two co-op steppers,
    // siblings of _skillshots (Step() each tick in TickCore, before the snapshot). Both close over the SAME spatial
    // gather, the shared monster-damage seam (ApplyDuoMonsterDamage), the shared monster-slow seam (SlowMonster), and —
    // for the tether's overstretch DoT — the player-damage choke point. Wire status/marker/cue relays close back over
    // the session set.
    private readonly TetherEngine _tether;
    private readonly MidpointDetonationEngine _detonation;

    // DUO-WAVE2: the SHARED monster-slow registry (tether sweep + detonation slow zone). Maps a monster entity id to the
    // absolute tick its brief slow lapses; while present its SpeedMultiplier is held at base × MonsterSlowFactor and the
    // reduced cadence is replicated via MovementSpeedChanged (the reused speed-modifier path). StepMonsterSlows restores
    // the base multiplier + re-broadcasts once the tick passes.
    private readonly Dictionary<ulong, uint> _monsterSlowUntil = new();
    private readonly List<ulong> _slowExpiryScratch = [];

    // MONSTER-SEPARATION (todo/N-monster-monster-collision-separation.md): the server-authoritative monster↔monster
    // de-penetration pass, run each tick AFTER all movement is resolved and BEFORE the snapshot (SeparateMonsters in
    // TickCore). Constructed after _zone since it closes over the SAME shared seams the hop/glide use — the spatial
    // neighbour query (World.GatherInterestCandidates), the wall query + resolver (QueryNearbyWalls), and the
    // apply-landing seam (ApplyMonsterLanding) — plus the live shared body radius. Pure position resolution: no
    // protocol/AI/wall-collision change, never touches Velocity.
    private readonly MonsterSeparation _monsterSeparation;

    // Reused participant buffer for the separation pass — refilled each tick from the live monster set (no per-tick
    // allocation in the hot loop).
    private readonly List<WorldEntity> _monsterSeparationScratch = new();

    // PLAYER↔MONSTER COLLISION: reused spatial-candidate buffer for the monster-side player-obstacle gathers (the
    // locomotions' GatherPlayerObstacles + the executor's GatherActionObstacles). Refilled per gather call
    // (GatherInterestCandidates clears it). Single-threaded tick loop, and the monster-AI pass and the executor's
    // StepAll run sequentially (never reentrant within one gather), so one shared buffer is safe — no per-tick alloc.
    // (The PLAYER integrator's own monster-obstacle gather lives in Zone with its own scratch — a separate seam.)
    private readonly List<WorldEntity> _obstacleCandidateScratch = new();

    // MOVEMENT-ACTIONS Phase B1: the SHARED action registry — the SAME defs the client loads (MovementActionRegistry
    // .Default is built from the shared assembly). HandleActionIntent resolves the wire ActionId to a def from here,
    // so server execution and (B2) client prediction run identical trajectories. Static-shared today (compile-time
    // defs); the Phase-B live-tuning path (ActionTuningMessage) would replicate per-instance values later.
    private readonly MovementActionRegistry _actionRegistry = MovementActionRegistry.Default;

    // LIVING-ENEMIES P2-POLISH: the table of monster TYPES (named templates — slime now) + their live-tunable,
    // replicated per-type tuning. Replaces the former single global monster.* tuning block: a spawned monster
    // remembers its type (via _monsterTypeOf), and StepMonsterAi reads that type's Tunables + SpeedMultiplier each
    // tick. Tick-rate-fixed at construction (for the tick-quantised pause/cooldown derivations).
    private readonly MonsterTypeRegistry _monsterTypes;

    // ECOLOGY E1 (docs/ecology-v1-design.md §3/§8): the authored regions (Content/ecology.json, mirrors
    // monsters.json's load/clamp/code-seed-fallback pattern) + their live per-region×type {stock, pressure} math
    // engine. EcologyTick() is called from TickCore every 200 server ticks (10s @ 20Hz — see the gate at the call
    // site). Server-only this phase: NO spawning (E2), NO persistence (E3), NO wire (E4) — just the numbers.
    private readonly EcologyState _ecology;

    // LOOT P4a: the shared loot-table store (slime_loot + the nested rare_material_pool). KillMonster resolves
    // the dead monster's type -> LootTableId -> table and rolls it. P4a only LOGS the rolled stacks (P4b spawns
    // the corpse that holds them); nothing is delivered to any inventory here.
    private readonly LootTableRegistry _lootTables = LootTableRegistry.CreateDefault(ItemRegistry.Default);

    // LOOT P4a: the seeded RNG for loot rolls, off the map seed (mixed with a constant so it isn't lockstep
    // with the roam AI's same-seed stream). Single-threaded tick loop => no lock needed. Deterministic for a
    // given world; the headless tests roll their OWN seeded Random so they don't depend on this stream.
    private readonly Random _lootRng;

    // LOOT P4b: the contribution ledger (group-loot groundwork). As players damage a monster, HandleAttack records
    // the damaging player's durable CharacterId here; on death KillMonster snapshots the contributor set onto the
    // corpse's eligibleLooters and forgets the entry. Solo => the eligible set is just the killer. Pure + tiny.
    private readonly ContributionLedger _contributionLedger = new();

    // LOOT P4b: the live corpses' server-side loot payloads, keyed by the Corpse WorldEntity's ENTITY id (stable for
    // the corpse's life; the network id can be reused after despawn). KillMonster adds one; the interact loot-all and
    // the decay pass remove + despawn. The contents stay SERVER-SIDE (never replicated) this phase. Tiny.
    private readonly Dictionary<ulong, Corpse> _corpses = [];

    // Per-monster type membership (entity id -> its type). Set on spawn; REMOVED on death (LIVING-ENEMIES P3 —
    // KillMonster calls _monsterTypeOf.Remove + the behavior's Forget, fixing the former add-only leak flagged in
    // todo/monster-types-followups.md). The AI step reads it to build that monster's Tunables from the live per-type
    // values. Tiny (a handful of monsters).
    private readonly Dictionary<ulong, MonsterType> _monsterTypeOf = [];

    // LIVING-ENEMIES P3: the PERSISTENT monster spawners, keyed by spawner id. A spawner owns a monster, respawns it
    // after the type's delay when it dies, and is the red-tile anchor (the monster's leash home = the spawner tile).
    // `/monster` creates one. Server objects, not replicated entities — the red marker is sent via SpawnerMarkerMessage
    // keyed by SpawnerId (AOI-driven), so it survives the monster's death/respawn. Tiny (a handful).
    private readonly Dictionary<uint, MonsterSpawner> _spawners = [];

    // Reverse map: a live monster's entity id -> the spawner that owns it, so on death we find the spawner in O(1) to
    // schedule its respawn. Kept in lockstep with each spawner's LiveMonsterId (added on spawn, removed on death).
    private readonly Dictionary<ulong, MonsterSpawner> _spawnerOfMonster = [];

    // Monotonic spawner-id allocator (distinct id space from entity network ids — these key the red marker only).
    private uint _nextSpawnerId = 1;

    // ECOLOGY E2 (docs/ecology-v1-design.md §3/§8 E2): one RegionSpawner per authored region×type, built ONCE at
    // boot (BuildRegionSpawners, right after _ecology loads) from RegionSpawnPlanner's deterministic tile
    // derivation. Materialized from the stock every tick (MaterializeRegionSpawners) — NOT the legacy
    // MonsterSpawner timer path (D10: `/monster` orphan spawners keep their own timer, untouched, coexisting).
    private readonly List<RegionSpawner> _regionSpawners = [];

    // Reverse map: a live region-spawned monster's entity id -> the RegionSpawner that owns it, so KillMonster
    // finds the region×type to record the kill against in O(1) — the region-ecology sibling of
    // _spawnerOfMonster. A monster id is NEVER in both maps (SpawnMonsterForSpawner and SpawnMonsterForRegion are
    // the only two spawn paths, and each registers into exactly one).
    private readonly Dictionary<ulong, RegionSpawner> _regionSpawnerOfMonster = [];

    // Reused candidate buffer for the "no spawn within 6 units of a player" gather (MaterializeRegionSpawners),
    // mirroring _monsterAggroScratch's reuse discipline (single-threaded tick loop, refilled per call).
    private readonly List<WorldEntity> _regionSpawnPlayerScratch = [];

    // ECOLOGY E4 (docs/ecology-v1-design.md §3/§8 E4): the LAST-BROADCAST per-region×type state, seeded at boot
    // from the freshly-constructed _ecology (so the very first CheckRegionEcologyChange call — the next
    // EcologyTick or the first kill — compares against real initial values, never an empty cache that would
    // spuriously "flip" every type on the first check). CheckRegionEcologyChange updates this in lockstep with
    // every BroadcastRegionEcology, so the cache always reflects exactly what was last put on the wire.
    private readonly Dictionary<string, Dictionary<string, EcologyState.PopulationState>> _lastSentEcologyState =
        new(StringComparer.OrdinalIgnoreCase);

    // D5/§3 pacing: "one spawn per 2s per region×type", tick-quantised off the live tick rate at construction
    // (mirrors how MonsterTypeRegistry quantises its own tick-based knobs once from options.TickRate).
    private readonly uint _regionSpawnPacingTicks;

    // D5/§3 "no spawn within 6 units of a player" — a EUCLIDEAN exclusion radius (world units), tested against
    // the spawn tile's centre.
    private const double RegionSpawnPlayerExclusionRadius = 6.0d;

    // D7: overgrown-spawn modifier — new spawns get +25% maxHealth and +25% renderScale while their region×type
    // reads Overgrown (EcologyState.PopulationState.Overgrown). Applied ONLY at spawn time (never retroactively
    // to an already-alive monster), per the task brief.
    private const double OvergrownSpawnStatMultiplier = 1.25d;

    // Half-extent (in tiles) of the cell neighborhood an AOI query must examine. The grid returns every
    // entity in the cells overlapping [viewer ± this], and the per-entity interest test then filters to
    // the exact set. It MUST cover the interest EXIT radius (interest radius + hysteresis), so a
    // hysteresis-retained entity sitting between the entry and exit radius is never dropped — dropping
    // one would be both a visible bug and an anti-cheat hole. Derived from the LIVE interest radius and
    // recomputed whenever AdminSetTuning changes it (S60); the entity-grid cell size stays fixed (a pure
    // perf knob — correctness is independent of it, the query box just grows to cover a larger radius).
    private int _aoiQueryRadiusTiles;

    private uint _serverTick;
    private uint _nextPersistenceCheckpointTick;
    private long _pendingMovementElapsedTicks;
    private long _traceStartTimestamp;
    private int _snapshotsSentThisTick;

    // CONTINUOUS MIGRATION (prediction-regression fix): the Stopwatch timestamp of the previous dt-budget credit
    // pass. The anti-speedhack budget must accrue REAL elapsed wall-clock time, NOT the fixed tick interval — when
    // a server tick runs long (a GC gen2 pause / the startup entities spike) the client keeps spending real-time dt
    // the whole stall, so crediting only 1/TickRate per tick LEAVES THE BUDGET SHORT and clamps the honest inputs
    // that queued during the stall (a SOFT predicted-vs-authoritative rubberband the client never modeled — the
    // measured regression vs the budget-less exp server). Crediting the real elapsed since this timestamp refunds
    // the full stall window while still bounding a cheater to real-elapsed + the burst allowance (anti-speedhack
    // intact: the cap is unchanged; only the accrual source moves from assumed-fixed to actual-elapsed). 0 until the
    // first credit pass (the per-session seed handles a fresh peer's first move).
    private long _lastBudgetCreditTimestamp;

    // ECOLOGY E3: `ecologyRepository` is OPTIONAL (defaults to NullEcologyRepository) so every existing test
    // call site (`new GameServer(options, someCharacterRepository)`) keeps compiling unchanged — only Program.cs
    // and any new persistence-focused test need to pass a real one.
    public GameServer(ServerOptions options, ICharacterRepository characters, IEcologyRepository? ecologyRepository = null)
    {
        _options = options;
        _tuning = new ServerTuning(options);
        _aoiQueryRadiusTiles = ResolveAoiQueryRadiusTiles(_tuning.InterestRadius);
        _characters = characters;
        _ecologyRepository = ecologyRepository ?? new NullEcologyRepository();
        _persistence = new PersistenceWriteBehindWorker(characters);
        _nextPersistenceCheckpointTick = options.PersistenceCheckpointTicks;
        _runtimeGuard = new ServerRuntimeGuard(_metrics);
        _movementTrace = new ServerMovementTrace(options);
        _resourceNodes = ResourceNodeRegistry.CreateDefault(_itemRegistry);
        // AUTHORED-MAP M3: the genVersion comes from options (FromEnvironment defaults to the authored
        // town+floor-1 map and derives the matching 384x384 world-size defaults; MMO_GEN_VERSION=1 is
        // the procedural escape hatch, and hand-constructed test options default procedural too).
        _zone = Zone.CreateGenerated(
            options.WorldWidthTiles,
            options.WorldHeightTiles,
            options.MapSeed,
            options.GenVersion,
            options.SpawnDistribution,
            ResolveEntityGridCellSize(options.InterestRadius));
        // NODE-FIELD N1/N2 (docs/node-field-design.md D1-D3): the shared catalogue, built ONCE right after the
        // zone from the SAME (seed, authored map) the client independently derives (the CatalogHash drift guard
        // on ZoneInfo, D2). A non-authored (genVersion 1, procedural) zone has no authored markers/categories to
        // build a catalogue from -- both sides fall back to the trivial empty one (0 entries, so CatalogHash
        // still agrees by construction) rather than special-casing "no catalogue" everywhere. NodeField owns the
        // per-index mutable depleted/respawn state on top of it.
        _nodeCatalog = _zone.Authored is { } authoredMap ? NodeCatalog.Build(_zone.Seed, authoredMap) : NodeCatalog.Empty();
        _nodeField = new NodeField(_nodeCatalog);
        // LIVING-ENEMIES P2-POLISH: the monster-type registry (seeds the one "slime" type). Tick rate fixes the
        // pause/cooldown/scan tick-quantisation, mirroring how ServerTuning derived the old global monster.* ticks.
        // CONTINUOUS MIGRATION (Phase 8): built BEFORE the AI so the hop locomotion's live hop-distance provider can
        // read the default type's HopDistanceUnits fresh each hop.
        _monsterTypes = LoadMonsterTypes(options.TickRate);
        // ECOLOGY E1: load the authored regions (Content/ecology.json, falling back to the code-seeded §7 starter
        // regions like the monster manifest does) and build the live stock/pressure state off them. No dependency
        // on _monsterTypes/_zone — the ecology's "type" is just the string id RecordKill/E2 will pass, not a
        // resolved MonsterType.
        _ecology = new EcologyState(LoadEcology());
        // ECOLOGY E3 (docs/ecology-v1-design.md D8, §8 E3): overlay any PERSISTED stock/pressure on top of the
        // K-seed EcologyState just constructed — a restart must not heal the world. Must run AFTER the K-seed
        // above (it only overwrites region×types it finds a saved row for) and BEFORE BuildRegionSpawners below
        // (materialization reads live stock immediately).
        LoadEcologyPopulations();
        // ECOLOGY E2 (docs/ecology-v1-design.md §8 E2; docs/procedural-population-design.md D5): derive every
        // authored region×type's spawn geography NOW (deterministic from the zone's seed/categories, computed
        // once) and build its RegionSpawner. Needs _zone (map/categories) + _monsterTypes (resolve each typeId to
        // its MonsterType) — both already constructed above; does NOT need _behaviors/_actionExecutor (those are
        // only touched once materialization actually spawns a monster, in TickCore, not here).
        _regionSpawners.AddRange(BuildRegionSpawners());
        // ECOLOGY E4: seed the last-broadcast cache from the just-constructed _ecology's initial values (D1: every
        // region×type seeds at K) — see the field doc for why an empty cache would be wrong.
        foreach (var region in _ecology.Registry.Regions)
        {
            var byType = new Dictionary<string, EcologyState.PopulationState>(StringComparer.OrdinalIgnoreCase);
            foreach (var typeId in region.Types.Keys)
            {
                byType[typeId] = _ecology.StateOf(region.Id, typeId);
            }

            _lastSentEcologyState[region.Id] = byType;
        }

        // D5/§3 pacing tick-quantised once from the live tick rate (>= 1 tick so a silly TickRate can't produce a
        // zero-tick "spawn every tick" pace).
        _regionSpawnPacingTicks = (uint)Math.Max(1, 2 * options.TickRate);
        // MOVEMENT-ACTIONS (Phase A/C): the action executor reuses the EXACT same shared collision derivation + apply
        // seam ordinary movement and the hop use (so an action collides byte-identically), and the same live body
        // radius (read fresh per tick). Driven each tick by StepAll. CONSTRUCTED BEFORE the behavior (Phase C) — it has no
        // dependency on the AI, but the AI's HopLocomotion now drives its hop arc THROUGH this executor (BeginMonsterHop
        // + IsActive below), so it must exist first.
        _actionExecutor = new ServerActionExecutor(
            options.TickRate,
            () => _tuning.BodyRadiusUnits,
            _zone.QueryNearbyWalls,
            _zone.ApplyMonsterLanding,
            // PLAYER↔MONSTER COLLISION: a monster's hop arc / charge dash STOPS at a nearby player (kind-aware gather).
            // MOVEMENT-ACTIONS Phase D: a player's charge/dodge-roll dash STOPS at bodies via the SAME Zone gather its
            // walking uses; only the player jump still avoids nothing (the P5 status quo).
            GatherActionObstacles);
        // MONSTER-SEPARATION: wire the de-penetration pass to the SAME shared seams (live body radius, the spatial
        // neighbour query, the wall query + resolver, and the apply-landing seam) the locomotions/actions use, so a
        // separation nudge collides byte-identically with walls and migrates the spatial bucket exactly like a hop.
        _monsterSeparation = new MonsterSeparation(
            () => _tuning.BodyRadiusUnits,
            _zone.World.GatherInterestCandidates,
            _zone.QueryNearbyWalls,
            _zone.ApplyMonsterLanding);
        // TELEGRAPH T1: the player-damage choke point (the REAL executor i-frame oracle + this class's landed tail)
        // and the telegraph engine wired through it — the SAME spatial gather AOI/aggro use for the resolve-time
        // superset query, and the SAME gate the monster melee routes through (one gate, two callers).
        // DUO-WAVE2 ability 2 (Unison Shield): the gate now also carries the shield ABSORB seam (AbsorbShield), so a
        // shield soaks a hit at the single choke point before ApplyDamage — no player-damage source can bypass it.
        _playerDamage = new PlayerDamageGate(_actionExecutor.HasActiveIFrames, OnPlayerDamageLanded, AbsorbShield);
        _telegraphs = new TelegraphScheduler(_zone.World.GatherInterestCandidates, _playerDamage.TryDamagePlayer);
        // DUO-SKILLSHOT: the fusion-skillshot engine, wired to the SAME spatial gather AOI/combat use and the SAME
        // monster-damage path the melee routes through (ApplyProjectileDamage → ApplyDamage + cosmetic event +
        // contribution ledger + KillMonster). Spawn/move/despawn go through Zone; the pairing gate reads the sessions.
        // BOSS-2 (P1): the fusion-shatter + solo boss-hit-count hooks the Sunderer encounter subscribes to (no-ops for
        // every non-boss fight — the engine filters to its own boss id and ignores events off-encounter).
        _skillshots = new SkillshotEngine(
            SpawnProjectileEntity,
            MoveProjectileEntity,
            DespawnProjectileEntity,
            _zone.World.GatherInterestCandidates,
            ApplyProjectileDamage,
            AreEntitiesPaired,
            onFusion: (tier, tick) => _bossEncounter.OnFusion(tier, tick),
            onMonsterHit: (monsterId, tick) => _bossEncounter.OnSkillshotMonsterHit(monsterId, tick));
        // DUO-WAVE2 ability 3 (Laser Tether): the beam stepper — same spatial gather, the shared monster-damage +
        // monster-slow seams, the player-damage choke point for the overstretch DoT, and the tether-status wire relay.
        _tether = new TetherEngine(
            _zone.World.GatherInterestCandidates,
            ApplyDuoMonsterDamage,
            SlowMonster,
            _playerDamage.TryDamagePlayer,
            SendTetherStatus);
        // DUO-WAVE2 ability 4 (Midpoint Detonation): the charge/blast stepper — same gather + monster-damage + slow
        // seams, plus the echo-cue relay and the live-tracking charge-marker relay.
        // BOSS-4 (P3 Ward break): report every resolved blast's centre + tier + pair separation to the Sunderer
        // encounter — a detonation within the ward-break radius of the boss opens the burst window (the onFusion
        // pattern; a no-op off-encounter, and the engine filters by phase + distance + duo-mode tier/separation).
        // Reads _bossEncounter at CALL time (constructed just below), the same late-bound pattern the SkillshotEngine
        // onFusion/onMonsterHit wiring above uses.
        _detonation = new MidpointDetonationEngine(
            _zone.World.GatherInterestCandidates,
            ApplyDuoMonsterDamage,
            SlowMonster,
            SendEchoCueTo,
            SendMidpointCharge,
            onBlast: (center, tick, tier, pairSeparationUnits) => _bossEncounter.OnMidpointBlast(center, tick, tier, pairSeparationUnits));
        // LIVING-ENEMIES P1 + CONTINUOUS MIGRATION (Phase 8) + MOVEMENT-ACTIONS (Phase C): seed the monster roam AI off
        // the map seed so a given world replays the same roaming (deterministic for repro/tests). Navigation is CONTINUOUS
        // (Euclidean ranges, sub-tile targets); movement is now a REAL ballistic Jump — the HopLocomotion DECIDES the hop
        // (collision-valid heading + clamped distance, the SAME swept-circle wall derivation + body radius players collide
        // at) and BeginMonsterHop hands the arc to the shared executor, which advances XY through the resolver + the
        // ballistic Z (the replicated VerticalOffset) per tick. The IsAction-active gate (lazy lambda) keeps the AI from
        // re-hopping mid-arc. Hop distance + body radius are read FRESH each hop so a live retune applies next tick.
        // MONSTER-BEHAVIOR P1: the per-type LOCOMOTION registry. Build the ONE "hop" entry (the shared HopLocomotion,
        // moved here from the AI's ctor) and resolve a monster's locomotion per-type each tick (StepMonsterAi →
        // ResolveLocomotion). NO behavior change this phase: only "hop" is registered, so every type resolves to this
        // same instance → byte-identical hopping. P2 adds a second entry (GlideLocomotion) + a type that selects it.
        _defaultLocomotion = new HopLocomotion(
            () => _monsterTypes.Default.HopDistanceUnits,
            () => _tuning.BodyRadiusUnits,
            _zone.QueryNearbyWalls,
            BeginMonsterHop,
            id => _actionExecutor.IsActive(id),
            // PLAYER↔MONSTER COLLISION: the hop DECISION probe accounts for a player in the way (the executor does the
            // physical stop per tick; see GatherActionObstacles above).
            GatherPlayerObstacles);
        // MONSTER-BEHAVIOR P2: the second body — GlideLocomotion (a continuous velocity-based WALK). Same shared
        // collision seams the hop uses (QueryNearbyWalls + the resolver, ApplyMonsterLanding, the live player body
        // radius); it SETS the monster's Velocity = heading × SpeedUnitsPerSecond, which already rides the wire (v39)
        // and is extrapolated by the default remote render — so a glider walks smoothly on the client with NO protocol
        // change. tickRate fixes the per-tick integration dt. The "gnoll" type (Content/monsters.json) selects it.
        _locomotions = new Dictionary<string, IMonsterLocomotion>(StringComparer.OrdinalIgnoreCase)
        {
            ["hop"] = _defaultLocomotion,
            ["glide"] = new GlideLocomotion(
                () => _tuning.BodyRadiusUnits,
                _zone.QueryNearbyWalls,
                _zone.ApplyMonsterLanding,
                options.TickRate,
                // MONSTER-BEHAVIOR P5: the SAME self-guard the hop carries — while a charge dash owns the monster's
                // movement (the executor's StepAll drives it), the glide must NOT also step it (a double-move).
                monster => _actionExecutor.IsActive(monster),
                // PLAYER↔MONSTER COLLISION: a chasing glider STOPS at / slides along a nearby player (server-only).
                GatherPlayerObstacles),
        };
        // MONSTER-BEHAVIOR P3/P4: the per-type BEHAVIOR ("brain") registry. Build the "basicRoamer" entry (the shared
        // BasicRoamerBehavior, formerly the standalone _monsterAi) and, as of P4, the "skirmisher" entry (the gnoll's
        // flee-when-wounded brain) and resolve a monster's behavior per type each spawn/death/step (ResolveBehavior).
        // The skirmisher takes the IDENTICAL deps as the basicRoamer (same aggro/resolve/attack/walkability wiring);
        // it overrides only the chase flee decision. Both are seeded off the map seed so a given world replays the same
        // roaming (deterministic for repro/tests); they own disjoint per-monster state so the shared seed is harmless.
        _defaultBehavior = new BasicRoamerBehavior(
            options.MapSeed,
            _zone.IsWalkable,
            FindMonsterAggroTarget,
            TryResolveMonsterTarget,
            ApplyMonsterAttack,
            // MONSTER-BEHAVIOR P5: the charge dep (both brains get it; the per-type ChargeEnabled tunable gates whether
            // a charge ever actually fires — only the gnoll composes "charge", so basicRoamer/slime is byte-identical).
            TryBeginMonsterCharge,
            // TELEGRAPH T1: the slam dep — same shape (both brains get it; the per-type SlamEnabled tunable gates
            // whether a slam ever actually casts — only the slime composes "slam" today).
            TryBeginMonsterSlam,
            // SLIME-SLAM ROOT+LEAP: the slam-LEAP dep — the ballistic jump to the locked telegraph origin the brain
            // fires at the plan's leap-start tick (only ever reached after a successful TryBeginMonsterSlam).
            BeginMonsterSlamLeap,
            // TELEGRAPH SHAPES WEDGE+LINE: the LUNGE dep — a telegraphed line charge (schedule line + hand back the cast
            // plan the shared slam channel roots+dashes through). Both brains get it; the per-type LungeEnabled tunable
            // gates whether a lunge ever casts (only the Sunderer authors a charge windup), so non-lungers are unchanged.
            TryBeginMonsterLunge);
        _behaviors = new Dictionary<string, IMonsterBehavior>(StringComparer.OrdinalIgnoreCase)
        {
            ["basicRoamer"] = _defaultBehavior,
            ["skirmisher"] = new SkirmisherBehavior(
                options.MapSeed,
                _zone.IsWalkable,
                FindMonsterAggroTarget,
                TryResolveMonsterTarget,
                ApplyMonsterAttack,
                TryBeginMonsterCharge,
                TryBeginMonsterSlam,
                BeginMonsterSlamLeap,
                TryBeginMonsterLunge),
            // BOSS-2 (P1): the interposer drone's minimal midline-seek brain. Its interpose target (the pair's segment
            // midpoint, or the boss<->player midpoint solo) is the Sunderer encounter's authority; the drone stays
            // encounter-agnostic behind this delegate. Inert for any non-drone (only the "interposer" type selects it).
            ["interposer"] = new InterposerBehavior((WorldEntity drone, out WorldVector target) =>
                _bossEncounter.TryGetInterposeTarget(out target)),
            // BOSS-3 (P2): the splinter's minimal seek-nearest-participant brain. Its target (the nearest living
            // participant) is the encounter's authority (TryGetSplinterTarget); the POP is encounter-driven, so the
            // brain never harms a player. Inert for any non-splinter (only the "splinter" type selects it).
            ["splinter"] = new SplinterBehavior((WorldEntity splinter, out WorldVector target) =>
                _bossEncounter.TryGetSplinterTarget(splinter, out target)),
        };
        // BOSS-1 (docs/boss-encounter-sunderer-design.md): the Sunderer encounter engine. Every world touch is an
        // injected seam so the engine is headlessly testable and GameServer stays the single wiring point: spawn the
        // boss via SpawnMonsterCore with a per-participant HP override (NOT a spawner — no auto-respawn); despawn it
        // leak-free (DespawnBossEntity, no corpse/loot); resolve entity ids; teleport a player (Zone.Teleport + clear
        // its held move intent so it doesn't walk off the entry tile); and chat a participant via its session.
        _bossEncounter = new BossEncounterEngine(
            options.TickRate,
            spawnBoss: (tile, maxHealth) =>
            {
                var type = _monsterTypes.TryGet("sunderer", out var sunderer) ? sunderer : _monsterTypes.Default;
                return SpawnMonsterCore(type, tile, maxHealthOverride: maxHealth, renderScaleOverride: null);
            },
            despawnBoss: DespawnBossEntity,
            tryResolve: id => _zone.World.TryGet(id, out var entity) ? entity : null,
            teleport: (player, tile) =>
            {
                _zone.Teleport(player, tile);
                player.OwnerSession?.ClearMoveIntent();
            },
            notify: (entityId, text) =>
            {
                var session = SessionByEntity(entityId);
                if (session is not null)
                {
                    SendSystem(session, text);
                }
            },
            // BOSS-2 (P1): spawn the interposer drone (the "interposer" type — glide body, interposer brain, 40 HP, no
            // loot) at `tile`, like the boss via SpawnMonsterCore (NOT a spawner — the engine owns the respawn cadence).
            spawnDrone: tile =>
            {
                var type = _monsterTypes.TryGet("interposer", out var interposer) ? interposer : _monsterTypes.Default;
                return SpawnMonsterCore(type, tile, maxHealthOverride: null, renderScaleOverride: null);
            },
            // BOSS-2 (P1): tear down an encounter ADD (the drone) through the SAME leak-free by-id path the boss uses.
            despawnAdd: DespawnBossEntity,
            // BOSS-2 (P1): broadcast the boss's plating state to AOI viewers (Laws 4/7 legibility).
            broadcastPlating: BroadcastBossPlating,
            // BOSS-3 (P2): damage a participant through THE player-damage choke point (i-frames + shield absorb apply
            // naturally) — the user's damage-choke invariant; the engine never mutates Stats directly.
            damagePlayer: _playerDamage.TryDamagePlayer,
            // BOSS-3 (P2 Repel): server-authoritative, wall-resolved displacement (the reconcile snap is accepted v1).
            displacePlayer: (player, target) => _zone.DisplaceResolved(player, target, CollisionDefaults.BodyRadius),
            // BOSS-3 (P2 Echo Lash): reuse the wave-2 shield ECHO CUE on the participant's own client (ShieldPress kind,
            // no protocol change) — resolve the entity, then the existing SendEchoCueTo relay.
            echoCue: entityId =>
            {
                if (_zone.World.TryGet(entityId, out var participant))
                {
                    SendEchoCueTo(participant, EchoCueKind.ShieldPress);
                }
            },
            // BOSS-3 (P2 Splinter ring): spawn a splinter add (the "splinter" type), like the drone (engine owns cadence).
            spawnSplinter: tile =>
            {
                var type = _monsterTypes.TryGet("splinter", out var splinter) ? splinter : _monsterTypes.Default;
                return SpawnMonsterCore(type, tile, maxHealthOverride: null, renderScaleOverride: null);
            },
            // BOSS-3 (P2 Repel/Bind): schedule the field's VISUAL as a NO-DAMAGE telegraph ring (damage 0) — reuse the
            // decal wire; the field RESOLVE is encounter-side on pair distance, so the ring carries zero gameplay weight.
            scheduleFieldVisual: (center, radius, startTick, resolveTick) =>
                _telegraphs.Schedule(_bossEncounter.BossId, TelegraphShape.Circle(center, radius), startTick, resolveTick, damage: 0, source: "Sunder field"),
            // BOSS-4 (P3 root): re-centre the boss ONCE at the 40% edge — Zone.Teleport (bumps StateRevision → the snap
            // rides the snapshot), cancel any in-flight action (a lunge dash mid-cast), and zero its velocity so the
            // glider stops extrapolating. The ONGOING chase suppression is the IsBossRooted gate in StepMonsterAi.
            // REVIEW MEDIUM-1: Cancel, NOT ClearEntity — Cancel is the interrupt seam that LANDS an airborne entity
            // (SnapToGround); ClearEntity is the leaving-the-world teardown and left a mid-leap boss frozen floating
            // at its arc height for the whole Core phase (the brain skip means nothing would ever land it).
            rootBoss: (boss, tile) =>
            {
                _zone.Teleport(boss, tile);
                _actionExecutor.Cancel(boss, _serverTick);
                boss.StopMovement();
            },
            // BOSS-4 (P3 rotating sweep beam): schedule a LINE telegraph from the boss through the scheduler's NORMAL gate
            // path (real damage, dodgeable at resolve) — the SAME player-damage choke point the field/monster melee use.
            scheduleBeam: (origin, length, aim, halfWidth, damage, startTick, resolveTick) =>
                _telegraphs.Schedule(_bossEncounter.BossId, TelegraphShape.Line(origin, length, aim, halfWidth), startTick, resolveTick, damage, source: "Sunder beam"));
        // LOOT P4a: seed the loot RNG off the map seed (mixed so it's not the roam AI's identical stream).
        _lootRng = new Random(unchecked(options.MapSeed * 31 + 0x100712));
        SpawnAuthoredProps();
        SpawnDummies();
        _netManager = new NetManager(_listener)
        {
            AutoRecycle = false,
            DisconnectTimeout = 15000
        };

        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnNetworkReceive;
        _listener.NetworkLatencyUpdateEvent += OnNetworkLatencyUpdate;
        _listener.NetworkErrorEvent += (endpoint, error) =>
        {
            _metrics.RecordNetworkError();
            Log.Warn($"Network error from {endpoint}: {error}.");
        };
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timerResolution = WindowsTimerResolutionScope.Begin();
        if (timerResolution.IsActive)
        {
            Log.Info("Enabled Windows timer resolution: 1ms.");
        }
        else if (OperatingSystem.IsWindows())
        {
            Log.Warn($"Windows timer resolution request failed: result={timerResolution.BeginResult}.");
        }

        _netManager.Start(_options.Port);
        Log.Info($"Server listening on UDP {_options.Port}.");
        if (_cadenceTrace.Enabled)
        {
            Log.Info("Server cadence trace enabled: writing .run/server-cadence.csv and .run/server-steps.csv.");
        }

        _traceStartTimestamp = Stopwatch.GetTimestamp();
        var tickIntervalTimestampTicks = PreciseTickScheduler.TickIntervalTimestampTicks(_options.TickRate);
        var tickInterval = PreciseTickScheduler.ToTimeSpan(tickIntervalTimestampTicks);
        var nextTickAt = Stopwatch.GetTimestamp();
        var lastTickStartedAt = 0L;
        var tickBudget = new TickBudgetRecorder();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _netManager.PollEvents();
                _syntheticLoad.Poll();
                DrainMainThreadActions();

                var now = Stopwatch.GetTimestamp();
                var catchUpTicksThisIteration = PreciseTickScheduler.CountDueTicks(now, nextTickAt, tickIntervalTimestampTicks);
                // STALL CLAMP (LIVE-DESYNC FIX 2026-07-04): after a long process stall (AV/disk freeze — observed
                // multi-second on this dev machine) the catch-up loop below would replay EVERY missed tick
                // back-to-back with NO PollEvents in between — the per-session "ticks since last snapshot ack"
                // watchdog then ages 160+ ticks in one burst and kicks every live client that was acking fine the
                // whole time (observed live: five 'no snapshot ack' warns for the SAME peer within 0.3 ms). Past
                // one second of debt, DROP the lost wall time instead of replaying it: run one tick now and
                // re-anchor. Simulation time is tick-count-based, so dropped ticks just mean the world simulated
                // less wall time; the client's cosmetic clock re-anchors on >2s steps by design.
                if (catchUpTicksThisIteration > _options.TickRate)
                {
                    Log.Warn(
                        $"Tick loop stalled ~{catchUpTicksThisIteration * tickInterval.TotalSeconds:0.0}s "
                            + $"({catchUpTicksThisIteration} missed ticks) — dropping the debt instead of catch-up-bursting.");
                    nextTickAt = now;
                    catchUpTicksThisIteration = 1;
                }

                while (now >= nextTickAt)
                {
                    var tickStartedAt = Stopwatch.GetTimestamp();
                    var interTickGap = lastTickStartedAt == 0
                        ? TimeSpan.Zero
                        : Stopwatch.GetElapsedTime(lastTickStartedAt, tickStartedAt);
                    lastTickStartedAt = tickStartedAt;
                    var gen0Before = GC.CollectionCount(0);
                    var gen1Before = GC.CollectionCount(1);
                    var gen2Before = GC.CollectionCount(2);
                    tickBudget.Reset();
                    var scheduleDrift = Stopwatch.GetElapsedTime(nextTickAt, now);
                    Tick(tickBudget);
                    var tickDuration = Stopwatch.GetElapsedTime(tickStartedAt);
                    var budgetSample = tickBudget.ToSample();
                    var gcSample = new GcCollectionSample(
                        GC.CollectionCount(0) - gen0Before,
                        GC.CollectionCount(1) - gen1Before,
                        GC.CollectionCount(2) - gen2Before);
                    _metrics.RecordTick(tickDuration, scheduleDrift, budgetSample, gcSample);
                    _movementTrace.TickHitch(
                        _serverTick,
                        interTickGap,
                        tickDuration,
                        scheduleDrift,
                        budgetSample,
                        catchUpTicksThisIteration,
                        gcSample,
                        tickInterval);
                    nextTickAt += tickIntervalTimestampTicks;
                }

                await PreciseTickScheduler.WaitUntilNextTickOrPollAsync(nextTickAt, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            QueueConnectedPlayersForPersistence();
            await _persistence.FlushAsync(CancellationToken.None);
            await _persistence.DisposeAsync();
            // ECOLOGY E3 (docs/ecology-v1-design.md D8): graceful-shutdown save — awaited directly (not
            // Task.Run'd) since a restart must not heal the world and this is the LAST chance to persist before
            // the process exits. The in-flight checkpoint task is awaited FIRST (E3 review M2): its DB write
            // could otherwise acquire the SQLite write lock AFTER the final commit and overwrite it with a
            // snapshot from seconds earlier — last-committer-wins staleness at the worst possible moment.
            await _ecologyCheckpointInFlight;
            await SaveEcologyPopulationsAsync(CancellationToken.None);
            _syntheticLoad.Stop();
            _netManager.Stop();
            _cadenceTrace.Flush();
            _cadenceTrace.Dispose();
            Log.Info("Server stopped.");
        }
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        request.AcceptIfKey(_options.ConnectionKey);
    }

    private void OnPeerConnected(NetPeer peer)
    {
        _sessions[peer] = new ClientSession(peer);
        _metrics.RecordPeerConnected();
        // CONTINUOUS MIGRATION (Phase 4, v37): replicate the live authoritative body radius (the ServerTuning knob, default
        // 0.5) so the client predictor collides against the SAME radius the server integrates with (the wall-determinism gap).
        TrySend(peer, new ServerHelloMessage(ServerName, ProtocolCodec.Version, _options.TickRate, _options.StepCooldownMs, _options.InterestRadius, (float)_tuning.BodyRadiusUnits), DeliveryMethod.ReliableOrdered);
        Log.Info($"Peer connected: {FormatPeer(peer)}.");
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (!_sessions.Remove(peer, out var session))
        {
            return;
        }

        // DUO-SKILLSHOT: a disconnect unpairs — clear the pair + notify the surviving partner BEFORE despawn.
        BreakPair(session);

        if (session.IsAuthenticated)
        {
            if (_zone.Despawn(session.EntityId!.Value, out var entity))
            {
                _actionExecutor.ClearEntity(entity.Id); // drop any action state (cooldowns) for the leaving entity
                _networkIds.Return(entity.NetworkId);
                QueueTileSave(session, entity.Position);
                FlushInventory(entity);
            }
            else
            {
                _networkIds.Return(session.NetworkId);
                QueueTileSave(session);
            }
        }

        _metrics.RecordPeerDisconnected();
        Log.Info($"Peer disconnected: {FormatPeer(peer)}; reason={disconnectInfo.Reason}.");
    }

    private void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        if (_sessions.TryGetValue(peer, out var session))
        {
            session.LastLatencyMs = latency;
        }
    }

    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        try
        {
            var bytes = reader.GetRemainingBytes();
            var message = ProtocolCodec.Decode(bytes);
            _metrics.RecordReceived(message, bytes.Length);
            HandleMessage(peer, message);
        }
        catch (Exception exception)
        {
            _metrics.RecordBadPacket();
            var count = _sessions.TryGetValue(peer, out var session)
                ? session.RecordBadPacket()
                : MaxBadPacketsBeforeDisconnect;

            Log.Warn($"Failed to process packet from {FormatPeer(peer)}: {exception.Message}");
            if (count >= MaxBadPacketsBeforeDisconnect)
            {
                Log.Warn($"Disconnecting {FormatPeer(peer)} after {count} bad packets.");
                _netManager.DisconnectPeer(peer);
            }
            else
            {
                TrySend(peer, new ServerErrorMessage("bad_packet", "Bad packet."), DeliveryMethod.ReliableOrdered);
            }
        }
        finally
        {
            reader.Recycle();
        }
    }

    private void HandleMessage(NetPeer peer, IProtocolMessage message)
    {
        if (!_sessions.TryGetValue(peer, out var session))
        {
            return;
        }

        // LIVING-ENEMIES P3: while a player is DEAD (the brief respawn delay), drop its action inputs — movement
        // (intent/input/commit), movement-mode flips, and attacks — so it can't act while downed. Chat/ack/admin still
        // flow (a dead admin can still see/ack snapshots and use the panel). The session is server-paced, so suppressing
        // the inputs is enough; the entity is teleported + refilled on respawn.
        if (session.IsAuthenticated && session.IsDead && IsSuppressedWhileDead(message.Type))
        {
            return;
        }

        switch (message)
        {
            case ClientHelloMessage hello:
                Log.Info($"Client hello from {FormatPeer(peer)}: {hello.ClientName}.");
                break;
            case LoginRequestMessage login:
                BeginLogin(peer, session, login);
                break;
            case MoveIntentMessage intent:
                if (session.IsAuthenticated)
                {
                    // CONTINUOUS MIGRATION (Phase 3, v36): per-input continuous movement — integrate this input by its
                    // own dt ON THE RECEIVE PATH (the experiment model), with the anti-speedhack clamps applied.
                    HandleMoveIntent(session, intent);
                }
                break;
            case ChatSendMessage chat:
                if (session.IsAuthenticated)
                {
                    HandleChat(session, chat.Text);
                }
                break;
            case SnapshotAckMessage ack:
                if (session.IsAuthenticated)
                {
                    session.AcknowledgeSnapshot(ack.LastSnapshotSequence, _serverTick);
                }
                break;
            case InteractRequestMessage interact:
                if (session.IsAuthenticated)
                {
                    HandleInteract(session, interact.TargetNetworkId);
                }
                break;
            case HarvestNodeMessage harvest:
                if (session.IsAuthenticated)
                {
                    HandleHarvestNode(session, harvest.NodeIndex);
                }
                break;
            case AdminSetTuningMessage tuning:
                if (session.IsAuthenticated)
                {
                    HandleAdminSetTuning(session, tuning.Key, tuning.Value);
                }
                break;
            case SaveMonsterTuningMessage:
                if (session.IsAuthenticated)
                {
                    HandleSaveMonsterTuning(session);
                }
                break;
            case AdminSetPlayerCollisionMessage playerCollision:
                if (session.IsAuthenticated)
                {
                    HandleAdminSetPlayerCollision(session, playerCollision.Enabled);
                }
                break;
            case AdminSetStatMessage stat:
                if (session.IsAuthenticated)
                {
                    HandleAdminSetStat(session, stat.Stat, stat.Value);
                }
                break;
            case AttackMessage attack:
                if (session.IsAuthenticated)
                {
                    HandleAttack(session, attack.Sequence, attack.Kind, attack.AimAngle, attack.AuthoredTick);
                }
                break;
            case ActionIntentMessage action:
                if (session.IsAuthenticated)
                {
                    HandleActionIntent(session, action.ActionSeq, action.ActionId, action.Heading, action.AuthoredTick);
                }
                break;
            case LootActionMessage loot:
                if (session.IsAuthenticated)
                {
                    HandleLootAction(session, loot.CorpseNetworkId, loot.Kind, loot.TemplateKey);
                }
                break;
            case FireSkillshotMessage fire:
                if (session.IsAuthenticated)
                {
                    HandleFireSkillshot(session, fire.Sequence, fire.AimAngle);
                }
                break;
            case AimPreviewMessage preview:
                if (session.IsAuthenticated)
                {
                    HandleAimPreview(session, preview.Heading, preview.Active);
                }
                break;
            case DuoAbilityMessage duo:
                if (session.IsAuthenticated)
                {
                    HandleDuoAbility(session, duo.Sequence, duo.Ability);
                }
                break;
            default:
                TrySend(peer, new ServerErrorMessage("unsupported_message", $"Unsupported {message.Type}."), DeliveryMethod.ReliableOrdered);
                break;
        }
    }

    // LIVING-ENEMIES P3: the action message types suppressed while a player is dead (movement + attacks). Non-action
    // messages (chat, snapshot ack, admin tuning/stat, hello/login) are NOT suppressed so the client stays responsive.
    private static bool IsSuppressedWhileDead(MessageType type) =>
        type is MessageType.MoveIntent or MessageType.Attack
			or MessageType.InteractRequest or MessageType.LootAction
			// MOVEMENT-ACTIONS Phase B1: an action trigger is an action message — suppressed while the player is downed
			// (like Attack/MoveIntent), so a dead player can't jump.
			or MessageType.ActionIntent
			// NODE-FIELD N2: a harvest is an action message too — suppressed while downed, like InteractRequest.
			or MessageType.HarvestNode
			// DUO-SKILLSHOT: firing a skillshot + streaming an aim preview are actions — suppressed while downed, like
			// Attack (a dead player can't fire or telegraph an aim).
			or MessageType.FireSkillshot
			or MessageType.AimPreview
			// DUO-WAVE2: the co-op R/G/V triggers are actions - suppressed while downed (a dead player cannot
			// shield, toggle a tether, or detonate).
			or MessageType.DuoAbility;

    private void BeginLogin(NetPeer peer, ClientSession session, LoginRequestMessage login)
    {
        if (session.IsAuthenticated || session.LoginInProgress)
        {
            return;
        }

        session.LoginInProgress = true;
        var loginStartedAt = Stopwatch.GetTimestamp();
        _ = Task.Run(async () =>
        {
            try
            {
                var character = await _characters.LoadOrCreateAsync(login.AccountName, login.DisplayName, CancellationToken.None);
                var items = await _characters.LoadItemsAsync(character.CharacterId, CancellationToken.None);
                _mainThreadActions.Enqueue(() =>
                {
                    if (!_sessions.TryGetValue(peer, out var current))
                    {
                        return;
                    }

                    var networkId = 0u;
                    try
                    {
                        var role = ResolveRole(login.AccountName, character.DisplayName);
                        var takeover = KickExistingSessionForCharacter(current, character.CharacterId);
                        // CONTINUOUS MIGRATION (Phase 10): the persisted (or handed-off) CONTINUOUS position is the
                        // login truth. Resolve the SPAWN TILE from its rounded tile (walkability + default-spawn
                        // scatter logic is tile-based); only when the resolver keeps that exact tile do we restore
                        // the off-grid sub-tile position below — otherwise the player was relocated to a fresh
                        // spawn tile and must sit at that tile's centre.
                        var loginPosition = takeover.Position ?? character.Position;
                        var loginTile = ResolveLoginTile(loginPosition.ToTileRounded());
                        networkId = _networkIds.Rent();
                        // On account takeover, hand off the kicked session's in-memory Inventory (which may
                        // hold harvested items not yet flushed to the DB) instead of the DB-loaded stacks
                        // read at the start of this login. Without this, a mid-session relogin reloads the
                        // pre-harvest inventory and can later overwrite the kicked session's flushed gains.
                        var inventory = takeover.Inventory ?? new Inventory(_itemRegistry, items);
                        var entity = _zone.SpawnPlayer(networkId, character.CharacterId, character.DisplayName, loginTile, current, inventory);
                        // Restore the exact off-grid position only when the resolved spawn tile matches the
                        // persisted position's rounded tile (i.e. it was walkable and not relocated). If the player
                        // was scattered to a different spawn tile, keep the tile-centre the spawn seeded.
                        if (loginTile == loginPosition.ToTileRounded())
                        {
                            entity.RestorePosition(loginPosition);
                        }

                        current.Authenticate(entity.NetworkId, character.CharacterId, character.DisplayName, role, character.ZoneId);
                        current.AttachEntity(entity);
                        // Seed the player's tiles/sec speed stat — LIVE in Phase 1 (the integrator reads it).
                        RefreshSpeedStat(entity);
                        _metrics.RecordLogin(true, Stopwatch.GetElapsedTime(loginStartedAt));
                        TrySend(peer, new LoginResultMessage(true, character.CharacterId, character.DisplayName, role, entity.TileCoord, ""), DeliveryMethod.ReliableOrdered);
                        TrySend(peer, CreateZoneInfoMessage(), DeliveryMethod.ReliableOrdered);
                        // Send a full inventory snapshot so the client panel reflects the persisted (or
                        // handed-off, on takeover) contents immediately — otherwise it stays empty until the
                        // next harvest delta. Covers both login paths: `inventory` is whatever the entity was
                        // spawned with. Snapshot() yields only non-empty stacks; skip the send entirely when
                        // the inventory is empty — a fresh character's panel is already empty, so an empty
                        // update is a pointless packet.
                        var snapshot = inventory.Snapshot();
                        if (snapshot.Count > 0)
                        {
                            SendInventoryUpdate(current, snapshot);
                        }
                        // COMBAT-S1: replicate the player's initial vitals (full 100/100 each by default) so the
                        // HUD bars render real values immediately on login, not the F5 stub.
                        SendPlayerStats(current, entity);
                        // COMBAT-TUNING: replicate the current combat feel-knobs so the client's free-aim wedge mesh,
                        // swing-root prediction, and radial cooldown indicator derive from the server's authoritative
                        // values immediately on login (not stale client constants).
                        SendCombatTuning(current);
                        // LIVING-ENEMIES P2-POLISH: replicate the per-monster-TYPE tuning so an admin's F1 Monster tab
                        // can list the types (dropdown) and show + edit the live values immediately on login.
                        SendMonsterTuning(current);
                        // PLAYER-COLLISION-TOGGLE: replicate the authoritative player↔player collision flag so this
                        // client's obstacle gather gates on the SAME value the server integrator does (prediction
                        // parity) from the first frame — and the F1 Server-tab checkbox seeds to the live value.
                        SendPlayerCollisionSetting(current);
                        // NODE-FIELD N2 (D4): replicate the field's current exceptions (only the depleted indices)
                        // so a joining client's rendered field starts correct.
                        SendNodeStateBatch(current);
                        // ECOLOGY E4 (D6a/D6c): replicate the full authored region set (minimap legibility) then
                        // announce the single most-extreme region as a login rumor — both read-only off the live
                        // EcologyState, no simulation gated on either.
                        SendRegionEcology(current);
                        SendEcologyLoginRumor(current);
                        Log.Info($"Authenticated {character.DisplayName} ({character.CharacterId}) as {role}.");
                    }
                    catch (Exception exception)
                    {
                        _networkIds.Return(networkId);
                        current.LoginInProgress = false;
                        _metrics.RecordLogin(false, Stopwatch.GetElapsedTime(loginStartedAt));
                        TrySend(peer, new LoginResultMessage(false, Guid.Empty, login.DisplayName, ClientRole.Player, _zone.ResolveSpawnTile(Zone.DefaultSpawnTile), "No network id available."), DeliveryMethod.ReliableOrdered);
                        Log.Error("Login failed", exception);
                    }
                });
            }
            catch (Exception exception)
            {
                _mainThreadActions.Enqueue(() =>
                {
                    if (_sessions.TryGetValue(peer, out var current))
                    {
                        current.LoginInProgress = false;
                    }

                    _metrics.RecordLogin(false, Stopwatch.GetElapsedTime(loginStartedAt));
                    TrySend(peer, new LoginResultMessage(false, Guid.Empty, login.DisplayName, ClientRole.Player, _zone.ResolveSpawnTile(Zone.DefaultSpawnTile), exception.Message), DeliveryMethod.ReliableOrdered);
                    Log.Error("Login failed", exception);
                });
            }
        });
    }

    private TakeoverState KickExistingSessionForCharacter(ClientSession current, Guid characterId)
    {
        ClientSession? existing = null;
        foreach (var session in _sessions.Values)
        {
            if (ReferenceEquals(session, current) || !session.IsAuthenticated || session.CharacterId != characterId)
            {
                continue;
            }

            existing = session;
            break;
        }

        return existing is null
            ? default
            : KickAuthenticatedSession(existing, "logged_in_elsewhere", "Logged in elsewhere.");
    }

    private TakeoverState KickAuthenticatedSession(ClientSession session, string code, string message)
    {
        // DUO-SKILLSHOT: a kick (relogin-elsewhere/takeover) unpairs — clear the pair + notify the partner first.
        BreakPair(session);
        TrySend(session.Peer, new ServerErrorMessage(code, message), DeliveryMethod.ReliableOrdered);
        _sessions.Remove(session.Peer);
        _metrics.RecordPeerDisconnected();

        WorldVector? position = null;
        Inventory? inventory = null;
        if (session.EntityId.HasValue && _zone.Despawn(session.EntityId.Value, out var entity))
        {
            // CONTINUOUS MIGRATION (Phase 10): hand the kicked entity's CONTINUOUS position to the taking-over
            // login so a relog-elsewhere restores the exact sub-tile spot, not the rounded tile centre.
            position = entity.Position;
            // Hand the live in-memory inventory to the taking-over login so any not-yet-flushed harvest
            // gains survive the relogin. FlushInventory still enqueues its dirty changes for persistence;
            // the quantities live on this same object, so nothing is lost either way.
            inventory = entity.Inventory;
            _actionExecutor.ClearEntity(entity.Id); // drop any action state (cooldowns) for the kicked entity
            _networkIds.Return(entity.NetworkId);
            QueueTileSave(session, entity.Position);
            FlushInventory(entity);
        }
        else
        {
            _networkIds.Return(session.NetworkId);
            QueueTileSave(session);
        }

        _netManager.DisconnectPeer(session.Peer);
        Log.Info($"Kicked {session.DisplayName}: {message}");
        return new TakeoverState(position, inventory);
    }

    private void Tick(TickBudgetRecorder tickBudget)
    {
        _runtimeGuard.TryRun("tick", () => TickCore(tickBudget));
    }

    private void TickCore(TickBudgetRecorder tickBudget)
    {
        _serverTick++;

        // Held-direction movement (protocol v15): step each session's entity at the server's own cooldown
        // cadence from its held intent, instead of acting on per-step client messages. DrainPending... is
        // kept for any movement work still timed off the tick thread (currently none → 0).
        tickBudget.RecordElapsed(TickBudgetCategory.Movement, DrainPendingMovementElapsedTicks());
        using (tickBudget.Measure(TickBudgetCategory.Movement))
        {
            // CONTINUOUS MIGRATION (Phase 3, v36): PLAYER integration is now 100% input-driven (HandleMoveIntent on the
            // receive path). This tick pass no longer integrates — it (a) credits each session's anti-speedhack dt
            // budget by the elapsed tick time and (b) does the keepalive/stop housekeeping (a wedged "moving" client
            // is force-stopped; a stale player's velocity is zeroed for AOI/animation). Monsters keep the fixed-cadence
            // tile-step path below, UNTOUCHED.
            CreditMoveDtBudgetsAndKeepalive();
            // LIVING-ENEMIES P1: a sibling movement pass that steps each roaming Monster off the step cooldown
            // (the TILE-STEP path — monsters stay tile-stepped; the continuous integrator is PLAYER-only), so
            // monsters idle near home and occasionally stroll within a leash.
            StepMonsterAi();
            // MOVEMENT-ACTIONS (Phase A): advance every entity currently in a movement action (ballistic jump) by one
            // tick — XY through the shared resolver, Z via the ballistic arc, ending + arming cooldown on the landing
            // tick. Sibling pass to StepMonsterAi; ~free while no action is active (no trigger source until Phase B/C).
            _actionExecutor.StepAll(_zone.World, _serverTick);
            // MONSTER-SEPARATION: with ALL movement resolved this tick (AI tile-step / glide + the action executor),
            // push any overlapping monster bodies apart (pure position de-penetration, no physics) BEFORE the snapshot,
            // so the corrected positions replicate this same tick. Bounded by the spatial grid (no O(n²) scan); a no-op
            // when nothing overlaps.
            SeparateMonsters();
            // TELEGRAPH T1: resolve every telegraph whose resolve tick arrived — placed AFTER all movement for this
            // tick (player input rides the receive path; AI/actions/separation just ran), so shape membership is
            // judged against positions AT tick T, and BEFORE BroadcastSnapshot so the damage/HP change replicates
            // this same tick. ~free while nothing is pending (the common case).
            _telegraphs.ResolveDue(_serverTick);
            // BOSS-1: pump the Sunderer encounter lifecycle (countdown → boss spawn; victory / wipe / empty resets).
            // Placed right AFTER ResolveDue so a boss Cleave (slam) telegraph that just killed the last participant is
            // seen as a WIPE this same tick — before RespawnPlayers (the Other block) teleports the bodies to town. It
            // spawns/despawns the boss directly (bounded work; no per-tick cost when Idle).
            _bossEncounter.Step(_serverTick);
            // DUO-SKILLSHOT: step in-flight skillshots (fusion merge + straight-line flight + monster hits) after all
            // movement, BEFORE BroadcastSnapshot so a fusion merge, a projectile move, and a hit's HP change all
            // replicate this same tick. Fixed dt = 1/TickRate (the tick cadence). ~free when nothing is in flight.
            _skillshots.Step(_serverTick, 1d / _options.TickRate);
            // DUO-WAVE2 abilities 3 & 4: step the tether (band resolve + monster/player damage) and the midpoint
            // detonation (confirm-window expiry, charge live-tracking, blast resolve, lingering slow zones) — siblings
            // of the skillshot step, BEFORE the snapshot so damage/HP/marker changes replicate this same tick. Then
            // restore any monster whose brief slow lapsed (re-broadcasting the base cadence). ~free when none active.
            _tether.Step(_serverTick, 1d / _options.TickRate);
            _detonation.Step(_serverTick);
            StepMonsterSlows(_serverTick);
        }

        using (tickBudget.Measure(TickBudgetCategory.Other))
        {
            RespawnNodes();
            RegenEnemies();
            // LIVING-ENEMIES P3: spawn fresh monsters whose spawner's respawn delay elapsed, and respawn dead players
            // whose downed window elapsed. Both poll tiny sets (spawners / sessions) and no-op when nothing is due.
            RespawnMonsters();
            RespawnPlayers();
            // DUO-WAVE2 ability 2: drop any shield whose 4s duration lapsed this tick (push a single ShieldStatus clear
            // so the client bubble disappears — the only expiry signal on the wire). ~free when no shield is armed.
            StepShieldExpiry(_serverTick);
            // LOOT P4b: despawn any corpse whose decay deadline has arrived (unlooted corpses don't linger forever).
            DecayCorpses();
            // ECOLOGY E1 (docs/ecology-v1-design.md §3): advance the region×type stock/pressure math once every 200
            // server ticks (10s @ 20Hz) — the "ecology tick" cadence is coarser than the movement/AI tick, so the
            // gate lives here rather than inside EcologyState (which stays tick-count-agnostic for headless tests).
            if (_serverTick % 200 == 0)
            {
                _ecology.EcologyTick();
                // ECOLOGY E4: growth can move any/all regions this tick, so check every authored region for a
                // type-state flip and re-send only the ones that actually changed (CheckRegionEcologyChange is a
                // no-op wire-wise when nothing flipped — the common case).
                foreach (var region in _ecology.Registry.Regions)
                {
                    CheckRegionEcologyChange(region);
                }
            }

            // ECOLOGY E2 (§3 "Spawning"): materialize monsters from each region×type's stock, paced per
            // RegionSpawner. Runs every tick (cheap — a handful of region×type entries, each a tick-compare
            // unless its pacing gate is actually due) rather than gated like EcologyTick, so a spawn opportunity
            // that opens up mid-window (e.g. right after a kill frees a slot) is picked up on the very next tick.
            MaterializeRegionSpawners(_serverTick);
        }

        using (tickBudget.Measure(TickBudgetCategory.Persistence))
        {
            CheckpointDirtyDurableState();
        }

        BroadcastSnapshot(tickBudget);

        if (_cadenceTrace.Enabled)
        {
            _cadenceTrace.RecordTick(
                _serverTick,
                Stopwatch.GetElapsedTime(_traceStartTimestamp),
                _entityScratch,
                _snapshotsSentThisTick);
        }
    }

    private void BroadcastSnapshot(TickBudgetRecorder tickBudget)
    {
        _snapshotsSentThisTick = 0;
        _authenticatedScratch.Clear();
        foreach (var session in _sessions.Values)
        {
            if (session.IsAuthenticated)
            {
                _authenticatedScratch.Add(session);
            }
        }

        _entityScratch.Clear();
        _zone.World.CopyEntitiesTo(_entityScratch);

        foreach (var session in _authenticatedScratch)
        {
            TryBroadcastSnapshotToSession(session, _entityScratch, tickBudget);
        }
    }

    private void TryBroadcastSnapshotToSession(ClientSession session, IReadOnlyCollection<WorldEntity> entities, TickBudgetRecorder tickBudget)
    {
        try
        {
            BroadcastSnapshotToSession(session, entities, tickBudget);
        }
        catch (Exception exception)
        {
            _metrics.RecordRuntimeFault();
            Log.Error($"Runtime fault in snapshot for {session.DisplayName} #{session.NetworkId}.", exception);
        }
    }

    private void BroadcastSnapshotToSession(ClientSession session, IReadOnlyCollection<WorldEntity> entities, TickBudgetRecorder tickBudget)
    {
        using (tickBudget.Measure(TickBudgetCategory.Aoi))
        {
            SelectVisibleEntities(session, entities, _visibleEntityScratch, _visibleNetworkIdScratch);
        }

        using (tickBudget.Measure(TickBudgetCategory.Network))
        {
            SendEntityDespawns(session, _visibleNetworkIdScratch);
            EnsureEntitySpawns(session, _visibleEntityScratch);
            // LIVING-ENEMIES P3: sync the persistent spawner red-tile markers for this viewer (AOI-driven). Only when
            // there are spawners to consider; the viewer's own entity is the AOI center.
            if (_spawners.Count > 0 || session.KnownSpawnerIds.Count > 0)
            {
                if (TryGetSessionEntity(session, out var viewerEntity))
                {
                    SyncSpawnerMarkers(session, viewerEntity);
                }
            }

            // TELEGRAPH T2: sync pending telegraph announcements for this viewer (AOI-driven, the same known-id diff
            // shape as the spawner markers). Guarded to a no-op in the common case (nothing pending, nothing known).
            if (_telegraphs.PendingCount > 0 || session.KnownTelegraphIds.Count > 0)
            {
                if (TryGetSessionEntity(session, out var telegraphViewer))
                {
                    SyncTelegraphs(session, telegraphViewer);
                }
            }
        }

        SendSnapshotPackets(session, _visibleEntityScratch, tickBudget, out var visibleCount, out var chunkCount, out var sentBytes, out var sentPackets);

        if (sentPackets > 0)
        {
            _metrics.RecordSnapshotSent(sentBytes, visibleCount, entities.Count);
            _snapshotsSentThisTick++;
        }

        if ((visibleCount < entities.Count || sentPackets > 1) && _serverTick % (uint)(_options.TickRate * 5) == 0)
        {
            Log.Info($"snapshot for {session.DisplayName}: visible={visibleCount}/{entities.Count}, radius={_tuning.InterestRadius:0.#}, chunks={sentPackets}/{chunkCount}, bytes={sentBytes}");
        }
    }

    private void SendEntityDespawns(ClientSession recipient, IReadOnlySet<uint> visibleIds)
    {
        _despawnScratch.Clear();
        recipient.CollectSnapshotEntitiesMissingFrom(visibleIds, _despawnScratch);
        foreach (var networkId in _despawnScratch)
        {
            var packet = _messageEncodeBuffer.EncodeEntityDespawn(_serverTick, networkId);
            TrySend(recipient.Peer, packet, DeliveryMethod.ReliableOrdered, MessageType.EntityDespawn);
            recipient.ForgetEntityBaseline(networkId);
            recipient.ForgetKnownEntity(networkId);
        }
    }

    private void SelectVisibleEntities(
        ClientSession recipient,
        IReadOnlyCollection<WorldEntity> entities,
        List<WorldEntity> destination,
        HashSet<uint> visibleIds)
    {
        destination.Clear();
        visibleIds.Clear();
        _visibleCandidateScratch.Clear();

        if (!TryGetSessionEntity(recipient, out var recipientEntity))
        {
            return;
        }

        // Spatial-index candidate gather (S41): instead of scanning every world entity, query only the
        // cells overlapping the viewer's interest box. The grid returns a SUPERSET of the in-interest set
        // (it covers the exit/hysteresis radius), so applying the exact same IsEntityInInterest test to
        // the candidates below yields a result IDENTICAL to the old full scan. `entities.Count` (the total
        // world entity count) still drives the cap branch exactly as before.
        _zone.World.GatherInterestCandidates(recipientEntity.TileCoord, _aoiQueryRadiusTiles, _aoiCandidateScratch);

        if (_options.MaxVisibleEntities >= entities.Count)
        {
            foreach (var candidate in _aoiCandidateScratch)
            {
                if (IsEntityInInterest(recipientEntity, candidate, recipient, _tuning.InterestRadius))
                {
                    destination.Add(candidate);
                    visibleIds.Add(candidate.NetworkId);
                }
            }

            return;
        }

        foreach (var candidate in _aoiCandidateScratch)
        {
            var distanceSquared = DistanceSquared(recipientEntity, candidate);
            if (IsEntityInInterest(recipientEntity, candidate, recipient, _tuning.InterestRadius))
            {
                _visibleCandidateScratch.Add(new VisibleEntity(candidate, SnapshotSortKey(recipient, candidate, distanceSquared)));
            }
        }

        _visibleCandidateScratch.Sort(_visibleEntityComparer);
        var visibleCount = Math.Min(_visibleCandidateScratch.Count, _options.MaxVisibleEntities);
        for (var i = 0; i < visibleCount; i++)
        {
            var entity = _visibleCandidateScratch[i].Entity;
            destination.Add(entity);
            visibleIds.Add(entity.NetworkId);
        }
    }

    private void SendSnapshotPackets(
        ClientSession recipient,
        IReadOnlyList<WorldEntity> visible,
        TickBudgetRecorder tickBudget,
        out int visibleCount,
        out int chunkCount,
        out int sentBytes,
        out int sentPackets)
    {
        sentBytes = 0;
        sentPackets = 0;
        chunkCount = 0;
        if (!TryGetSessionEntity(recipient, out var recipientEntity))
        {
            visibleCount = 0;
            return;
        }

        // S76: the recipient-scoped step sequence for this build. Read once from the recipient's OWN entity and
        // ride it on every snapshot header (real-delta AND keep-alive), even when that entity is idle and gets
        // delta'd out of the payload below — it is recipient metadata, not entity payload.
        var recipientStepSeq = recipientEntity.StepSequence;
        // CONTINUOUS MIGRATION (Phase 3, v36): the recipient-scoped last INTEGRATED input seq rides the header too
        // (after RecipientStepSeq), so the Phase-4 predictor can trim/replay its unacked input buffer against it.
        var recipientLastInputSeq = recipient.LastInputSeq;

        // Acked-baseline selection (S46): send an entity iff its current revision differs from the revision
        // the CLIENT has acknowledged. A dropped snapshot is never acked, so its entities stay unacked and
        // are re-included next tick → self-healing under loss, no periodic full heartbeat needed. A viewer
        // gone silent past the safety threshold is force-rebaselined first (acked map cleared) so per-viewer
        // pending state cannot grow without bound; a longer threshold disconnects a wedged client.
        ApplyUnackedSafetyBound(recipient);

        _payloadEntityScratch.Clear();
        foreach (var entity in visible)
        {
            // CONTINUOUS MIGRATION (remote-walk fluidity — PER-TICK CONTINUOUS REPLICATION): a MOVING entity
            // (Velocity != 0) is force-included for EVERY in-AOI recipient each tick, carrying its LIVE continuous
            // position — even when its tile-keyed StateRevision has not bumped this tick. This deletes the tile-crossing
            // dependence for remote movers, which was a tile-stepped-era artifact wrong for continuous movement:
            // ApplyResolvedMove bumps StateRevision only on a rounded-tile crossing (R1), so a REMOTE viewer used to
            // receive a walking player only ~once per tile (~250ms @ 4u/s) and had to glide/extrapolate across the gap;
            // with the playout buffer (~125ms) shorter than that interval, every cycle overran into extrapolation and
            // corrected by a jittery amount at each handoff — a ~4Hz remote-walk stutter (the live symptom). Sending a
            // mover every tick hands the interpolator dense 50ms samples so it ALWAYS interpolates between two real
            // points → smooth, exactly like the airborne jump path below. The local predictor's own-entity sub-tile
            // reconcile (it must see its LIVE position every tick) is a strict subset of this (the own entity is just
            // one moving viewer), so that fix is preserved. At REST (Velocity == 0) an entity falls through to the
            // unchanged acked-baseline delta — idle AOI bandwidth is untouched; the stop-edge StateRevision bump
            // (StopMovement) re-publishes the final stopped position once. COST: per-tick re-sends WHILE an entity is
            // moving in a viewer's AOI (the bandwidth the tile-gate previously saved) — measured under the 120/30s
            // stress gate against the parity budget; this is the deliberate trade for continuous remote fluidity.
            // MOVEMENT-ACTIONS: an entity running a movement action (jump) is ALSO force-included for every recipient
            // each tick. A jump moves via the executor with Velocity == 0 (so `forceMoving` would miss a standstill
            // jump) and arcs a PARABOLIC VerticalOffset, so the interpolator needs the real per-tick height to glide;
            // IsActive covers it for the action's duration. On the landing tick IsActive is already false (the instance
            // is removed in Step before it returns), so the grounded height re-publishes via the normal
            // !HasAckedCurrentRevision path (SnapToGround's StateRevision bump) — no double-send.
            var forceMoving = entity.Velocity.LengthSquared > 0d;
            var forceActionAirborne = _actionExecutor.IsActive(entity);
            if (forceMoving || forceActionAirborne || !recipient.HasAckedCurrentRevision(entity))
            {
                _payloadEntityScratch.Add(entity);
            }
        }

        recipient.RememberSnapshotEntities(visible);

        if (_payloadEntityScratch.Count == 0)
        {
            // Per-recipient delta is empty (static AOI: nothing changed, entered, or left). With the periodic
            // full heartbeat gone (S46), sending nothing here would leave an idle-but-healthy viewer with no
            // snapshot to ack, so its ack-silence clock would grow until the disconnect bound wrongly dropped
            // it (~8 s on a perfect connection). Emit a low-rate EMPTY keep-alive instead: a tiny snapshot
            // carrying no entity states (isComplete=false so it does NOT reconcile/prune the client's visible
            // set) just so the client acks the sequence and stays alive. Tiny + low-rate → the dense-bandwidth
            // win (no full resend) is preserved.
            if (recipient.ShouldSendKeepAlive(_serverTick, SnapshotKeepAliveTicks))
            {
                SendKeepAliveSnapshot(recipient, recipientStepSeq, recipientLastInputSeq, visible.Count, tickBudget, ref sentBytes, ref sentPackets);
            }

            visibleCount = visible.Count;
            return;
        }

        // A snapshot is "complete" (the client may reconcile/prune to it) iff its payload carries every
        // currently-visible entity — i.e. nothing was omitted as already-acked. Re-baselines and the very
        // first snapshot are complete; an incremental delta is not (the client relies on reliable
        // EntityDespawn for AOI-exit removals, exactly as before).
        var isComplete = _payloadEntityScratch.Count == visible.Count;

        var maxEntitiesPerPacket = Math.Max(1, (MaxSequencedSnapshotBytes - ProtocolHeaderBytes - SnapshotHeaderBytes) / EstimateEntityStateBytes());
        chunkCount = Math.Max(1, (int)Math.Ceiling(_payloadEntityScratch.Count / (double)maxEntitiesPerPacket));
        var snapshotSequence = recipient.NextSnapshotSequence();

        // One per-seq carried record for the whole sequence (all chunks): the client acks the sequence only
        // once every chunk of it is received, so the baseline advances for the full payload atomically.
        var pending = recipient.BeginPendingSnapshot(snapshotSequence, _serverTick);
        foreach (var entity in _payloadEntityScratch)
        {
            pending.Add(entity.NetworkId, entity.StateRevision);
        }

        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            _snapshotChunkScratch.Clear();
            var start = chunkIndex * maxEntitiesPerPacket;
            var end = Math.Min(start + maxEntitiesPerPacket, _payloadEntityScratch.Count);
            for (var i = start; i < end; i++)
            {
                var entity = _payloadEntityScratch[i];
                _snapshotChunkScratch.Add(ToEntityStateSnapshot(entity));
                if (_movementTrace.Enabled)
                {
                    _movementTrace.SnapshotCarried(recipient, entity, snapshotSequence, _serverTick, chunkIndex, chunkCount);
                }
            }

            EncodedPacket packet;
            using (tickBudget.Measure(TickBudgetCategory.Serialize))
            {
                packet = _snapshotEncodeBuffer.EncodeWorldSnapshot(
                    _serverTick,
                    snapshotSequence,
                    recipientStepSeq,
                    recipientLastInputSeq,
                    visible.Count,
                    isComplete,
                    chunkIndex,
                    chunkCount,
                    _snapshotChunkScratch);
            }

            if (packet.Length > MaxSequencedSnapshotBytes)
            {
                Log.Warn($"Snapshot chunk exceeded budget for {recipient.DisplayName}: chunk={chunkIndex + 1}/{chunkCount}, bytes={packet.Length}.");
            }

            using (tickBudget.Measure(TickBudgetCategory.Network))
            {
                if (TrySend(recipient.Peer, packet.Buffer, packet.Length, DeliveryMethod.Unreliable))
                {
                    sentBytes += packet.Length;
                    sentPackets++;
                }
            }
        }

        // No "remember sent" baseline step: the acked baseline advances only on ACK (AcknowledgeSnapshot),
        // which is what makes a dropped snapshot self-heal — its carried entities stay unacked and re-send
        // next tick. We DO reset the keep-alive cadence clock, so a real delta defers the next empty
        // keep-alive: the keep-alive only fires after SnapshotKeepAliveTicks of NO snapshot at all.
        recipient.RememberSnapshotSentTick(_serverTick);
        visibleCount = visible.Count;
    }

    // Sends a single empty keep-alive WorldSnapshot to a viewer whose per-recipient delta is empty, so it
    // keeps acking and never trips the disconnect bound. The payload is empty and isComplete is FALSE — an
    // empty complete snapshot would tell the client to prune its entire visible set, so the keep-alive must
    // be non-complete to leave the client's known entities untouched. It opens NO pending record (carries no
    // entities, so there is nothing to baseline) but advances the cadence + disconnect clocks via
    // RememberSnapshotSentTick. The client acks it like any snapshot, refreshing its silence clock on ack.
    private void SendKeepAliveSnapshot(
        ClientSession recipient,
        uint recipientStepSeq,
        uint recipientLastInputSeq,
        int totalVisible,
        TickBudgetRecorder tickBudget,
        ref int sentBytes,
        ref int sentPackets)
    {
        var snapshotSequence = recipient.NextSnapshotSequence();
        _snapshotChunkScratch.Clear();

        EncodedPacket packet;
        using (tickBudget.Measure(TickBudgetCategory.Serialize))
        {
            // S76: the keep-alive carries the recipient's step seq in the header even though its payload is
            // empty — an idle player's own entity is exactly the case that gets no entity delta, so without
            // this the seq would never reach an idle client. CONTINUOUS MIGRATION (v36): the LastInputSeq rides
            // the keep-alive too (same reason — an idle player's input ack must still reach the client).
            packet = _snapshotEncodeBuffer.EncodeWorldSnapshot(
                _serverTick,
                snapshotSequence,
                recipientStepSeq,
                recipientLastInputSeq,
                totalVisible,
                isComplete: false,
                chunkIndex: 0,
                chunkCount: 1,
                _snapshotChunkScratch);
        }

        using (tickBudget.Measure(TickBudgetCategory.Network))
        {
            if (TrySend(recipient.Peer, packet.Buffer, packet.Length, DeliveryMethod.Unreliable))
            {
                sentBytes += packet.Length;
                sentPackets++;
            }
        }

        recipient.RememberSnapshotSentTick(_serverTick);
    }

    private void EnsureEntitySpawns(ClientSession recipient, IReadOnlyCollection<WorldEntity> authenticated)
    {
        foreach (var entity in authenticated)
        {
            if (recipient.KnowsEntity(entity.NetworkId))
            {
                continue;
            }

            // MONSTER-BEHAVIOR P6: the placeholder per-type VISUAL — a MONSTER ships its type's authored render tint +
            // scale (×1000) so the client renders it visibly distinct (a gnoll bigger + tinted) with no art assets.
            // Every other kind (players/dummies/resources/corpses) ships the defaults (white 0xFFFFFF / scale 1.0 →
            // 1000), a no-op the client renders byte-identically. The type is looked up by the monster's entity id
            // (_monsterTypeOf, set at spawn); a monster missing from the map falls back to the defaults too.
            var tintRgb = 0xFFFFFFu;
            var scaleMilli = (ushort)1000;
            if (entity.Kind == EntityKind.Monster && _monsterTypeOf.TryGetValue(entity.Id, out var spawnType))
            {
                tintRgb = spawnType.RenderTintRgb;
                // ECOLOGY E2 (D7): a region-spawned monster born while its region×type was Overgrown carries a
                // per-INSTANCE RenderScaleOverride (+25%, WorldEntity.SetRenderScaleOverride) — read it here in
                // preference to the shared type's RenderScale so only THIS monster looks bigger, never the type.
                var effectiveScale = entity.RenderScaleOverride ?? spawnType.RenderScale;
                scaleMilli = (ushort)Math.Clamp((int)Math.Round(effectiveScale * 1000d), 0, ushort.MaxValue);
            }
            else if (entity.Kind == EntityKind.Projectile && _projectileTierOf.TryGetValue(entity.Id, out var tier))
            {
                // DUO-SKILLSHOT: the projectile's tier rides the SAME replicated tint+scale channel monsters use —
                // no new wire field. Solo = small bright cyan; Good = bigger amber; Perfect = biggest magenta.
                (tintRgb, scaleMilli) = tier switch
                {
                    ProjectileTier.Perfect => (0xFF3CFFu, (ushort)1100),
                    ProjectileTier.Good => (0xFFB020u, (ushort)850),
                    _ => (0x40FFF0u, (ushort)550),
                };
            }

            var packet = _messageEncodeBuffer.EncodeEntitySpawn(
                entity.NetworkId,
                entity.CharacterId ?? Guid.Empty,
                entity.Kind,
                entity.DisplayName,
                entity.TileCoord,
                entity.Facing,
                EffectiveStepCooldownMs(entity),
                tintRgb,
                scaleMilli);
            TrySend(recipient.Peer, packet, DeliveryMethod.ReliableOrdered, MessageType.EntitySpawn);
            recipient.RememberKnownEntity(entity.NetworkId);

            // BOSS-2 REVIEW HIGH-1/MEDIUM-1: plating is edge-broadcast (BroadcastBossPlating), and every edge is
            // gated on KnowsEntity — but the spawn-time "plating up" edge fires inside the encounter Step, BEFORE
            // this method has introduced the boss to anyone, so it was dropped for every viewer on every fight (the
            // boss rendered unplated exactly when players must learn the mechanic). The fix is STATE-SYNC at the
            // introduction point: whoever just learned the boss entity also learns its current plating state. This
            // covers the spawn tick AND every late viewer (approach mid-fight, reconnect) — the same known-id-diff
            // pattern the spawner markers and telegraphs use.
            if (_bossEncounter.BossSpawned && entity.Id == _bossEncounter.BossId && _bossEncounter.PlatingActive)
            {
                TrySend(recipient.Peer, new BossPlatingMessage(entity.NetworkId, true), DeliveryMethod.ReliableOrdered);
            }
        }

        // LIVING-ENEMIES P3: the red anchor is now the persistent SPAWNER (not the transient monster), replicated via
        // a separate AOI-driven SpawnerMarker pass below — so it stays put across the monster's death/respawn.
    }

    // LIVING-ENEMIES P3: per-recipient AOI sync of the persistent SPAWNER red-tile markers. A spawner is a server
    // object, not a world entity, so it has no EntitySpawn; this is the parallel "spawn/despawn" for its marker.
    // For each spawner whose tile is within the viewer's interest radius and that the viewer doesn't yet know, send
    // SpawnerMarker(Active=true) (place the red tile); for each KNOWN spawner that has left AOI or no longer exists,
    // send Active=false (drop it). Reliable-ordered, like the entity spawn/despawn pair. The set is tiny (a handful),
    // and the diff is against the viewer's _knownSpawnerIds so a steady-state in-AOI spawner costs one Contains check.
    private readonly List<uint> _spawnerForgetScratch = [];

    private void SyncSpawnerMarkers(ClientSession recipient, WorldEntity recipientEntity)
    {
        // Place markers for spawners that newly entered AOI.
        foreach (var spawner in _spawners.Values)
        {
            var inAoi = IsTileInInterest(recipientEntity.Position, spawner.Tile, _tuning.InterestRadius);
            // TELEGRAPH T2 REVIEW FOLLOWUP (mirrors the telegraph fix below): remember-known only on a SUCCESSFUL
            // send. A failed send on a surviving session (transient socket hiccup, not a disconnect) must not mark
            // the viewer as knowing a marker it never received — that would permanently skip the retry and the
            // spawner tile silently never renders for that viewer. The diff naturally retries next AOI pass.
            if (inAoi && !recipient.KnowsSpawner(spawner.SpawnerId)
                && TrySend(recipient.Peer, new SpawnerMarkerMessage(spawner.SpawnerId, spawner.Tile, true), DeliveryMethod.ReliableOrdered))
            {
                recipient.RememberKnownSpawner(spawner.SpawnerId);
            }
        }

        // Drop markers for known spawners that left AOI or were removed.
        _spawnerForgetScratch.Clear();
        foreach (var spawnerId in recipient.KnownSpawnerIds)
        {
            var stillVisible = _spawners.TryGetValue(spawnerId, out var spawner)
                && IsTileInInterest(recipientEntity.Position, spawner.Tile, _tuning.InterestRadius);
            if (!stillVisible)
            {
                _spawnerForgetScratch.Add(spawnerId);
            }
        }

        foreach (var spawnerId in _spawnerForgetScratch)
        {
            TrySend(recipient.Peer, new SpawnerMarkerMessage(spawnerId, default, false), DeliveryMethod.ReliableOrdered);
            recipient.ForgetKnownSpawner(spawnerId);
        }
    }

    // TELEGRAPH T2: per-recipient AOI sync of the PENDING telegraph announcements — the same known-id diff shape as
    // SyncSpawnerMarkers, for the other non-entity replicated object. For each pending telegraph whose shape is within
    // the viewer's interest radius and that the viewer doesn't yet know, send the TelegraphMessage (reliable — a
    // dropped telegraph is a hit with no warning) and remember the id. Because the diff runs every broadcast tick,
    // ONE code path covers both required sends: the schedule-time announcement (the telegraph is new to everyone in
    // AOI on the tick it was scheduled) and the mid-windup AOI-enter delivery (a late joiner is missing the id, so the
    // next pass sends it — the deadline form then renders the correct REMAINING fill from the replicated startTick).
    //
    // The forget pass drops known ids whose telegraph is no longer pending (it resolved — ResolveDue ran earlier this
    // same tick), with NO wire message: clients self-resolve at T (the whole point), and T1 decided a telegraph
    // outlives its caster so a cancel cannot exist. An id is deliberately NOT forgotten on AOI-exit: the client keeps
    // rendering to T anyway (harmless — the decal is off-screen), and keeping it known means exit-and-re-enter
    // mid-windup can't send a duplicate. Cost: the active set is tiny (telegraphs live ~1.5 s) and the whole pass is
    // gated behind PendingCount/KnownTelegraphIds at the call site, so the steady state pays nothing.
    private readonly List<TelegraphScheduler.ActiveTelegraph> _telegraphSyncScratch = [];
    private readonly List<ulong> _telegraphForgetScratch = [];

    private void SyncTelegraphs(ClientSession recipient, WorldEntity recipientEntity)
    {
        // Announce pending telegraphs that are in AOI and unknown to this viewer. The radius test includes the shape's
        // BoundingRadius (the same superset idea the resolve gather uses) so a large shape OVERLAPPING the viewer's
        // interest disc is announced even when its centre sits just outside — a viewer standing inside a circle must
        // never be hit by a telegraph it was never shown.
        _telegraphs.CopyActiveTo(_telegraphSyncScratch);
        foreach (var telegraph in _telegraphSyncScratch)
        {
            if (recipient.KnowsTelegraph(telegraph.Id))
            {
                continue;
            }

            var delta = telegraph.Shape.Origin - recipientEntity.Position;
            var reach = _tuning.InterestRadius + telegraph.Shape.BoundingRadius;
            if (delta.LengthSquared > reach * reach)
            {
                continue;
            }

            // TELEGRAPH T2 REVIEW FOLLOWUP (fairness): remember-known only on a SUCCESSFUL send. A failed send on a
            // surviving session (a transient socket hiccup, not a disconnect) must never permanently mark the
            // viewer as knowing a telegraph it never received — that would be a hit with no warning. Leaving it
            // unknown means this same diff naturally retries the send next AOI pass.
            if (TrySend(
                recipient.Peer,
                new TelegraphMessage(telegraph.Id, telegraph.Shape, telegraph.StartTick, telegraph.ResolveTick),
                DeliveryMethod.ReliableOrdered))
            {
                recipient.RememberKnownTelegraph(telegraph.Id);
            }
        }

        // Forget known ids whose telegraph resolved (left the pending set). Bookkeeping only — nothing is sent.
        _telegraphForgetScratch.Clear();
        foreach (var telegraphId in recipient.KnownTelegraphIds)
        {
            if (!_telegraphs.IsPending(telegraphId))
            {
                _telegraphForgetScratch.Add(telegraphId);
            }
        }

        foreach (var telegraphId in _telegraphForgetScratch)
        {
            recipient.ForgetKnownTelegraph(telegraphId);
        }
    }

    // Whether `target` (a spawner's authored integer tile) is within `interestRadius` (Euclidean, no hysteresis) of
    // the viewer's CONTINUOUS float position. Plain radius test for the spawner marker — it does not need the entity
    // hysteresis (the marker is cheap + reliable, and a sub-tile flicker at the boundary is invisible since the
    // marker is static). CONTINUOUS MIGRATION (Phase 6): the viewer side is now the precise float Position; the
    // spawner stays a tile centre (FromTile) because spawners are authored on integer tiles. So the marker
    // flips on/off at the viewer's true sub-tile distance to the spawner, matching the entity AOI test.
    private static bool IsTileInInterest(WorldVector viewerPosition, TileCoord target, float interestRadius)
    {
        var delta = WorldVector.FromTile(target) - viewerPosition;
        return delta.LengthSquared <= interestRadius * (double)interestRadius;
    }

    private static float SnapshotSortKey(ClientSession recipient, WorldEntity candidate, float distanceSquared)
    {
        if (recipient.EntityId.HasValue && candidate.Id == recipient.EntityId.Value)
        {
            return -1;
        }

        return recipient.WasInLastSnapshot(candidate.NetworkId)
            ? distanceSquared - SnapshotRetentionBonusDistanceSquared
            : distanceSquared;
    }

    private readonly record struct VisibleEntity(WorldEntity Entity, float SortKey);

    private sealed class VisibleEntityComparer : IComparer<VisibleEntity>
    {
        public int Compare(VisibleEntity x, VisibleEntity y)
        {
            var keyComparison = x.SortKey.CompareTo(y.SortKey);
            return keyComparison != 0
                ? keyComparison
                : x.Entity.Id.CompareTo(y.Entity.Id);
        }
    }

    // CONTINUOUS MIGRATION (Phase 6): the PRECISE interest distance is now computed on the entities' continuous
    // float Position (WorldVector, tile units), not the rounded .TileCoord. So an entity's in/out-of-AOI is
    // decided by its TRUE sub-tile distance to the viewer, the thing integer-tile distance could not express.
    // Returned as a float (the radius/hysteresis comparands are float tiles); the underlying double distance is
    // well within float range for any sane interest radius, and the value only feeds the radius² compare + the
    // snapshot sort key. The grid candidate gather stays a coarse rounded-tile superset (see
    // ResolveAoiQueryRadiusTiles) — this float test is the exact filter applied to the gathered candidates.
    private static float DistanceSquared(WorldEntity a, WorldEntity b)
    {
        return (float)(b.Position - a.Position).LengthSquared;
    }

    internal static bool IsEntityInInterest(
        WorldEntity recipientEntity,
        WorldEntity candidate,
        ClientSession recipient,
        float interestRadius)
    {
        if (candidate.Id == recipientEntity.Id)
        {
            return true;
        }

        var distanceSquared = DistanceSquared(recipientEntity, candidate);
        var radiusSquared = interestRadius * interestRadius;
        if (distanceSquared <= radiusSquared)
        {
            return true;
        }

        var exitRadius = interestRadius + InterestExitHysteresisTiles;
        var exitRadiusSquared = exitRadius * exitRadius;
        return recipient.WasInLastSnapshot(candidate.NetworkId) && distanceSquared <= exitRadiusSquared;
    }

    // Tile half-extent an AOI grid query must cover so the rounded-tile cell gather stays a strict SUPERSET of
    // the precise FLOAT interest test (Phase 6). The gather centers on the viewer's ROUNDED TileCoord and the
    // grid keys candidates on their ROUNDED TileCoord, but IsEntityInInterest now measures the EXIT radius
    // (interest + hysteresis) on the continuous float Positions. Bounding the rounding both sides, per axis:
    //   |Tc - Tv| <= |Tc - Pc| + |Pc - Pv| + |Pv - Tv| <= 0.5 + (exit radius) + 0.5 = exitRadius + 1
    // (each round() is at most 0.5 off its true position, and the axis gap never exceeds the Euclidean exit
    // distance). So any entity that can pass the float test lies within Chebyshev (exitRadius + 1) tiles of the
    // viewer's tile. Ceil that: the +1 is the load-bearing margin that the old integer-only gather lacked — a
    // sub-tile-further float candidate could otherwise round into a cell just outside the box and be dropped.
    private static int ResolveAoiQueryRadiusTiles(float interestRadius)
    {
        return (int)Math.Ceiling(interestRadius + InterestExitHysteresisTiles + RoundedGatherMarginTiles);
    }

    // The +1-tile superset margin above: 0.5 from rounding the viewer's continuous position to its gather-center
    // tile, plus 0.5 from rounding each candidate's continuous position to its grid-cell key. Named so the parity
    // test can stay in lockstep with the gather-radius math.
    internal const float RoundedGatherMarginTiles = 1f;

    // Spatial-index cell size (tiles). Pure performance knob — correctness is independent of it (the query
    // expands the cell box to cover the exit radius for whatever cell size is chosen; SpatialAoiParityTests
    // pins the parity at several sizes).
    //
    // AOI-GATHER OVER-SWEEP FIX (the density profile's superlinear pass, todo/N-tick-profile-at-density):
    // cell == radius (the old ceil(radius)) made each viewer query sweep an average ~3.3 cells per axis for
    // the ±(exit+margin) box — at radius 18 that is ~3600 tiles (~22% of the default map) of candidates per
    // viewer per tick, ~2.6× the ideal bounding box, and EVERY entity in the sweep pays the interest test.
    // radius/4 (cell 5 at radius 18) tightens the swept area toward the ideal box (~2450 tiles, ~32% fewer
    // candidates) while keeping the per-query cell-dictionary lookups small (~100). The floor of 2 keeps a
    // tiny admin-set radius from degenerating into per-tile cells (lookup-dominated).
    private static int ResolveEntityGridCellSize(float interestRadius)
    {
        return Math.Max(2, (int)Math.Ceiling(interestRadius / 4f));
    }

    // P0 (monster-behavior architecture, docs/monster-behavior-design.md): load monster TYPES from the loose data
    // manifest at <output>/Content/monsters.json so they can be authored/edited in data without a code build. The
    // loose file is AUTHORITATIVE at runtime (edit it + restart → new monsters); the code-seeded registry is the
    // safety net used when the file is absent OR fails to parse. NOT a ServerOptions field by design — it follows
    // the AppContext.BaseDirectory/Content convention. Server-only STARTUP data: no protocol change.
    // MONSTER-TUNING-SAVE: the single source of the monster manifest path — the file LoadMonsterTypes READS at startup
    // AND HandleSaveMonsterTuning WRITES on Save, so the loaded copy and the saved copy can never drift. AppContext.
    // BaseDirectory/Content/monsters.json is the OUTPUT-dir copy (CopyToOutputDirectory=PreserveNewest); a Saved file
    // survives rebuilds unless the SOURCE manifest is later edited + rebuilt (which re-clobbers it) — fine for the dev
    // loop. We write the OUTPUT copy (not the repo source) deliberately: it is the file a restart actually loads.
    private static string MonsterManifestPath =>
        Path.Combine(AppContext.BaseDirectory, "Content", "monsters.json");

    private static MonsterTypeRegistry LoadMonsterTypes(int tickRate)
    {
        var path = MonsterManifestPath;
        if (File.Exists(path))
        {
            try
            {
                var registry = MonsterTypeRegistry.FromManifestJson(tickRate, File.ReadAllText(path));
                Log.Info($"Loaded {registry.Types.Count} monster type(s) from data manifest: {path}.");
                return registry;
            }
            catch (Exception ex)
            {
                Log.Warn(
                    $"Failed to load monster manifest {path} ({ex.Message}); using the code-seeded defaults.");
            }
        }
        else
        {
            Log.Info($"No monster manifest at {path}; using the code-seeded defaults.");
        }

        return new MonsterTypeRegistry(tickRate);
    }

    // ECOLOGY E1: load the authored region content from the loose data manifest at <output>/Content/ecology.json,
    // mirroring LoadMonsterTypes exactly (loud parse-failure log, code-seeded fallback on missing/malformed file).
    private static string EcologyManifestPath =>
        Path.Combine(AppContext.BaseDirectory, "Content", "ecology.json");

    private static EcologyRegistry LoadEcology()
    {
        var path = EcologyManifestPath;
        if (File.Exists(path))
        {
            try
            {
                var registry = EcologyRegistry.FromManifestJson(File.ReadAllText(path));
                Log.Info($"Loaded {registry.Regions.Count} ecology region(s) from data manifest: {path}.");
                return registry;
            }
            catch (Exception ex)
            {
                Log.Warn(
                    $"Failed to load ecology manifest {path} ({ex.Message}); using the code-seeded defaults.");
            }
        }
        else
        {
            Log.Info($"No ecology manifest at {path}; using the code-seeded defaults.");
        }

        return new EcologyRegistry();
    }

    // ECOLOGY E3 (docs/ecology-v1-design.md D8, §8 E3): restore persisted stock/pressure OVER the K-seed
    // EcologyState just constructed. Blocking on the repository's async LoadAllAsync here (rather than awaiting
    // it from RunAsync) is deliberate: several test suites (RegionSpawnerIntegrationTests, AuthoredWorldTests,
    // EcologyWireTests, ...) construct GameServer directly and drive its test seams WITHOUT ever calling
    // RunAsync, so the load must already be visible right after the constructor returns. This runs exactly once
    // at boot (never on the tick hot path); the table is tiny (a handful of region×type rows); and a console app
    // has no SynchronizationContext to deadlock a blocking wait against — the same reasoning Program.cs already
    // relies on when it awaits the migration runner before this constructor ever runs.
    //
    // Clamp/reject-on-load (the D8 "manifest may have changed K since the save" fork): TrySetStock/TrySetPressure
    // apply the EXACT SAME guard the `/ecology set`/`/ecology pressure` admin commands use — a finite stock is
    // clamped into [Smin, 1.5K] of the CURRENT config (never trusted verbatim), and a non-finite value is
    // REJECTED outright, leaving that region×type at its fresh K-seed rather than poisoning the cell. A row whose
    // region id or type id is no longer authored by the CURRENT manifest is an ORPHAN — ignored, logged, never
    // applied (D8: "rows for regions/types no longer in the manifest are ignored").
    private void LoadEcologyPopulations()
    {
        IReadOnlyList<RegionPopulationRecord> rows;
        try
        {
            rows = _ecologyRepository.LoadAllAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Log.Error("Failed to load persisted ecology populations; every region×type stays at its K-seed.", exception);
            return;
        }

        if (rows.Count == 0)
        {
            return;
        }

        var applied = 0;
        var orphaned = 0;
        foreach (var row in rows)
        {
            if (!_ecology.Registry.TryGet(row.RegionId, out var region) || !region.Types.ContainsKey(row.TypeId))
            {
                orphaned++;
                Log.Warn($"Ignoring orphaned ecology row for '{row.RegionId}'/'{row.TypeId}' (no longer in the ecology manifest).");
                continue;
            }

            // WHOLE-row rejection (E3 review L3): validate BOTH values before applying EITHER — checking only the
            // stock let a finite-stock/non-finite-pressure row half-apply silently, contradicting the stated
            // policy ("an invalid value means the row can't be trusted").
            if (double.IsFinite(row.Stock) && double.IsFinite(row.Pressure)
                && _ecology.TrySetStock(row.RegionId, row.TypeId, row.Stock)
                && _ecology.TrySetPressure(row.RegionId, row.TypeId, row.Pressure))
            {
                applied++;
            }
            else
            {
                Log.Warn($"Rejected corrupt persisted row for '{row.RegionId}'/'{row.TypeId}' (non-finite value); kept its K-seed.");
            }
        }

        Log.Info($"Loaded {applied} persisted ecology row(s) from {rows.Count} saved ({orphaned} orphaned/ignored).");
    }

    // ECOLOGY E3: snapshots every region×type's live stock/pressure ON THE CALLER'S THREAD and returns the save
    // task — the ONE method both the checkpoint cadence and the graceful-shutdown path call. The snapshot MUST
    // happen before any thread hop (E3 review M1): SnapshotAll enumerates cells the tick thread mutates, so
    // snapshotting inside a pool thread read stocks and pressures mid-tick (incoherent cross-cell state, and a
    // theoretical torn double on non-x64 platforms). Only the DB write itself is async.
    private Task SaveEcologyPopulationsAsync(CancellationToken cancellationToken)
    {
        return WriteEcologyRecordsAsync(BuildEcologyRecords(), cancellationToken);
    }

    // The snapshot + record build, ALWAYS on the caller's thread (the tick thread for checkpoints; the post-loop
    // shutdown path for the final save) — the cheap, coherent half the M1 review required. The expensive half
    // (the SQLite write) is what callers route off-thread.
    private List<RegionPopulationRecord> BuildEcologyRecords()
    {
        var snapshot = _ecology.SnapshotAll();
        var tick = (long)_serverTick;
        var records = new List<RegionPopulationRecord>(snapshot.Count);
        foreach (var cell in snapshot)
        {
            records.Add(new RegionPopulationRecord(cell.RegionId, cell.TypeId, cell.Stock, cell.Pressure, tick));
        }

        return records;
    }

    private async Task WriteEcologyRecordsAsync(IReadOnlyList<RegionPopulationRecord> records, CancellationToken cancellationToken)
    {
        try
        {
            await _ecologyRepository.SaveAllAsync(records, cancellationToken);
        }
        catch (Exception exception)
        {
            Log.Error("Failed to save ecology populations.", exception);
        }
    }

    // ECOLOGY E3: fires on the SAME cadence CheckpointDirtyDurableState already uses for character state (D8: "on
    // the existing character-checkpoint cadence") — no separate persistence timer to keep in sync. The snapshot is
    // taken synchronously on the tick thread inside SaveEcologyPopulationsAsync (coherent state, E3 review M1);
    // only the DB write continues asynchronously, so the tick budget never blocks on I/O. The in-flight task is
    // TRACKED (E3 review M2): the shutdown path awaits it before the final save, so a checkpoint that snapshotted
    // during the last ticks can never acquire the SQLite write lock AFTER the final-state commit and leave the DB
    // seconds stale at process exit. No overlap guard beyond that: at the checkpoint cadence (>= 1s, default 15s)
    // a save of this table's tiny row count completes long before the next one fires, and SaveAllAsync is one
    // idempotent keyed-upsert transaction either way.
    private Task _ecologyCheckpointInFlight = Task.CompletedTask;

    private void CheckpointEcologyPopulations()
    {
        // LIVE-DESYNC FIX (2026-07-04): the snapshot + record build stay HERE on the tick thread (coherent state —
        // the M1 review fix), but the DB write MUST hop threads via Task.Run: Microsoft.Data.Sqlite's async APIs
        // complete SYNCHRONOUSLY on the calling thread, so returning WriteEcologyRecordsAsync's task directly ran
        // the whole connection-open + transaction + fsync ON the tick thread every checkpoint — multi-second tick
        // stalls on a slow/AV-intercepted disk, catch-up bursts, and mass no-ack kicks of every live client.
        var records = BuildEcologyRecords();
        _ecologyCheckpointInFlight = Task.Run(() => WriteEcologyRecordsAsync(records, CancellationToken.None));
    }

    // ECOLOGY E2: builds one RegionSpawner per authored region×type via RegionSpawnPlanner's deterministic
    // derivation. An ecology.json type id that doesn't match any loaded MonsterType (a content-authoring
    // mismatch) is skipped with a loud warning rather than crashing boot — the manifest-loading philosophy every
    // other registry in this codebase follows (a typo'd id degrades, it never takes the server down).
    private List<RegionSpawner> BuildRegionSpawners()
    {
        var result = new List<RegionSpawner>();
        // ONE shared road-distance field for the whole zone, reused by every region×type (mirrors the client's
        // DecorPlacer computing its own field once per zone build — see RegionSpawnPlanner's FORK note on why
        // this is a SEPARATE computation, not a shared one).
        var roadDistanceField = RegionSpawnPlanner.ComputeRoadDistanceField(_zone.Authored, _zone.Width, _zone.Height);

        foreach (var region in _ecology.Registry.Regions)
        {
            foreach (var (typeId, config) in region.Types)
            {
                if (!_monsterTypes.TryGet(typeId, out var monsterType))
                {
                    Log.Warn($"Ecology region '{region.Id}' authors unknown monster type '{typeId}'; skipping its region spawner.");
                    continue;
                }

                var targetTileCount = RegionSpawnPlanner.SpawnTileCountFor(config.MaxLive);
                var spawnTiles = RegionSpawnPlanner.DeriveSpawnTiles(
                    _zone.IsWalkable,
                    _zone.Authored,
                    _zone.Width,
                    _zone.Height,
                    roadDistanceField,
                    _zone.Seed,
                    region,
                    typeId,
                    targetTileCount,
                    RegionSpawnPlanner.MinSpacing);

                if (spawnTiles.Count == 0)
                {
                    Log.Warn($"Ecology region '{region.Id}'/{typeId} derived ZERO spawn tiles (region rect outside this zone?) — its RegionSpawner will never spawn.");
                }
                else
                {
                    Log.Info($"Ecology region '{region.Id}'/{typeId} derived {spawnTiles.Count} spawn tile(s) (target {targetTileCount}).");
                }

                result.Add(new RegionSpawner(region.Id, typeId, monsterType, config.MaxLive, spawnTiles));
            }
        }

        return result;
    }

    private static EntityStateSnapshot ToEntityStateSnapshot(WorldEntity entity)
    {
        // COMBAT-S2A: replicate PUBLIC HP (current + max) on the per-entity state for the overhead bar. Only
        // entities that actually HAVE vitals report HP (players + dummies); everything else (resource nodes,
        // any future stat-less kind) replicates 0/0, which the client reads as "no HP" and hides the bar for.
        // WorldEntity.Stats defaults to 100/100 for every kind, so the kind gate — not the value — is what
        // distinguishes "has HP" from "no HP". Mana/stamina are deliberately NOT here (owner-only via
        // PlayerStatsMessage). Clamp to ushort defensively (HP is small and non-negative in practice).
        var (health, maxHealth) = HasPublicHealth(entity.Kind)
            ? (ToHealthWire(entity.Stats.Health), ToHealthWire(entity.Stats.MaxHealth))
            : ((ushort)0, (ushort)0);
        // MIGRATION (Phase 3 Pass A): carry the entity's full continuous Position on the snapshot DTO. The codec
        // still quantizes it to a tile on the wire (v35 unchanged); Pass B sends it continuously. The double sim
        // position is never rounded here — only the wire projection is.
        // MOVEMENT-ACTIONS Phase B1: replicate the authoritative airborne height (design §1.4.5). 0 for every grounded
        // entity (the codec then pays just the +1-byte presence flag); >0 while the entity is mid-jump (the executor
        // drives WorldEntity.VerticalOffset each airborne tick). XY/Position is untouched — the Z rides alongside.
        // REMOTE-WALK Phase 1 (v39): also replicate the authoritative continuous Velocity (units/sec) so a remote
        // client can dead-reckon the entity between sparse snapshots (Phase 2). Zero at rest (the codec then pays no
        // velocity bytes — only the combined flags byte); non-zero while walking. WIRE-ONLY this phase: the client
        // buffers it but does not extrapolate yet.
        // NODE-FIELD N2: the Depleted bit is now ALWAYS false — harvestable nodes replicate their
        // availability via NodeState/NodeStateBatch (global, index-keyed), never as entity state. No entity
        // kind sets this anymore (House/Portal props never did either — this only formalizes it).
        return new EntityStateSnapshot(
            entity.NetworkId, entity.Position, entity.Facing, Depleted: false, health, maxHealth, entity.VerticalOffset, entity.Velocity);
    }

    // Which entity kinds expose a public HP bar. Players, dummies, and (LIVING-ENEMIES P1) roaming Monsters carry
    // CharacterStats that drive the overhead bar; resources and anything else do not (they replicate 0/0 and the
    // client hides the bar — the client gate is purely MaxHealth>0, so adding Monster here is the only touch
    // needed to show its bar).
    private static bool HasPublicHealth(EntityKind kind) =>
        kind is EntityKind.Player or EntityKind.Dummy or EntityKind.Monster;

    private static ushort ToHealthWire(int value) => (ushort)Math.Clamp(value, 0, ushort.MaxValue);

    private static int EstimateEntityStateBytes()
    {
        return EntityStateFixedBytes;
    }

    // Safety bound for the acked baseline (S46). A healthy client acks every snapshot within ~1 RTT, so the
    // oldest-unacked age stays tiny. A wedged/silent-but-connected client never acks; without a bound its
    // per-viewer pending-snapshot ring would grow each tick. Two thresholds, both in ticks:
    //  - RebaselineTicks (~2 s): clear the acked baseline and re-send a complete snapshot. Cheap recovery
    //    if the client is merely lagging its acks; also bounds the pending ring (it is cleared here).
    //  - DisconnectTicks (~8 s): the client is genuinely wedged; drop it. (LiteNetLib's own 15 s
    //    DisconnectTimeout still covers a fully dead peer; this is the faster app-level bound.)
    private uint RebaselineUnackedThresholdTicks => (uint)Math.Max(1, _options.TickRate * 2);
    private uint DisconnectUnackedThresholdTicks => (uint)Math.Max(1, _options.TickRate * 8);

    // Low-rate EMPTY keep-alive cadence (~1 s): the longest a viewer with an empty per-recipient delta goes
    // without being sent SOME snapshot to ack. Well under the 2 s re-baseline / 8 s disconnect bounds, so an
    // idle-but-healthy viewer keeps acking and never trips them. One tiny empty packet/sec/idle-viewer is
    // negligible next to the dense-scene resend the acked baseline removed.
    private uint SnapshotKeepAliveTicks => (uint)Math.Max(1, _options.TickRate);

    private void ApplyUnackedSafetyBound(ClientSession recipient)
    {
        // Disconnect bound first: measured from the last ack (non-resetting), so a client that keeps being
        // re-baselined but never acks is still dropped once total silence passes the larger threshold.
        var silence = recipient.SilenceTicks(_serverTick);
        if (silence >= DisconnectUnackedThresholdTicks)
        {
            Log.Warn($"Disconnecting {recipient.DisplayName} #{recipient.NetworkId}: no snapshot ack for {silence} ticks.");
            _netManager.DisconnectPeer(recipient.Peer);
            recipient.ForceFullRebaseline();
            return;
        }

        // Cheap re-baseline bound: if the pending ring has been growing for the smaller threshold, clear the
        // acked baseline and re-send a complete snapshot. Bounds the per-viewer pending state.
        if (recipient.UnackedAgeTicks(_serverTick) >= RebaselineUnackedThresholdTicks)
        {
            recipient.ForceFullRebaseline();
        }
    }

    private bool TryGetSessionEntity(ClientSession session, out WorldEntity entity)
    {
        if (session.EntityId.HasValue)
        {
            return _zone.World.TryGet(session.EntityId.Value, out entity);
        }

        entity = null!;
        return false;
    }

    private TileCoord ResolveLoginTile(TileCoord tile)
    {
        return _zone.ResolvePlayerSpawnTile(tile);
    }

    // AUTHORED-MAP M3: spawn the authored map's prop MARKERS at boot. `H`/`P` become inert
    // EntityKind.Resource transients whose DisplayName drives the existing client archetype hook
    // ("House" -> casa sprite, "Portal" -> portal mesh; EntityVisualFactory) — Resource because that
    // is the only kind the factory name-routes. House COLLISION is the blocked `#` footprint stamped
    // into the map itself (M1 review F4 — the flood-fill reachability test sees it); the marker tile
    // is just the walkable sprite anchor south of the footprint. No-op on a procedural map (no
    // authored data).
    // NODE-FIELD N2: `T`/`R` pins are no longer spawned here — they are catalogue indices [0, pinCount)
    // now (NodeCatalog.Build's Step 1, D1's pin-stability contract), replicated via NodeState/
    // NodeStateBatch like every other harvestable, not as entities.
    private void SpawnAuthoredProps()
    {
        var authored = _zone.Authored;
        if (authored is null)
        {
            return;
        }

        foreach (var marker in authored.Markers)
        {
            switch (marker.Kind)
            {
                case AuthoredMarkerKind.House:
                    _zone.SpawnTransient(_networkIds.Rent(), EntityKind.Resource, "House", marker.Tile, Direction8.S);
                    break;
                case AuthoredMarkerKind.Portal:
                    _zone.SpawnTransient(_networkIds.Rent(), EntityKind.Resource, "Portal", marker.Tile, Direction8.S);
                    break;
            }
        }
    }

    // COMBAT-S2A: spawn a couple of stationary "Dummy" enemies near the primary spawn so a logged-in player
    // immediately sees a hittable target with a partial red overhead HP bar (the proof the current/max HP
    // replication renders). They are EntityKind.Dummy transients with CharacterStats (HP); their current HP is
    // dev-set to a PARTIAL value so the bar shows a non-full fill. No behaviour/AI/damage/regen this stage —
    // they just stand there and replicate their HP via the snapshot like any other entity. Placement is
    // deterministic: a short Chebyshev-radius scan outward from the first spawn tile for distinct walkable
    // tiles, so the same world regenerates the same dummies on restart (no PRNG, no clock).
    private void SpawnDummies()
    {
        const int dummyCount = 2;
        const int partialHealth = 70; // out of the CharacterStats.Default MaxHealth (100) → a 70% bar.

        var anchor = _zone.SpawnTiles.Count > 0 ? _zone.SpawnTiles[0] : Zone.DefaultSpawnTile;
        var placed = 0;
        // Ring-scan outward from the anchor (radius 2 onward so dummies don't land on the spawn tile itself),
        // taking the first `dummyCount` distinct walkable tiles. Deterministic iteration order = deterministic
        // layout. Bounded radius so a pathological map can't loop forever; if fewer than dummyCount tiles are
        // found, we simply spawn fewer (a target is still present).
        for (var radius = 2; radius <= 6 && placed < dummyCount; radius++)
        {
            for (var dy = -radius; dy <= radius && placed < dummyCount; dy++)
            {
                for (var dx = -radius; dx <= radius && placed < dummyCount; dx++)
                {
                    // Only the ring at this Chebyshev radius (skip the filled interior visited at smaller radii).
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
                    {
                        continue;
                    }

                    var tile = anchor.Offset(dx, dy);
                    if (!_zone.IsWalkable(tile))
                    {
                        continue;
                    }

                    var dummy = _zone.SpawnTransient(_networkIds.Rent(), EntityKind.Dummy, "Dummy", tile, Direction8.S);
                    // Partial HP so the overhead bar is visibly non-full (proves current/max rendering).
                    dummy.TrySetStatCurrent(StatKind.Health, partialHealth);
                    placed++;
                }
            }
        }
    }

    // NODE-FIELD N2: O(depleted) respawn sweep — NodeField.DrainDueRespawns pops only nodes whose respawn
    // tick has arrived; still-available nodes are never visited. Each flip re-broadcasts NodeState
    // (depleted=false) to every session (D4 GLOBAL, not AOI — the un-deplete is as tiny and player-paced as
    // the harvest that caused it).
    private void RespawnNodes()
    {
        _nodeField.DrainDueRespawns(_serverTick, index => BroadcastNodeState(index, depleted: false));
    }

    // COMBAT-QOL: heal stationary enemy targets (Dummy/Npc) toward MaxHealth at a HEAVY per-tick rate so a hit dummy
    // refills fast and stays a permanent test target. Gated on the SAME kinds that can TAKE damage
    // (CombatTargeting.IsAttackableEnemy) so we never "regen" a player or a resource. TryRegenHealth clamps at max,
    // no-ops at full (so a healthy dummy costs nothing), and bumps StateRevision only on a real change — the refilled
    // HP rides the existing snapshot HP field, so the overhead bar fills automatically. NO DamageEventMessage is ever
    // emitted here: only real damage floats a number. Iterates the live entity collection directly (no per-tick
    // allocation); the count is tiny (a couple of dummies) so the linear scan is negligible.
    private void RegenEnemies()
    {
        var perTick = _tuning.EnemyRegenPerTick;
        if (perTick <= 0)
        {
            return;
        }

        foreach (var entity in _zone.World.Entities)
        {
            // LIVING-ENEMIES P1: regen the STATIONARY targets only (Dummy/Npc), NOT roaming Monsters — a Monster
            // is attackable but does not heal back this phase (its HP depletes and stays). Gate on the narrower
            // IsRegeneratingEnemy, not IsAttackableEnemy (which now also includes Monster).
            if (CombatTargeting.IsRegeneratingEnemy(entity))
            {
                entity.TryRegenHealth(perTick);
            }
        }
    }

    // NODE-FIELD N2: server-authoritative resolution of the generic Interact verb — CORPSE-OPEN ONLY now
    // (D5: harvestable nodes moved to the dedicated index-keyed HarvestNodeMessage/HandleHarvestNode below;
    // they are no longer WorldEntities an InteractRequest can target). House/Portal props and any other
    // visible entity kind fall through to the SAME "not_resource" reply the entity-based harvest path always
    // gave a non-node target, so a legacy/mis-clicked InteractRequest fails exactly as it always has.
    // Rate-limited like other client input.
    private void HandleInteract(ClientSession session, uint targetNetworkId)
    {
        if (!session.TryConsumeInteract(_serverTick))
        {
            SendInteractResult(session, false, "rate_limited");
            return;
        }

        if (!TryGetSessionEntity(session, out var actor))
        {
            SendInteractResult(session, false, "no_actor");
            return;
        }

        if (!TryFindVisibleEntity(session, actor, targetNetworkId, out var target))
        {
            SendInteractResult(session, false, "no_target");
            return;
        }

        // LOOT P4c: interacting with a CORPSE OPENS the loot window (eligibility-gated), NOT a resource harvest and
        // no longer an immediate loot-all (P4b). The window's buttons then drive take-item / loot-all / close via
        // LootActionMessage.
        if (target.Kind == EntityKind.Corpse)
        {
            HandleCorpseOpen(session, actor, target);
            return;
        }

        SendInteractResult(session, false, "not_resource");
    }

    // NODE-FIELD N2 (docs/node-field-design.md D5): harvest a catalogue node by INDEX — the node-field
    // analogue of HandleInteract's former resource-harvest branch, now keyed by index instead of an entity.
    // Validates: rate limit (shares the interact cooldown — a harvest is exactly as spammy as the old entity
    // interact was), a live actor, the index is in range, the node is available, the actor is within the
    // SAME interaction reach every other harvest/loot verb uses, and the actor has an inventory. On success:
    // award the per-NodeType yield (the SAME ResourceNodeRegistry content/respawn-ticks the entity path
    // used), mark depleted + schedule respawn, broadcast the flip to every session (D4 GLOBAL, not AOI), and
    // reply + push the inventory delta to the owner (unchanged reply shape/reason strings, reused verbatim).
    private void HandleHarvestNode(ClientSession session, ushort nodeIndex)
    {
        if (!session.TryConsumeInteract(_serverTick))
        {
            SendInteractResult(session, false, "rate_limited");
            return;
        }

        if (!TryGetSessionEntity(session, out var actor))
        {
            SendInteractResult(session, false, "no_actor");
            return;
        }

        if (!_nodeField.IsValidIndex(nodeIndex))
        {
            SendInteractResult(session, false, "no_target");
            return;
        }

        if (_nodeField.IsDepleted(nodeIndex))
        {
            SendInteractResult(session, false, "depleted");
            return;
        }

        var entry = _nodeField.EntryAt(nodeIndex);
        if (!IsWithinNodeInteractionRange(actor, entry.Tile))
        {
            SendInteractResult(session, false, "too_far");
            return;
        }

        if (actor.Inventory is null)
        {
            SendInteractResult(session, false, "no_inventory");
            return;
        }

        var definition = _resourceNodes.Get(NodeTypeKey(entry.NodeType));
        var added = actor.Inventory.TryAdd(definition.YieldItemKey, definition.YieldQuantity);
        if (added <= 0)
        {
            // Inventory full for this item (or unknown yield): do not deplete a node for nothing.
            SendInteractResult(session, false, "inventory_full");
            return;
        }

        _nodeField.Deplete(nodeIndex, _serverTick, definition.RespawnTicks);
        BroadcastNodeState(nodeIndex, depleted: true);
        SendInteractResult(session, true, "");
        SendInventoryUpdate(session, [new ItemStack(definition.YieldItemKey, actor.Inventory.QuantityOf(definition.YieldItemKey))]);
    }

    // NODE-FIELD N2: the harvestable-type key ResourceNodeRegistry was seeded with (ResourceNodeRegistry
    // .CreateDefault's "tree"/"rock"/"plant" keys, unchanged since S37/S38 — N2 does not invent new node
    // kinds, only relocates where instances live). Explicit switch (not a cast/ToString), mirroring
    // EcologyWire.ToWireState: a future reordering of NodeType fails to COMPILE here rather than silently
    // mis-keying the registry lookup.
    private static string NodeTypeKey(NodeType type) => type switch
    {
        NodeType.Tree => "tree",
        NodeType.Rock => "rock",
        NodeType.Plant => "plant",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown NodeType."),
    };

    // LOOT P4c: OPEN the loot window on a CORPSE the actor walked up to (an InteractRequest on a corpse). Gates:
    // adjacency (like a harvest) + the actor must be in the corpse's eligibleLooters (the contribution ledger's
    // contributors — solo = the killer). On success it records the open-loot pairing on the session and ships the
    // corpse's current contents (CorpseContents, Open=true) so the client shows the rarity-coloured window. No items
    // move here — taking is a separate LootAction the window's buttons send. A non-eligible / out-of-range / gone
    // corpse is rejected with the same machine reason the harvest path uses.
    private void HandleCorpseOpen(ClientSession session, WorldEntity actor, WorldEntity corpseEntity)
    {
        if (!_corpses.TryGetValue(corpseEntity.Id, out var corpse))
        {
            // Corpse kind but no live loot payload (already looted/decayed this tick, or a stale target): treat as gone.
            SendInteractResult(session, false, "no_target");
            return;
        }

        if (!IsInInteractionRange(actor, corpseEntity))
        {
            SendInteractResult(session, false, "too_far");
            return;
        }

        if (actor.CharacterId is not { } looterId || !corpse.IsEligible(looterId))
        {
            // Not a contributor to the kill (or a non-durable actor with no character id): rejected, window not opened.
            SendInteractResult(session, false, "not_eligible");
            return;
        }

        // Remember which corpse this session has open so a LootAction routes here and a despawn can close the window.
        session.SetOpenCorpse(corpseEntity.Id);
        SendInteractResult(session, true, "");
        SendCorpseContents(session, corpseEntity.NetworkId, corpse);
    }

    // LOOT P4c: a loot-window verb (take one stack / take all / close) on the corpse the session has OPEN. The action
    // is routed by the session's OpenCorpseEntityId (not blind trust of the client's network id — a stale window can't
    // loot a different corpse): the message's CorpseNetworkId must match the open corpse's network id. Close just drops
    // the pairing. Take/LootAll re-validate the corpse still exists + adjacency + eligibility (the player may have
    // walked away or the corpse decayed), transfer via the Corpse primitives, push the inventory delta + toast + the
    // refreshed CorpseContents, and despawn the corpse INSTANTLY if the action emptied it (no lingering empty body).
    private void HandleLootAction(ClientSession session, uint corpseNetworkId, LootActionKind kind, string templateKey)
    {
        // Close: drop the pairing only if it matches (a close for a different/stale corpse is harmless). No reply
        // needed — the client closed its own window; this just releases the server-side pairing.
        if (kind == LootActionKind.Close)
        {
            session.SetOpenCorpse(null);
            return;
        }

        if (session.OpenCorpseEntityId is not { } openCorpseId)
        {
            // No window open server-side (already closed / never opened): ignore — the client's window is stale.
            return;
        }

        if (!_corpses.TryGetValue(openCorpseId, out var corpse)
            || !_zone.World.TryGet(openCorpseId, out var corpseEntity)
            || corpseEntity.NetworkId != corpseNetworkId)
        {
            // The open corpse is gone (decayed/despawned) or the message targets a different corpse than the one this
            // session has open. Close the (now invalid) window and forget the pairing.
            session.SetOpenCorpse(null);
            SendCorpseClosed(session, corpseNetworkId);
            return;
        }

        if (!TryGetSessionEntity(session, out var actor))
        {
            return;
        }

        if (!IsInInteractionRange(actor, corpseEntity))
        {
            // Walked out of range with the window open: close it (the client drops the panel on this Open=false).
            session.SetOpenCorpse(null);
            SendCorpseClosed(session, corpseEntity.NetworkId);
            return;
        }

        if (actor.CharacterId is not { } looterId || !corpse.IsEligible(looterId) || actor.Inventory is null)
        {
            // Eligibility can't change for a live corpse, but re-gate defensively (and a missing inventory is a no-op).
            return;
        }

        if (kind == LootActionKind.TakeItem)
        {
            var take = corpse.TryTakeItem(templateKey, actor.Inventory);
            if (take.Took)
            {
                SendInventoryUpdate(session, [new ItemStack(take.Transferred.TemplateKey, actor.Inventory.QuantityOf(take.Transferred.TemplateKey))]);
                SendSystem(session, $"Looted: {FormatLootSummary([take.Transferred])}.");
            }

            FinishLootAction(session, corpseEntity, corpse, take.CorpseEmptied);
            return;
        }

        // LootAll.
        var result = corpse.TryLootAll(actor.Inventory);
        if (result.Looted)
        {
            var changed = new List<ItemStack>(result.Transferred.Count);
            foreach (var moved in result.Transferred)
            {
                changed.Add(new ItemStack(moved.TemplateKey, actor.Inventory.QuantityOf(moved.TemplateKey)));
            }

            SendInventoryUpdate(session, changed);
            SendSystem(session, $"Looted: {FormatLootSummary(result.Transferred)}.");
        }

        FinishLootAction(session, corpseEntity, corpse, result.CorpseEmptied);
    }

    // LOOT P4c: after a take/loot-all, either despawn the now-empty corpse INSTANTLY (which closes the window via
    // SendCorpseClosed + forgets the pairing) or refresh the still-open window with the remaining contents. Shared by
    // the take-item and loot-all paths so "empty => instant despawn, no lingering body" is one place.
    private void FinishLootAction(ClientSession session, WorldEntity corpseEntity, Corpse corpse, bool corpseEmptied)
    {
        if (corpseEmptied)
        {
            // Instant despawn the moment the last item leaves (parity with the old grab-all path): no empty corpse ever
            // sits on the ground waiting for decay. DespawnCorpse closes the window for every session that had it open.
            DespawnCorpse(corpseEntity.Id);
            return;
        }

        // Still has loot: refresh the open window with what remains.
        SendCorpseContents(session, corpseEntity.NetworkId, corpse);
    }

    // LOOT P4c: ship an OPEN corpse's current contents to the owner so the loot window shows/refreshes (rarity-coloured).
    // Resolves each stack's rarity from the item registry (Common for an unknown key — defensive). DisplayName is NOT
    // sent; the client resolves it from its own registry (falling back to the key), keeping the wire thin.
    private void SendCorpseContents(ClientSession session, uint corpseNetworkId, Corpse corpse)
    {
        var contents = corpse.Contents;
        var items = new List<CorpseItem>(contents.Count);
        foreach (var stack in contents)
        {
            var rarity = _itemRegistry.TryGet(stack.TemplateKey, out var definition) ? definition.Rarity : Rarity.Common;
            items.Add(new CorpseItem(stack.TemplateKey, stack.Quantity, rarity));
        }

        TrySend(session.Peer, new CorpseContentsMessage(corpseNetworkId, true, items), DeliveryMethod.ReliableOrdered);
    }

    // LOOT P4c: tell the client to CLOSE the loot window for a corpse (Open=false, empty items) — the corpse emptied,
    // decayed, despawned, or the player walked out of range. Reliable-ordered so a close is never lost (a stuck-open
    // window would let the client send LootActions the server now rejects, but never loot — still, close cleanly).
    private void SendCorpseClosed(ClientSession session, uint corpseNetworkId)
    {
        TrySend(session.Peer, new CorpseContentsMessage(corpseNetworkId, false, []), DeliveryMethod.ReliableOrdered);
    }

    // LOOT P4b: formats a looted-stacks list into a human "2x Slime Gel, 1x Arcane Dust" toast, using the item
    // registry's display names (falling back to the raw key for an unknown template — defensive only).
    private string FormatLootSummary(IReadOnlyList<ItemStack> stacks)
    {
        var summary = new StringBuilder();
        for (var i = 0; i < stacks.Count; i++)
        {
            if (i > 0)
            {
                summary.Append(", ");
            }

            var name = _itemRegistry.TryGet(stacks[i].TemplateKey, out var definition)
                ? definition.DisplayName
                : stacks[i].TemplateKey;
            summary.Append(stacks[i].Quantity).Append("x ").Append(name);
        }

        return summary.ToString();
    }

    // Resolves a target network id to a world entity the requester can actually see (AOI is the security
    // boundary: a client may never interact with something outside its interest set). The actor itself
    // is always visible. Mirrors the AOI test used for snapshots so interaction and replication agree.
    private bool TryFindVisibleEntity(ClientSession session, WorldEntity actor, uint targetNetworkId, out WorldEntity target)
    {
        // Route interaction visibility through the SAME spatial index as snapshot AOI so "can replicate"
        // and "can interact" can never diverge (S38/S41). The grid returns a superset of the actor's
        // interest set; the exact IsEntityInInterest test below is the security boundary. A target outside
        // the query neighborhood is necessarily outside the interest radius too, so it would fail the test
        // anyway — same result as the old full scan, but without scanning every entity.
        _zone.World.GatherInterestCandidates(actor.TileCoord, _aoiQueryRadiusTiles, _aoiInteractCandidateScratch);
        foreach (var candidate in _aoiInteractCandidateScratch)
        {
            if (candidate.NetworkId != targetNetworkId)
            {
                continue;
            }

            if (IsEntityInInterest(actor, candidate, session, _tuning.InterestRadius))
            {
                target = candidate;
                return true;
            }

            break;
        }

        target = null!;
        return false;
    }

    private void SendInteractResult(ClientSession session, bool success, string reason)
    {
        TrySend(session.Peer, new InteractResultMessage(success, reason), DeliveryMethod.ReliableOrdered);
    }

    private void SendInventoryUpdate(ClientSession session, IReadOnlyList<ItemStack> changedStacks)
    {
        TrySend(session.Peer, new InventoryUpdateMessage(changedStacks), DeliveryMethod.ReliableOrdered);
    }

    // Adjacency = Chebyshev distance <= 1 tile, so a player standing on or in any of the 8 tiles around
    // a node (matching 8-directional movement) may harvest it.
    // CONTINUOUS MIGRATION (Phase 9): the interact REACH gate (harvest a node / open + loot a corpse). Floats the
    // former tile Chebyshev <= 1 adjacency to a Euclidean distance on the CONTINUOUS positions — same int->float
    // pattern as Phase 6 (AOI) and Phase 7 (combat). The actor moves off-grid, so its sub-tile offset now counts;
    // the target (resource node / corpse) is still authored on a tile centre, so target.Position is that centre.
    // The radius (InteractionTuning.InteractionRadiusUnits, 1.5) is SHARED with the client's HarvestTargeting so
    // the player sees harvestable exactly what this gate accepts; compared squared to skip the sqrt.
    private static bool IsInInteractionRange(WorldEntity actor, WorldEntity target)
    {
        return (actor.Position - target.Position).LengthSquared <= InteractionTuning.InteractionRadiusUnitsSquared;
    }

    // NODE-FIELD N2: the SAME reach gate as IsInInteractionRange, but against a catalogue TILE CENTRE —
    // harvestable nodes have no WorldEntity/Position anymore, so this compares against
    // WorldVector.FromTile(nodeTile) instead of another entity's Position.
    private static bool IsWithinNodeInteractionRange(WorldEntity actor, TileCoord nodeTile)
    {
        return (actor.Position - WorldVector.FromTile(nodeTile)).LengthSquared <= InteractionTuning.InteractionRadiusUnitsSquared;
    }

    private ZoneInfoMessage CreateZoneInfoMessage()
    {
        // Ship the seed, not the tiles: the client regenerates the identical map locally via the shared
        // deterministic generator. ContentHash is computed over the same canonically-ordered set the
        // generator emits, so the client can compare against its own regeneration (drift/tamper check).
        var contentHash = TerrainGenerator.ContentHash(_zone.Width, _zone.Height, _zone.Seed, _zone.GenVersion);
        // NODE-FIELD N2 (D2): CatalogHash rides alongside, the same drift-guard discipline over the shared
        // NodeCatalog the client independently builds.
        return new ZoneInfoMessage(_zone.Id, _zone.Width, _zone.Height, _zone.Seed, _zone.GenVersion, contentHash, _nodeCatalog.CatalogHash);
    }

    private void HandleChat(ClientSession sender, string text)
    {
        var safeText = text.Trim();
        if (safeText.StartsWith("/", StringComparison.Ordinal))
        {
            HandleCommand(sender, safeText);
            return;
        }

        BroadcastChat(sender, safeText);
    }

    private void BroadcastChat(ClientSession sender, string text)
    {
        var safeText = text.Trim();
        if (safeText.Length == 0)
        {
            return;
        }

        if (safeText.Length > 240)
        {
            safeText = safeText[..240];
        }

        var broadcast = new ChatBroadcastMessage(sender.DisplayName, safeText);
        foreach (var session in _sessions.Values)
        {
            if (session.IsAuthenticated && session.ZoneId == sender.ZoneId)
            {
                TrySend(session.Peer, broadcast, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    private void HandleCommand(ClientSession sender, string commandLine)
    {
        var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts.Length == 0 ? "" : parts[0].TrimStart('/').ToLowerInvariant();

        if (command is "help" or "?")
        {
            SendSystem(sender, sender.Role == ClientRole.Admin
                ? "commands: /help, /role, /rumors, /pair <name>, /unpair, /boss, /who, /metrics, /speed <multiplier>, /monster [name], /slam [radius] [windupMs] [damage], /clearspawners, /ecology [set <region> <type> <stock> | pressure <region> <type> <n>], /stress, /stress status, /stress start [clients] [duration], /stress stop"
                : "commands: /help, /role, /rumors, /pair <name>, /unpair, /boss. Admin commands require role Admin.");
            return;
        }

        if (command == "role")
        {
            SendSystem(sender, $"role: {sender.Role}");
            return;
        }

        // ECOLOGY E4 (D6b): /rumors is available to EVERY player, not just admins — resolved BEFORE the admin
        // gate below, like /help and /role.
        if (command == "rumors")
        {
            HandleRumorsCommand(sender);
            return;
        }

        // DUO-SKILLSHOT (exp/duo-abilities): /pair <name> and /unpair are co-op gameplay verbs — available to EVERY
        // player (resolved BEFORE the admin gate, like /rumors), not admin dev tools.
        if (command == "pair")
        {
            HandlePairCommand(sender, parts);
            return;
        }

        if (command == "unpair")
        {
            HandleUnpairCommand(sender);
            return;
        }

        // BOSS-1 (docs/boss-encounter-sunderer-design.md): /boss is a co-op gameplay verb — available to EVERY player
        // (resolved BEFORE the admin gate, like /pair). Outside the arena it enters (pulls in a duo partner too);
        // inside, it leaves.
        if (command == "boss")
        {
            HandleBossCommand(sender);
            return;
        }

        if (sender.Role != ClientRole.Admin)
        {
            SendSystem(sender, "command denied: role Admin required.");
            Log.Warn($"Denied command from {sender.DisplayName}: {commandLine}");
            return;
        }

        switch (command)
        {
            case "who":
                SendSystem(sender, FormatWho());
                break;
            case "metrics":
                SendSystem(sender, _metrics.FormatStateSummary(
                    _sessions.Count,
                    _sessions.Values.Count(session => session.IsAuthenticated),
                    _serverTick,
                    _syntheticLoad.Status()));
                SendSystem(sender, _metrics.FormatWindowSummary(TimeSpan.FromSeconds(5)));
                SendSystem(sender, _metrics.FormatWindowSummary(TimeSpan.FromSeconds(60)));
                SendSystem(sender, _metrics.FormatTotalSummary());
                SendSystem(sender, _metrics.FormatMessageSummary());
                break;
            case "stress":
                HandleStressCommand(sender, parts);
                break;
            case "speed":
                HandleSpeedCommand(sender, parts);
                break;
            case "monster":
                HandleMonsterCommand(sender, parts);
                break;
            case "slam":
                HandleSlamCommand(sender, parts);
                break;
            case "clearspawners":
                HandleClearSpawnersCommand(sender);
                break;
            case "ecology":
                HandleEcologyCommand(sender, parts);
                break;
            default:
                SendSystem(sender, $"unknown command: /{command}. Try /help.");
                break;
        }
    }

    private void HandleStressCommand(ClientSession sender, string[] parts)
    {
        var subcommand = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "start";
        switch (subcommand)
        {
            case "status":
                SendSystem(sender, _syntheticLoad.Status());
                break;
            case "stop":
                SendSystem(sender, _syntheticLoad.Stop());
                Log.Info($"{sender.DisplayName} stopped synthetic load.");
                break;
            case "start":
                StartSyntheticLoad(sender, parts);
                break;
            default:
                SendSystem(sender, $"usage: /stress | /stress status | /stress start [clients] [duration] | /stress stop. Default: /stress start {DefaultStressClientCount} {FormatDuration(DefaultStressDuration)}.");
                break;
        }
    }

    private void StartSyntheticLoad(ClientSession sender, string[] parts)
    {
        const int maxClients = 200;
        var clientCount = parts.Length >= 3 && int.TryParse(parts[2], out var parsedCount)
            ? parsedCount
            : DefaultStressClientCount;
        clientCount = Math.Clamp(clientCount, 1, maxClients);

        var duration = parts.Length >= 4 && TryParseDuration(parts[3], out var parsedDuration)
            ? parsedDuration
            : DefaultStressDuration;
        duration = ClampDuration(duration, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10));

        // Pass a live base-speed Func so the bots' local dead-reckon tracks continuous.baseMoveSpeed changes.
        _syntheticLoad.Start(clientCount, duration, _options.Port, _options.ConnectionKey, () => _tuning.BaseMoveSpeedUnitsPerSecond);
        SendSystem(sender, $"stress started: clients={clientCount}, duration={FormatDuration(duration)}.");
        Log.Info($"{sender.DisplayName} started synthetic load: clients={clientCount}, duration={FormatDuration(duration)}.");
    }

    // Admin dev command (v1 way to exercise S51): /speed <multiplier> sets the CALLER's own entity speed
    // multiplier, the server recomputes its effective cadence, and a MovementSpeedChanged is replicated to
    // every viewer whose AOI currently includes the caller. /speed 1 resets to the base cadence. Item/buff-
    // driven speed is a separate follow-up; this command is just the hook to see varying cadence end-to-end.
    private void HandleSpeedCommand(ClientSession sender, string[] parts)
    {
        if (parts.Length < 2 ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var multiplier))
        {
            SendSystem(sender, "usage: /speed <multiplier> (e.g. /speed 2, /speed 0.5, /speed 1 to reset).");
            return;
        }

        if (!double.IsFinite(multiplier) || multiplier <= 0)
        {
            SendSystem(sender, "speed: multiplier must be a positive number.");
            return;
        }

        if (!TryGetSessionEntity(sender, out var entity))
        {
            SendSystem(sender, "speed: no controllable entity.");
            return;
        }

        var changed = entity.TrySetSpeedMultiplier(multiplier);
        // Keep the player's tiles/sec speed stat tracking the multiplier — LIVE in Phase 1 (the integrator reads it).
        RefreshSpeedStat(entity);
        var effectiveMs = EffectiveStepCooldownMs(entity);
        if (changed)
        {
            BroadcastMovementSpeedChanged(entity, effectiveMs);
        }

        SendSystem(
            sender,
            $"speed: multiplier={entity.SpeedMultiplier:0.###}, effective step cooldown={effectiveMs}ms"
                + (changed ? "." : " (unchanged)."));
        Log.Info($"{sender.DisplayName} set speed multiplier {entity.SpeedMultiplier:0.###} (cooldown={effectiveMs}ms).");
    }

    // TELEGRAPH T1: admin dev command /slam [radius] [windupMs] [damage] — force-schedule a circle telegraph at the
    // CALLER's own current position (caster = the caller's entity) so the schedule→resolve engine can be exercised
    // live without a monster. Defaults mirror the slime's authored slam (r=2, 1500 ms, 15). The caller is a player
    // standing at the locked origin, so standing still eats the hit at the resolve tick and stepping/dodge-rolling
    // out escapes it — exactly the dodgeability the tool exists to demonstrate. NO rendering this phase (T2): the
    // confirmation message + the resolve-time damage number are how the result is observed.
    private void HandleSlamCommand(ClientSession sender, string[] parts)
    {
        const string usage = "usage: /slam [radius] [windupMs] [damage] (e.g. /slam 2 1500 15).";
        if (!TryGetSessionEntity(sender, out var actor))
        {
            SendSystem(sender, "slam: no controllable entity.");
            return;
        }

        var radius = 2.0d;
        if (parts.Length >= 2
            && (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out radius)
                || !double.IsFinite(radius) || radius <= 0))
        {
            SendSystem(sender, usage);
            return;
        }

        // T1-review followup: clamp to the SAME ceiling the manifest/F1 path enforces. Unclamped, a fat-fingered
        // /slam 100000 makes the resolve-tick spatial gather iterate ~(2R/cell)^2 cells inside the tick loop (a
        // multi-second single-thread stall), and an absurd radius overflows the gather's (int)Ceiling into a
        // 1-tile query that silently MISSES — the admin dev tool must not be the one unbounded radius source.
        radius = Math.Min(radius, MonsterTypeRegistry.MaxSlamRadiusUnits);

        var windupMs = 1500;
        if (parts.Length >= 3 && (!int.TryParse(parts[2], out windupMs) || windupMs < 0))
        {
            SendSystem(sender, usage);
            return;
        }

        var damage = 15;
        if (parts.Length >= 4 && (!int.TryParse(parts[3], out damage) || damage <= 0))
        {
            SendSystem(sender, usage);
            return;
        }

        // Tick-quantised windup (Ceiling, >= 1 — the cooldown convention, so even /slam 2 0 resolves NEXT tick, never
        // the same one it was scheduled on).
        var windupTicks = (uint)Math.Max(1, (int)Math.Ceiling(windupMs / (1000d / _options.TickRate)));
        var resolveTick = _serverTick + windupTicks;
        var telegraphId = _telegraphs.Schedule(
            actor.Id,
            TelegraphShape.Circle(actor.Position, radius),
            _serverTick,
            resolveTick,
            damage,
            $"/slam by {sender.DisplayName}");

        SendSystem(
            sender,
            $"slam: telegraph #{telegraphId} at {actor.Position.X:0.##},{actor.Position.Y:0.##} r={radius}, "
                + $"resolves at tick {resolveTick} (~{windupMs}ms), damage={damage}.");
        Log.Info($"{sender.DisplayName} scheduled /slam #{telegraphId} r={radius} windup={windupMs}ms damage={damage} (resolve tick {resolveTick}).");
    }

    // DUO-SKILLSHOT (exp/duo-abilities): /pair <displayName> — establish a MUTUAL pair with another online player (the
    // FOUNDATION seam abilities 2-4 consume). Either partner can /unpair; a disconnect unpairs. The pair links the two
    // sessions symmetrically; PairStatus replicates each partner's network id to BOTH clients so they can draw the
    // intercept previews. Available to every player (dispatched before the admin gate). Rejects: no name, self, a name
    // that isn't a distinct online player, or a target already paired to someone else. Re-pairing breaks the sender's
    // (and, if free, the target's) previous pair first.
    private void HandlePairCommand(ClientSession sender, string[] parts)
    {
        if (parts.Length < 2)
        {
            SendSystem(sender, "usage: /pair <displayName>.");
            return;
        }

        // The display name may contain spaces (join the remaining parts) — match case-insensitively.
        var targetName = string.Join(' ', parts, 1, parts.Length - 1).Trim();
        if (string.Equals(targetName, sender.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            SendSystem(sender, "pair: you cannot pair with yourself.");
            return;
        }

        ClientSession? target = null;
        foreach (var session in _sessions.Values)
        {
            if (session.IsAuthenticated && !ReferenceEquals(session, sender)
                && string.Equals(session.DisplayName, targetName, StringComparison.OrdinalIgnoreCase))
            {
                target = session;
                break;
            }
        }

        if (target is null)
        {
            SendSystem(sender, $"pair: no online player named '{targetName}'.");
            return;
        }

        // Already paired to each other — nothing to do.
        if (ReferenceEquals(sender.PartnerSession, target) && ReferenceEquals(target.PartnerSession, sender))
        {
            SendSystem(sender, $"pair: already paired with {target.DisplayName}.");
            return;
        }

        if (target.HasPartner && !ReferenceEquals(target.PartnerSession, sender))
        {
            SendSystem(sender, $"pair: {target.DisplayName} is already paired with someone else.");
            return;
        }

        // Break the sender's prior pair (if any) so a player is only ever in one pair; notify the jilted partner.
        BreakPair(sender);

        sender.SetPartner(target);
        target.SetPartner(sender);
        SendPairStatus(sender, target);
        SendPairStatus(target, sender);
        SendSystem(sender, $"paired with {target.DisplayName}.");
        SendSystem(target, $"paired with {sender.DisplayName}.");
        Log.Info($"Paired {sender.DisplayName} <-> {target.DisplayName}.");
    }

    // DUO-SKILLSHOT: /unpair — break the sender's current pair (if any), notifying + updating both sides.
    private void HandleUnpairCommand(ClientSession sender)
    {
        if (!sender.HasPartner)
        {
            SendSystem(sender, "unpair: you are not paired.");
            return;
        }

        var partnerName = sender.PartnerSession!.DisplayName;
        BreakPair(sender);
        SendSystem(sender, $"unpaired from {partnerName}.");
    }

    // DUO-SKILLSHOT: break `session`'s pair (if any), clearing BOTH sides and pushing the updated PairStatus (a
    // Paired=false to the surviving partner, and to `session` itself so its client clears the partner). A no-op when
    // unpaired. Shared by /unpair, re-pairing, disconnect, and kick — the single pair-teardown funnel.
    private void BreakPair(ClientSession session)
    {
        var partner = session.PartnerSession;
        if (partner is null)
        {
            return;
        }

        // DUO-WAVE2: a broken pair drops any active tether + in-progress detonation between the two (one call catches
        // both, since they involve the pair). Done BEFORE the links clear so the status relays still resolve sessions.
        if (session.EntityId is { } sessionEntityId)
        {
            TearDownDuoAbilities(sessionEntityId);
        }
        else if (partner.EntityId is { } partnerEntityId)
        {
            TearDownDuoAbilities(partnerEntityId);
        }

        session.SetPartner(null);
        partner.SetPartner(null);
        SendPairStatusCleared(session);
        SendPairStatusCleared(partner);
        SendSystem(partner, $"{session.DisplayName} unpaired.");
        Log.Info($"Unpaired {session.DisplayName} <-> {partner.DisplayName}.");
    }

    // DUO-SKILLSHOT: replicate the paired state to `recipient` — the partner's current network id + Paired=true.
    private void SendPairStatus(ClientSession recipient, ClientSession partner)
    {
        TrySend(recipient.Peer, new PairStatusMessage(partner.NetworkId, true), DeliveryMethod.ReliableOrdered);
    }

    // DUO-SKILLSHOT: replicate the unpaired state to `recipient` (Paired=false; partner id irrelevant).
    private void SendPairStatusCleared(ClientSession recipient)
    {
        TrySend(recipient.Peer, new PairStatusMessage(0u, false), DeliveryMethod.ReliableOrdered);
    }

    // DUO-SKILLSHOT: fire a fusion skillshot from the caller toward `aimAngle`. Dedup on the session's DEDICATED fire
    // cursor (independent of move/attack/action), resolve the shooter entity, and hand a solo projectile to the engine
    // (it owns the flight/fusion/hit). No per-entity cooldown this experiment — the fire cadence is the client's key
    // press. Solo shots fire regardless of pairing; pairing only gates whether two shots FUSE (engine-side).
    private void HandleFireSkillshot(ClientSession session, uint sequence, ushort aimAngle)
    {
        if (!session.TryConsumeFireSequence(sequence))
        {
            return;
        }

        if (!TryGetSessionEntity(session, out var shooter))
        {
            return;
        }

        var aimDir = AimAngle.ToUnitVector(aimAngle);
        _skillshots.Fire(shooter.Id, shooter.CharacterId ?? Guid.Empty, shooter.Position, aimDir);
    }

    // DUO-SKILLSHOT: relay a shooter's aim-preview to its PARTNER only. The sender streams these ~8Hz while holding the
    // fire key (client-throttled, and only while a partner exists); the server stamps the sender's network id so the
    // partner draws the faint intercept-preview line from the sender's position along the heading. No projectile/state
    // change — pure relay. Dropped silently when the sender has no partner (the client shouldn't send in that case).
    private void HandleAimPreview(ClientSession session, ushort heading, bool active)
    {
        var partner = session.PartnerSession;
        if (partner is null || !partner.IsAuthenticated)
        {
            return;
        }

        TrySend(partner.Peer, new AimPreviewMessage(session.NetworkId, heading, active), DeliveryMethod.Unreliable);
    }

    // ---- DUO-SKILLSHOT engine seams ----

    // Spawn the replicated projectile WorldEntity, set its constant flight velocity (so remote clients extrapolate
    // smoothly between the sparse tile-cross snapshot updates), zero its vitals (no HP bar), record its tier for the
    // visual replication, and return its entity id. Facing follows the velocity heading. Rents a network id like any
    // transient spawn.
    private ulong SpawnProjectileEntity(WorldVector position, WorldVector velocity, ProjectileTier tier)
    {
        var facing = Direction8FromUnit(velocity.Normalized());
        var entity = _zone.SpawnProjectile(_networkIds.Rent(), position, facing);
        entity.SetVelocity(velocity);
        entity.MakeNonCombatant();
        _projectileTierOf[entity.Id] = tier;
        return entity.Id;
    }

    // Move the projectile entity to its advanced position (Zone migrates the spatial-grid bucket on a tile cross).
    private void MoveProjectileEntity(ulong entityId, WorldVector newPosition)
    {
        if (_zone.World.TryGet(entityId, out var entity))
        {
            _zone.MoveProjectile(entity, newPosition);
        }
    }

    // Despawn the projectile entity (world removal → EntityDespawn to AOI viewers), free its network id, drop its tier.
    private void DespawnProjectileEntity(ulong entityId)
    {
        _projectileTierOf.Remove(entityId);
        if (_zone.Despawn(entityId, out var removed))
        {
            _networkIds.Return(removed.NetworkId);
        }
    }

    // Apply projectile damage to a monster through the SAME seam the melee uses: ApplyDamage, a cosmetic AOI damage
    // number, the contribution ledger (for corpse loot eligibility), and KillMonster on death. Returns whether the
    // monster died this hit (drives the Perfect pierce). Mirrors HandleAttack's post-hit tail.
    private bool ApplyProjectileDamage(WorldEntity monster, int amount, ulong shooterEntityId, Guid shooterCharacterId, uint serverTick)
    {
        // BOSS-2 (P1): the boss PLATING damage-taken modifier — the uniform hook covering EVERY non-melee source
        // (skillshot + tether + midpoint blast all funnel here). A no-op for any non-boss monster / when the plating is
        // down. Applied BEFORE the damage number so the floated "-N" matches the HP actually removed. (The melee path
        // applies the same modifier inside FreeAimSectorResolver — the two damage-application seams, one modifier.)
        amount = _bossEncounter.ModifyIncomingDamage(monster.Id, amount);
        if (!monster.ApplyDamage(amount))
        {
            return false;
        }

        // Float a cosmetic damage number to every AOI viewer (the shooter has no client-side prediction for a
        // projectile, so — unlike melee — it is NOT excluded).
        BroadcastDamageEvent(monster, amount);

        if (shooterCharacterId != Guid.Empty)
        {
            _contributionLedger.RecordDamage(monster.Id, shooterCharacterId, amount);
        }

        if (monster.Stats.Health <= 0)
        {
            KillMonster(monster);
            return true;
        }

        return false;
    }

    // Whether two shooter entities are mutually PAIRED (the fusion gate). Resolves each entity to its owning session
    // and checks the symmetric pair link. The session set is tiny (a co-op session), so the linear resolve is cheap.
    private bool AreEntitiesPaired(ulong shooterEntityIdA, ulong shooterEntityIdB)
    {
        var a = SessionByEntity(shooterEntityIdA);
        var b = SessionByEntity(shooterEntityIdB);
        return a is not null && b is not null
            && ReferenceEquals(a.PartnerSession, b) && ReferenceEquals(b.PartnerSession, a);
    }

    // DUO-SKILLSHOT: resolve the authenticated session owning `entityId`, or null. Linear scan over the (small) session
    // set — no reverse index maintained for this experiment.
    private ClientSession? SessionByEntity(ulong entityId)
    {
        foreach (var session in _sessions.Values)
        {
            if (session.IsAuthenticated && session.EntityId == entityId)
            {
                return session;
            }
        }

        return null;
    }

    // BOSS-1 (docs/boss-encounter-sunderer-design.md): /boss enter/leave. Inside the arena interior → LEAVE (the
    // engine teleports the issuer back to its stored return tile). Outside → ENTER, pulling in the issuer's duo
    // partner when paired AND online (the engine gives the partner its own chat line — no consent flow, the branch's
    // decision); works solo with no partner. The engine returns the ISSUER's chat line either way.
    private void HandleBossCommand(ClientSession sender)
    {
        if (!TryGetSessionEntity(sender, out var issuer))
        {
            SendSystem(sender, "boss: no controllable entity.");
            return;
        }

        if (BossArena.ContainsInterior(issuer.TileCoord))
        {
            if (_bossEncounter.TryLeave(issuer, out var leaveMessage))
            {
                SendSystem(sender, leaveMessage);
                return;
            }

            // Inside the arena but not a tracked participant — defensive only (the arena is a sealed pocket, so this
            // is unreachable in normal play). Eject to a spawn anchor so the player is never stranded off-map.
            _zone.Teleport(issuer, _zone.NextSpawnTile());
            sender.ClearMoveIntent();
            SendSystem(sender, "You leave the Sunderer's arena.");
            return;
        }

        WorldEntity? partner = null;
        if (sender.PartnerSession is { IsAuthenticated: true } partnerSession
            && TryGetSessionEntity(partnerSession, out var partnerEntity))
        {
            partner = partnerEntity;
        }

        _bossEncounter.TryBegin(issuer, partner, _serverTick, out var message);
        SendSystem(sender, message);
    }

    // BOSS-1: despawn + fully clean up the encounter boss — the SAME leak-free teardown KillMonster /
    // HandleClearSpawnersCommand run (action cooldowns, contribution ledger, brain, type map, network id) but with NO
    // corpse/loot roll (a reset/abandon, not a kill). Idempotent: a no-op if the boss is already gone — a PLAYER kill
    // runs KillMonster first (which despawns it), so the encounter's victory path only calls this defensively. The
    // boss belongs to NEITHER spawner map, so there is nothing else to unhook.
    // BOSS-2 (P1): this is generic by id, so it is ALSO the encounter-ADD teardown (the interposer drone) — wired as
    // both `despawnBoss` and `despawnAdd` on the engine, keeping the "adds cleaned everywhere the boss is" invariant.
    private void DespawnBossEntity(ulong bossId)
    {
        if (!_zone.Despawn(bossId, out var removed))
        {
            return;
        }

        _actionExecutor.ClearEntity(bossId);
        _contributionLedger.Forget(bossId);
        if (_monsterTypeOf.TryGetValue(bossId, out var typeToForget))
        {
            ResolveBehavior(typeToForget).Forget(bossId);
        }
        else
        {
            _defaultBehavior.Forget(bossId);
        }

        _monsterTypeOf.Remove(bossId);
        _networkIds.Return(removed.NetworkId);
    }

    // DUO-SKILLSHOT: the Direction8 a continuous unit heading points toward (nearest of 8), for the projectile's
    // sprite facing. Mirrors the 8-way table used elsewhere; defaults to S for a zero vector.
    private static Direction8 Direction8FromUnit(WorldVector unitDir)
    {
        if (unitDir.LengthSquared <= 0d)
        {
            return Direction8.S;
        }

        var dx = Math.Sign(unitDir.X);
        var dy = Math.Sign(unitDir.Y);
        return (dx, dy) switch
        {
            (0, -1) => Direction8.N,
            (1, -1) => Direction8.NE,
            (1, 0) => Direction8.E,
            (1, 1) => Direction8.SE,
            (0, 1) => Direction8.S,
            (-1, 1) => Direction8.SW,
            (-1, 0) => Direction8.W,
            (-1, -1) => Direction8.NW,
            _ => Direction8.S,
        };
    }

    // ==== DUO-WAVE2 (exp/duo-abilities): co-op abilities 2-4 ====

    // Ability 2 (Unison Shield) timing + strengths (server ticks @20Hz). Perfect is the tighter window; a coincidence
    // within it grants the strongest shared shield. A solo press grants a weak personal shield; the shared cooldown
    // runs from the FIRST press. Tunables live here (the one obvious place, per-ability).
    private const uint ShieldPerfectWindowTicks = 2;
    private const uint ShieldGoodWindowTicks = 6;
    private const int ShieldSoloStrength = 10;
    private const int ShieldGoodStrength = 25;
    private const int ShieldPerfectStrength = 40;
    private const uint ShieldDurationTicks = 80;    // 4s
    private const uint ShieldCooldownTicks = 200;   // 10s shared cooldown

    // The SHARED monster-slow (tether sweep + detonation slow zone): 30% slow for 1s (@20Hz). One place for both.
    private const double MonsterSlowFactor = 0.7d;
    private const uint MonsterSlowDurationTicks = 20;

    // DUO-WAVE2: dispatch a co-op R/G/V trigger. Dedup on the session's DEDICATED duo cursor (independent of move/
    // attack/action/fire), resolve the caller's entity, and route to the ability. A malformed selector never reaches
    // here (the codec range-validates DuoAbilityKind).
    private void HandleDuoAbility(ClientSession session, uint sequence, DuoAbilityKind ability)
    {
        if (!session.TryConsumeDuoSequence(sequence))
        {
            return;
        }

        if (!TryGetSessionEntity(session, out var self))
        {
            return;
        }

        switch (ability)
        {
            case DuoAbilityKind.Shield:
                HandleShieldPress(session, self);
                break;
            case DuoAbilityKind.TetherToggle:
                HandleTetherToggle(session, self);
                break;
            case DuoAbilityKind.Detonate:
                HandleDetonate(session, self);
                break;
        }
    }

    // DUO-WAVE2 ability 2 (Unison Shield): the caller pressed R. Flash an echo cue on the partner (so they can react),
    // then either COMPLETE a shared shield (the partner has a pending press within the timing window — both get the
    // tier's shield, shared cooldown from the first press) or, on a fresh press off cooldown, grant a weak SOLO shield
    // and record the pending press so the partner can still upgrade it within the window.
    private void HandleShieldPress(ClientSession session, WorldEntity self)
    {
        var t = _serverTick;
        var partner = session.PartnerSession;
        var partnerLive = partner is { IsAuthenticated: true };

        if (partnerLive)
        {
            // Echo cue on the partner's character (unreliable — a missed flash is harmless).
            TrySend(partner!.Peer, new EchoCueMessage(self.NetworkId, EchoCueKind.ShieldPress), DeliveryMethod.Unreliable);
        }

        // Shared upgrade: the partner already has a pending press within the window → both get the shared shield.
        if (partnerLive && partner!.ShieldPendingPressTick is { } pTick)
        {
            var tier = PairedTimingWindow.Classify(t, pTick, ShieldPerfectWindowTicks, ShieldGoodWindowTicks);
            if (tier != PairTier.None && TryGetSessionEntity(partner, out var partnerEntity))
            {
                var strength = tier == PairTier.Perfect ? ShieldPerfectStrength : ShieldGoodStrength;
                var expiry = t + ShieldDurationTicks;
                var cooldownUntil = Math.Min(t, pTick) + ShieldCooldownTicks;
                ApplyShield(session, self, strength, expiry, cooldownUntil);
                ApplyShield(partner, partnerEntity, strength, expiry, cooldownUntil);
                session.ClearShieldPending();
                partner.ClearShieldPending();
                return;
            }
        }

        // Fresh / solo press: gate on the shared cooldown.
        if (t < session.ShieldCooldownUntilTick)
        {
            return;
        }

        ApplyShield(session, self, ShieldSoloStrength, t + ShieldDurationTicks, t + ShieldCooldownTicks);
        session.SetShieldPending(t);
        // LIVE FEEL FIX (2026-07-04, user repro: "the shield seems to go only on one"): the first press used to
        // ALSO pre-arm the partner's cooldown, so a partner pressing just OUTSIDE the upgrade window got nothing
        // for 10s — one bubble on screen. Per the spec ("a solo press still grants that player a weak personal
        // shield"), a missed-window press now falls through to the partner's OWN solo grant; the SHARED cooldown
        // binds where the spec puts it — on the upgraded shared bubble (both cooldowns armed in the upgrade path
        // above). Worst case without the pre-block: two out-of-sync weak solos (10) on independent cooldowns —
        // strictly less protection than one coordinated Perfect (40+40 on one shared cooldown).
    }

    // DUO-WAVE2 ability 2: arm one player's shield + shared cooldown and replicate the bubble. ArmShield keeps the
    // stronger of any live pool and the new strength (a solo→shared upgrade).
    private void ApplyShield(ClientSession session, WorldEntity entity, int strength, uint expiryTick, uint cooldownUntil)
    {
        session.ArmShield(strength, expiryTick, _serverTick);
        session.SetShieldCooldownUntil(cooldownUntil);
        var pool = session.ShieldRemainingAt(_serverTick);
        var message = new ShieldStatusMessage(entity.NetworkId, (ushort)Math.Clamp(pool, 0, ushort.MaxValue), expiryTick, pool > 0);
        BroadcastShieldStatus(session, entity, message);
    }

    // DUO-WAVE2 ability 2: per-tick shield-expiry pass. For each session whose shield just lapsed, push a single
    // ShieldStatus(Active=false) to that player + partner so both drop the bubble (natural 4s expiry has no other wire
    // signal). Cheap: a tiny loop over sessions, and TryExpireShield no-ops unless a shield is actually armed + due.
    private void StepShieldExpiry(uint serverTick)
    {
        foreach (var session in _sessions.Values)
        {
            if (session.IsAuthenticated && session.TryExpireShield(serverTick) && TryGetSessionEntity(session, out var entity))
            {
                BroadcastShieldStatus(session, entity, new ShieldStatusMessage(entity.NetworkId, 0, 0, false));
            }
        }
    }

    // DUO-WAVE2 ability 2: the shield ABSORB seam the PlayerDamageGate calls (between i-frame + ApplyDamage). Decrement
    // the victim's session pool; on a real absorb, re-push the shrunk bubble to the victim + partner. Returns the amount soaked.
    private int AbsorbShield(WorldEntity victim, int amount, uint serverTick)
    {
        if (victim.OwnerSession is not { } session)
        {
            return 0;
        }

        var absorbed = session.AbsorbWithShield(amount, serverTick);
        if (absorbed > 0)
        {
            var pool = session.ShieldRemainingAt(serverTick);
            var message = new ShieldStatusMessage(victim.NetworkId, (ushort)Math.Clamp(pool, 0, ushort.MaxValue), session.ShieldExpiryTick, pool > 0);
            BroadcastShieldStatus(session, victim, message);
        }

        return absorbed;
    }

    // DUO-WAVE2 ability 3 (Laser Tether): the caller pressed G — toggle the beam with their partner (either partner may
    // toggle; both see the state). No partner ⇒ a hint, nothing to link.
    private void HandleTetherToggle(ClientSession session, WorldEntity self)
    {
        if (session.PartnerSession is not { IsAuthenticated: true } partner || !TryGetSessionEntity(partner, out var partnerEntity))
        {
            SendSystem(session, "tether: you have no partner to link with (/pair <name>).");
            return;
        }

        _tether.Toggle(self, partnerEntity, _serverTick);
    }

    // DUO-WAVE2 ability 4 (Midpoint Detonation): the caller pressed V — initiate, or confirm the partner's pending
    // initiate. A solo player (no partner) still initiates; the engine degrades it to a self-blast when unconfirmed.
    private void HandleDetonate(ClientSession session, WorldEntity self)
    {
        WorldEntity? partnerEntity = null;
        if (session.PartnerSession is { IsAuthenticated: true } partner && TryGetSessionEntity(partner, out var pe))
        {
            partnerEntity = pe;
        }

        _detonation.PressDetonate(self, partnerEntity, _serverTick);
    }

    // LIVE FIX (2026-07-05, user repro: "shields on characters seem to not be replicated... when not paired"): the
    // bubble used to go to self + partner ONLY (SendToSelfAndPartner, now retired) — a world-visible visual scoped
    // like a pair-private signal, so an UNPAIRED player's shield never reached anyone else's screen (and even a
    // paired shield was invisible to third parties). The bubble is world state: broadcast it like BroadcastDamageEvent
    // — the owner always, plus every authenticated session that knows the entity and has it in interest. Reliable-
    // ordered (shield edges are discrete state, not a lossy stream). Known shared limitation with damage events: a
    // viewer entering AOI AFTER the arm misses the event (worst case a bubble absent for its <=4s life) — acceptable
    // for the experiment; the fix if it matters is shield-in-snapshot, not a bigger event fanout.
    private void BroadcastShieldStatus(ClientSession owner, WorldEntity entity, ShieldStatusMessage message)
    {
        TrySend(owner.Peer, message, DeliveryMethod.ReliableOrdered);
        foreach (var session in _sessions.Values)
        {
            if (ReferenceEquals(session, owner)
                || !session.IsAuthenticated
                || !session.KnowsEntity(entity.NetworkId)
                || !TryGetSessionEntity(session, out var viewerEntity))
            {
                continue;
            }

            if (IsEntityInInterest(viewerEntity, entity, session, _tuning.InterestRadius))
            {
                TrySend(session.Peer, message, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    // BOSS-2 (P1) LEGIBILITY (Laws 4/7): the boss-plating wire relay (BossEncounterEngine seam). Resolve the boss
    // entity by id (a no-op if it is already gone) and broadcast its plating state AOI-scoped to every viewer that
    // knows it and has it in interest — the SAME fanout BroadcastDamageEvent uses (world state, no owner session).
    // Reliable-ordered: plating on/shatter/reform/off are discrete edges a dropped packet would desync.
    private void BroadcastBossPlating(ulong bossId, bool platingActive)
    {
        if (!_zone.World.TryGet(bossId, out var boss))
        {
            return;
        }

        var message = new BossPlatingMessage(boss.NetworkId, platingActive);
        foreach (var session in _sessions.Values)
        {
            if (!session.IsAuthenticated
                || !session.KnowsEntity(boss.NetworkId)
                || !TryGetSessionEntity(session, out var viewerEntity))
            {
                continue;
            }

            if (IsEntityInInterest(viewerEntity, boss, session, _tuning.InterestRadius))
            {
                TrySend(session.Peer, message, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    // DUO-WAVE2 ability 3: the tether-status wire relay (engine seam) — push on/off/broken to BOTH linked players.
    private void SendTetherStatus(WorldEntity a, WorldEntity b, TetherState state)
    {
        var message = new TetherStatusMessage(a.NetworkId, b.NetworkId, state);
        if (SessionByEntity(a.Id) is { } sa)
        {
            TrySend(sa.Peer, message, DeliveryMethod.ReliableOrdered);
        }

        if (SessionByEntity(b.Id) is { } sb)
        {
            TrySend(sb.Peer, message, DeliveryMethod.ReliableOrdered);
        }
    }

    // DUO-WAVE2 abilities 2 & 4: the echo-cue wire relay (engine seam) — flash a brief cue on the target's own client.
    private void SendEchoCueTo(WorldEntity target, EchoCueKind cue)
    {
        if (SessionByEntity(target.Id) is { } session)
        {
            TrySend(session.Peer, new EchoCueMessage(target.NetworkId, cue), DeliveryMethod.Unreliable);
        }
    }

    // DUO-WAVE2 ability 4: the live-tracking charge-marker relay (engine seam) — push the current blast circle to both
    // the initiator and the confirmer each charge tick (Active=false is the resolve/cancel end edge).
    private void SendMidpointCharge(
        WorldEntity initiator, WorldEntity partner, ulong chargeId, WorldVector origin, double radiusUnits,
        uint startTick, uint resolveTick, bool active)
    {
        var message = new MidpointChargeMessage(chargeId, TelegraphShape.Circle(origin, radiusUnits), startTick, resolveTick, active);
        if (SessionByEntity(initiator.Id) is { } si)
        {
            TrySend(si.Peer, message, DeliveryMethod.ReliableOrdered);
        }

        if (SessionByEntity(partner.Id) is { } sp)
        {
            TrySend(sp.Peer, message, DeliveryMethod.ReliableOrdered);
        }
    }

    // DUO-WAVE2 abilities 3 & 4: the shared monster-damage seam — apply `amount` through the SAME melee path the
    // skillshot uses (ApplyProjectileDamage: ApplyDamage + cosmetic number + contribution ledger + KillMonster),
    // attributed to `attributedTo` (one of the paired players) for loot eligibility. Return value is unused here.
    private void ApplyDuoMonsterDamage(WorldEntity monster, WorldEntity attributedTo, int amount, uint serverTick)
    {
        ApplyProjectileDamage(monster, amount, attributedTo.Id, attributedTo.CharacterId ?? Guid.Empty, serverTick);
    }

    // DUO-WAVE2 abilities 3 & 4: the SHARED monster-slow seam — arm/refresh a monster's brief 30%/1s slow via the
    // reused speed-modifier path (entity SpeedMultiplier → EntitySpawn/MovementSpeedChanged cadence). Re-arming while
    // already slowed only extends the expiry (the multiplier is not re-stacked — TrySetSpeedMultiplier no-ops an equal
    // value anyway). StepMonsterSlows restores the base multiplier once the expiry passes.
    private void SlowMonster(WorldEntity monster, uint serverTick)
    {
        if (monster.Kind != EntityKind.Monster)
        {
            return;
        }

        var until = serverTick + MonsterSlowDurationTicks;
        var alreadySlowed = _monsterSlowUntil.TryGetValue(monster.Id, out var prev);
        _monsterSlowUntil[monster.Id] = alreadySlowed ? Math.Max(prev, until) : until;
        if (!alreadySlowed && monster.TrySetSpeedMultiplier(BaseMoveMultiplier(monster) * MonsterSlowFactor))
        {
            RefreshSpeedStat(monster);
            BroadcastMovementSpeedChanged(monster, EffectiveStepCooldownMs(monster));
        }
    }

    // DUO-WAVE2: restore any monster whose brief slow lapsed this tick — reset its base multiplier + re-broadcast the
    // cadence. Also drops entries for monsters that despawned mid-slow (the id no longer resolves). ~free when none slowed.
    private void StepMonsterSlows(uint serverTick)
    {
        if (_monsterSlowUntil.Count == 0)
        {
            return;
        }

        _slowExpiryScratch.Clear();
        foreach (var (id, until) in _monsterSlowUntil)
        {
            if (serverTick >= until)
            {
                _slowExpiryScratch.Add(id);
            }
        }

        foreach (var id in _slowExpiryScratch)
        {
            _monsterSlowUntil.Remove(id);
            if (_zone.World.TryGet(id, out var monster) && monster.Kind == EntityKind.Monster
                && monster.TrySetSpeedMultiplier(BaseMoveMultiplier(monster)))
            {
                RefreshSpeedStat(monster);
                BroadcastMovementSpeedChanged(monster, EffectiveStepCooldownMs(monster));
            }
        }
    }

    // DUO-WAVE2: a monster's un-slowed base speed multiplier — its live TYPE MoveSpeedMultiplier (1.0 for a monster with
    // no registered type). The slow multiplies this; the restore resets to it.
    private double BaseMoveMultiplier(WorldEntity monster)
        => _monsterTypeOf.TryGetValue(monster.Id, out var type) ? type.MoveSpeedMultiplier : 1.0d;

    // DUO-WAVE2: tear down any active tether + in-progress detonation involving `entityId` (unpair / disconnect). The
    // tether push its Off status to both clients; the detonation cancels silently. Called from the BreakPair funnel.
    private void TearDownDuoAbilities(ulong entityId)
    {
        _tether.RemoveInvolving(entityId);
        _detonation.RemoveInvolving(entityId);
    }

    // ECOLOGY E4 (docs/ecology-v1-design.md D6b, §8 E4): /rumors — ALL players, not just admins (unlike every
    // other command below, this one is dispatched BEFORE the admin-role gate in HandleCommand). One flavored line
    // per authored region, chosen by that region's WORST type-state (EcologyWire.WorstStateOf) through the shared
    // EcologyRumors table — fuzzy words only, never a stock/pressure number (D5).
    private void HandleRumorsCommand(ClientSession sender)
    {
        var any = false;
        foreach (var region in _ecology.Registry.Regions)
        {
            any = true;
            SendSystem(sender, EcologyRumors.LineFor(region.DisplayName, EcologyWire.WorstStateOf(_ecology, region)));
        }

        if (!any)
        {
            SendSystem(sender, "rumors: no news from any region.");
        }
    }

    // ECOLOGY E1 (docs/ecology-v1-design.md §3): admin dev command /ecology — no args dumps every authored
    // region×type's EXACT stock/pressure/state (admin eyes only, per D5 — players get fuzzy words via E4's
    // /rumors, never numbers); `set <region> <type> <stock>` and `pressure <region> <type> <n>` force-write a
    // value for live testing (clamped by EcologyState, same "live, no restart" philosophy as the F1 tuning knobs
    // and /slam). Region/type ids are matched case-insensitively, same as monster type ids.
    private void HandleEcologyCommand(ClientSession sender, string[] parts)
    {
        const string usage =
            "usage: /ecology | /ecology set <region> <type> <stock> | /ecology pressure <region> <type> <n>.";

        if (parts.Length == 1)
        {
            var any = false;
            foreach (var region in _ecology.Registry.Regions)
            {
                foreach (var dumpTypeId in region.Types.Keys)
                {
                    any = true;
                    SendSystem(
                        sender,
                        $"ecology: {region.DisplayName} ({region.Id})/{dumpTypeId}: "
                            + $"stock={_ecology.StockOf(region.Id, dumpTypeId):0.###} "
                            + $"pressure={_ecology.PressureOf(region.Id, dumpTypeId):0.###} "
                            + $"state={_ecology.StateOf(region.Id, dumpTypeId)}.");
                }
            }

            if (!any)
            {
                SendSystem(sender, "ecology: no authored regions.");
            }

            return;
        }

        var subcommand = parts[1].ToLowerInvariant();
        if ((subcommand != "set" && subcommand != "pressure") || parts.Length < 5)
        {
            SendSystem(sender, usage);
            return;
        }

        var regionId = parts[2];
        var typeId = parts[3];
        if (!double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
        {
            SendSystem(sender, usage);
            return;
        }

        var applied = subcommand == "set"
            ? _ecology.TrySetStock(regionId, typeId, value)
            : _ecology.TrySetPressure(regionId, typeId, value);

        if (!applied)
        {
            SendSystem(sender, $"ecology: unknown region/type '{regionId}'/'{typeId}'.");
            return;
        }

        var applyDescription = subcommand == "set"
            ? $"stock={_ecology.StockOf(regionId, typeId):0.###}"
            : $"pressure={_ecology.PressureOf(regionId, typeId):0.###}";
        SendSystem(sender, $"ecology: {regionId}/{typeId} {applyDescription} (state={_ecology.StateOf(regionId, typeId)}).");
        Log.Info($"{sender.DisplayName} set ecology {regionId}/{typeId} via /ecology {subcommand} {value}.");

        // ECOLOGY E4: a forced /ecology set|pressure is a live admin action like any other tuning knob — check +
        // (if it actually moved the state) broadcast immediately rather than waiting for the next EcologyTick.
        if (_ecology.Registry.TryGet(regionId, out var forcedRegion))
        {
            CheckRegionEcologyChange(forcedRegion);
        }
    }

    // LIVING-ENEMIES P1: admin dev command /monster — spawns an EntityKind.Monster at the CALLER's current tile
    // (mirroring the SpawnDummies setup but at the sender, like a "/dummy here"), records that tile as the
    // monster's leash HOME, and registers it with the roam AI. The monster carries CharacterStats (full HP) so it
    // shows an overhead HP bar and is hittable (CombatTargeting.IsAttackableEnemy now includes Monster). The
    // server then idles it near home and occasionally strolls it within the leash. It spawns on the caller's own
    // tile (always walkable — the caller stands there); replication + client interpolation render it as a moving
    // cube for free. LIVING-ENEMIES P2: it now also AGGROS the nearest player in range, CHASES (leashed to home),
    // and ATTACKS when adjacent (the player TAKES damage — HP floors at 0, no death/respawn yet).
    private void HandleMonsterCommand(ClientSession sender, string[] parts)
    {
        if (!TryGetSessionEntity(sender, out var actor))
        {
            SendSystem(sender, "monster: no controllable entity.");
            return;
        }

        // LIVING-ENEMIES P2-POLISH: /monster <name> spawns that TYPE; /monster with no name defaults to the only type
        // (slime). An unknown name is a clear error listing the available names (so a typo is obvious, not silent).
        MonsterType type;
        if (parts.Length >= 2)
        {
            if (!_monsterTypes.TryGet(parts[1], out type))
            {
                var names = string.Join(", ", _monsterTypes.Types.Select(t => t.Id));
                SendSystem(sender, $"monster: unknown type '{parts[1]}'. Available: {names}.");
                return;
            }
        }
        else
        {
            type = _monsterTypes.Default;
        }

        // LIVING-ENEMIES P3: /monster now creates a PERSISTENT SPAWNER at the caller's tile (the spawner owns + respawns
        // the monster, and is the red-tile anchor that survives a kill). The spawner immediately spawns its first
        // monster. The spawner tile = the monster's leash home; it must be walkable (the caller stands there).
        var spawner = new MonsterSpawner(_nextSpawnerId++, actor.TileCoord, type);
        _spawners[spawner.SpawnerId] = spawner;
        var monster = SpawnMonsterForSpawner(spawner);

        var effectiveMs = EffectiveStepCooldownMs(monster);
        SendSystem(
            sender,
            $"monster: spawner #{spawner.SpawnerId} for {type.DisplayName} at {spawner.Tile.X},{spawner.Tile.Y}, hp={type.MaxHealth}, step={effectiveMs}ms, roamRadius={type.RoamRadius}, aggro={type.AggroRadius}, leash={type.ChaseLeash}, atk={type.AttackDamage}/{type.AttackCooldownMs}ms, respawn={type.RespawnMs}ms.");
        Log.Info($"{sender.DisplayName} created spawner #{spawner.SpawnerId} for {type.DisplayName} at {spawner.Tile} (type={type.Id}).");
    }

    // SPAWNER-CLEANUP (todo/monster-types-followups #4): admin dev command /clearspawners — removes EVERY spawner (a
    // long dev session otherwise only accumulates them; /monster adds, nothing deleted) and despawns each spawner's
    // live monster. This is an ADMIN CLEAR, not a kill: no corpse, no loot roll, no respawn schedule — the monster and
    // its AI/type/ledger/action state are simply removed, mirroring KillMonster's cleanup minus the death effects.
    // The red markers need no explicit send: SyncSpawnerMarkers' "no longer exists" branch sends Active=false to every
    // viewer on the next AOI pass once the spawner leaves _spawners, and EntityDespawn likewise rides the normal AOI
    // known-entity diff.
    private void HandleClearSpawnersCommand(ClientSession sender)
    {
        // ECOLOGY E2 (D10 "/clearspawners clears BOTH kinds cleanly"): the "nothing to do" guard now also checks
        // for live region-ecology monsters — a session that never ran /monster but has a populated world (the
        // ecology materializes on its own from boot) must still get a real clear, not "no spawners".
        var hasLegacySpawners = _spawners.Count > 0;
        var hasRegionMonsters = _regionSpawners.Any(regionSpawner => regionSpawner.LiveCount > 0);
        if (!hasLegacySpawners && !hasRegionMonsters)
        {
            SendSystem(sender, "clearspawners: no spawners.");
            return;
        }

        var spawnerCount = _spawners.Count;
        var monsterCount = 0;
        foreach (var spawner in _spawners.Values)
        {
            if (spawner.LiveMonsterId is not ulong monsterId)
            {
                continue; // Dead + pending respawn — dropping the spawner below cancels the respawn (RespawnMonsters iterates _spawners).
            }

            if (_zone.Despawn(monsterId, out var removed))
            {
                // The same leak-free cleanup KillMonster does (action cooldowns, contribution ledger, brain, type map),
                // in the same order (behavior resolved BEFORE the type is removed).
                _actionExecutor.ClearEntity(monsterId);
                _contributionLedger.Forget(monsterId);
                if (_monsterTypeOf.TryGetValue(monsterId, out var typeToForget))
                {
                    ResolveBehavior(typeToForget).Forget(monsterId);
                }
                else
                {
                    _defaultBehavior.Forget(monsterId);
                }

                _monsterTypeOf.Remove(monsterId);
                _networkIds.Return(removed.NetworkId);
                monsterCount++;
            }

            _spawnerOfMonster.Remove(monsterId);
        }

        _spawners.Clear();

        // ECOLOGY E2 (D10): region-spawned monsters despawn too — same leak-free cleanup, no corpse, no ecology
        // stock/pressure change (an admin clear is not a kill). Unlike the legacy loop above, the RegionSpawner
        // objects themselves are NEVER removed (a region×type is a permanent slot, not a dev-session spawner).
        var regionMonsterCount = ClearRegionSpawnerMonsters();
        monsterCount += regionMonsterCount;

        SendSystem(sender, $"clearspawners: removed {spawnerCount} spawner(s), despawned {monsterCount} monster(s) ({regionMonsterCount} region-ecology).");
        Log.Info($"{sender.DisplayName} cleared {spawnerCount} spawner(s) + {regionMonsterCount} region-ecology monster(s) ({monsterCount} total despawned).");
    }

    // LIVING-ENEMIES P3: spawns a fresh full-HP monster of the spawner's type at the spawner tile, wires it to the AI +
    // type maps, and attaches it to the spawner. Shared by the initial /monster spawn AND each respawn. The red marker
    // is a separate persistent spawner concept, so spawning a monster does NOT send a per-monster home anymore.
    private WorldEntity SpawnMonsterForSpawner(MonsterSpawner spawner)
    {
        var monster = SpawnMonsterCore(spawner.Type, spawner.Tile, maxHealthOverride: null, renderScaleOverride: null);

        // Link the monster to its spawner (both directions) so a death finds the spawner in O(1).
        spawner.AttachMonster(monster.Id);
        _spawnerOfMonster[monster.Id] = spawner;
        return monster;
    }

    // ECOLOGY E2: spawns a fresh monster of `spawner`'s type at `tile` for a RegionSpawner (the region-ecology
    // sibling of SpawnMonsterForSpawner). `overgrown` is D7's per-spawn modifier: while the region×type reads
    // Overgrown, the NEW monster gets +25% maxHealth and +25% renderScale — applied ONLY at spawn (an
    // already-alive monster never retroactively changes size/HP when its region later flips into/out of
    // Overgrown).
    private WorldEntity SpawnMonsterForRegion(RegionSpawner spawner, TileCoord tile, bool overgrown)
    {
        var type = spawner.Type;
        int? maxHealthOverride = overgrown ? (int)Math.Ceiling(type.MaxHealth * OvergrownSpawnStatMultiplier) : null;
        double? renderScaleOverride = overgrown ? type.RenderScale * OvergrownSpawnStatMultiplier : null;
        var monster = SpawnMonsterCore(type, tile, maxHealthOverride, renderScaleOverride);

        spawner.AddLiveMonster(monster.Id);
        _regionSpawnerOfMonster[monster.Id] = spawner;
        return monster;
    }

    // The shared monster-spawn core (LIVING-ENEMIES P3 + ECOLOGY E2): rents a network id, spawns the transient
    // entity, seeds its per-TYPE stats/speed (with the D7 overgrown overrides applied per-INSTANCE, never onto
    // the shared MonsterType), and registers it with its type's behavior. Does NOT touch spawner-ownership
    // bookkeeping (_spawnerOfMonster / _regionSpawnerOfMonster / RegionSpawner.AddLiveMonster) — that stays the
    // caller's job, since the two spawner kinds track ownership differently (single-monster attach vs a live
    // set).
    private WorldEntity SpawnMonsterCore(MonsterType type, TileCoord tile, int? maxHealthOverride, double? renderScaleOverride)
    {
        // Rent throws only on the (ushort-space) exhaustion the dummy/resource spawns also rely on never hitting.
        var monster = _zone.SpawnTransient(_networkIds.Rent(), EntityKind.Monster, type.DisplayName, tile, Direction8.S);
        // The monster takes its TYPE's stats/AI tuning. MaxHealth (spawn at full, or the D7 overgrown override) +
        // the move-speed multiplier (which feeds the EffectiveStepCooldown path so it steps on its OWN
        // type-derived cadence — outrunnable). Remember the type for the AI step.
        monster.SetMaxHealthFull(maxHealthOverride ?? type.MaxHealth);
        monster.TrySetSpeedMultiplier(type.MoveSpeedMultiplier);
        // Seed the monster's tiles/sec speed stat from its type multiplier (BaseMoveSpeedUnitsPerSecond ×
        // MoveSpeedMultiplier). MONSTER-BEHAVIOR P2: this is DORMANT for a HOPPER (it leaps via the cadence gate, not
        // the velocity integrator; Velocity stays Zero) but IS the walk speed for a GLIDER (GlideLocomotion reads
        // SpeedUnitsPerSecond per tick) — so it must be set at spawn for a gnoll to have a non-zero walk speed.
        RefreshSpeedStat(monster);
        if (renderScaleOverride.HasValue)
        {
            monster.SetRenderScaleOverride(renderScaleOverride.Value);
        }

        _monsterTypeOf[monster.Id] = type;

        // Register with the roam AI: start Idle with an initial randomized pause, tick-quantised off THIS type's
        // pause bounds.
        var tunables = _monsterTypes.BuildTunables(type);
        ResolveBehavior(type).Register(monster, _serverTick, tunables.PauseMinTicks, tunables.PauseMaxTicks, tunables.AggroScanIntervalTicks);
        return monster;
    }

    // ECOLOGY E2 (§3 "Spawning"): per-tick materialization pass. For every RegionSpawner whose pacing gate is due
    // AND whose live count is below its (Overgrown-adjusted, D7) effective cap, attempt ONE spawn: take the next
    // round-robin spawn tile and, if no player is within RegionSpawnPlayerExclusionRadius of it, spawn there.
    // Pacing is armed on every ATTEMPT (spawn or skip) — see RegionSpawner.ArmPacing's own doc for why a
    // player-camped tile must not turn into a busy-loop. `serverTick` is a PARAMETER (not read from the live
    // _serverTick field) so this stays headlessly testable in a plain tick-count loop, like EcologyState.
    private void MaterializeRegionSpawners(uint serverTick)
    {
        if (_regionSpawners.Count == 0)
        {
            return;
        }

        foreach (var spawner in _regionSpawners)
        {
            if (!spawner.IsSpawnPacingDue(serverTick))
            {
                continue;
            }

            var state = _ecology.StateOf(spawner.RegionId, spawner.TypeId);
            var overgrown = state == EcologyState.PopulationState.Overgrown;
            // D7: while Overgrown, the live cap itself grows +50% (rounded up) — the "more" half of "more and meaner".
            var effectiveMaxLive = overgrown
                ? (int)Math.Ceiling(spawner.BaseMaxLive * EcologyState.OvergrowthCapMultiplier)
                : spawner.BaseMaxLive;
            var stockFloor = (int)Math.Floor(_ecology.StockOf(spawner.RegionId, spawner.TypeId));
            // LAST SURVIVOR rule (E2 review finding 1, orchestrator decision): floor(stock) hits 0 at the D3
            // brink (Smin 0.5 in every starter region), which would leave a fully-hunted region VISIBLY EXTINCT
            // for the ~25 min the quadratic wound takes to crawl back past 1.0 — the dead-content reading D3
            // exists to forbid. Instead the region always hosts at least ONE monster while its stock lives
            // (always): a lone survivor in an emptied hollow reads as wounded-not-dead and keeps the region
            // interactable. The survivor regime spawns on a SLOW trickle (below) so camping the last kill is a
            // 30s-per-monster faucet, not a 2s one.
            var survivorRegime = stockFloor < 1;
            var target = Math.Min(Math.Max(1, stockFloor), effectiveMaxLive);

            if (spawner.LiveCount >= target)
            {
                continue; // Nothing to do this window — no attempt happens, so pacing is NOT armed (stays "due").
            }

            // An attempt happens now (whether or not it results in a spawn) — arm the pacing gate. The survivor
            // regime (stock below 1) trickles at 15x the normal pacing (30 s at the 2 s default).
            spawner.ArmPacing(serverTick, survivorRegime ? _regionSpawnPacingTicks * 15 : _regionSpawnPacingTicks);

            if (!spawner.TryTakeNextTile(out var tile))
            {
                continue; // This region×type derived ZERO spawn tiles (BuildRegionSpawners already warned) — never spawns.
            }

            if (IsPlayerWithinRegionSpawnExclusion(tile))
            {
                continue; // Skip, don't queue (§3/§8 E2) — the next attempt (one pacing window later) tries the NEXT tile.
            }

            SpawnMonsterForRegion(spawner, tile, overgrown);
        }
    }

    // "No spawn within 6 units of a player" (§3/§8 E2) — mirrors FindMonsterAggroTarget's exact idiom: a coarse
    // Chebyshev/tile pre-filter via the spatial grid (a strict superset of the Euclidean exclusion disc), then an
    // exact Euclidean distance test on the returned candidates' Position.
    private bool IsPlayerWithinRegionSpawnExclusion(TileCoord tile)
    {
        var gatherRadiusTiles = (int)Math.Ceiling(RegionSpawnPlayerExclusionRadius) + 1;
        _zone.World.GatherInterestCandidates(tile, gatherRadiusTiles, _regionSpawnPlayerScratch);

        var tileCenter = WorldVector.FromTile(tile);
        var thresholdSq = RegionSpawnPlayerExclusionRadius * RegionSpawnPlayerExclusionRadius;
        foreach (var candidate in _regionSpawnPlayerScratch)
        {
            if (candidate.Kind != EntityKind.Player)
            {
                continue;
            }

            if ((candidate.Position - tileCenter).LengthSquared < thresholdSq)
            {
                return true;
            }
        }

        return false;
    }

    // ECOLOGY E2 (D10 "/clearspawners clears BOTH kinds cleanly"): despawns EVERY region spawner's live monsters
    // (an admin clear, not a kill — mirrors the legacy loop in HandleClearSpawnersCommand exactly: same leak-free
    // cleanup, no corpse, no ecology stock/pressure change). The RegionSpawner itself (its derived tiles, its
    // pacing state) is NEVER removed — a region×type is a permanent slot, unlike a legacy MonsterSpawner. Returns
    // the number of monsters actually despawned (folded into the command's total).
    private int ClearRegionSpawnerMonsters()
    {
        var despawnedCount = 0;
        foreach (var spawner in _regionSpawners)
        {
            if (spawner.LiveCount == 0)
            {
                continue;
            }

            // Snapshot to an array first — spawner.ClearLiveMonsters() below empties the very set this loop would
            // otherwise be iterating.
            foreach (var monsterId in spawner.LiveMonsterIds.ToArray())
            {
                if (_zone.Despawn(monsterId, out var removed))
                {
                    _actionExecutor.ClearEntity(monsterId);
                    _contributionLedger.Forget(monsterId);
                    if (_monsterTypeOf.TryGetValue(monsterId, out var typeToForget))
                    {
                        ResolveBehavior(typeToForget).Forget(monsterId);
                    }
                    else
                    {
                        _defaultBehavior.Forget(monsterId);
                    }

                    _monsterTypeOf.Remove(monsterId);
                    _networkIds.Return(removed.NetworkId);
                    despawnedCount++;
                }

                _regionSpawnerOfMonster.Remove(monsterId);
            }

            spawner.ClearLiveMonsters();
        }

        return despawnedCount;
    }

    // LIVING-ENEMIES P3: a monster DIED (HP hit 0 from a player attack). Despawn the entity (EntityDespawn to AOI
    // viewers + remove from the world/spatial index), clean up its AI + type state (the cleanup the P3 follow-up asked
    // for — no leak), and notify its spawner to schedule the respawn. The persistent spawner + its red marker stay.
    private void KillMonster(WorldEntity monster)
    {
        var monsterId = monster.Id;
        var deathTile = monster.TileCoord;

        // Remove from the world (also unhooks the spatial index). Do NOT free the network id here: RollAndSpawnCorpse
        // below rents an id and the pool REUSES freed ids, so freeing now lets the corpse rent the just-despawned
        // monster's SAME id. An in-AOI client still "knows" that id, so EnsureEntitySpawns skips the corpse's spawn
        // (KnowsEntity == true) and the corpse NEVER RENDERS (it exists server-side, so its loot window still opens —
        // the "no corpse on an in-AOI death" bug). Free it at the END, after the corpse has rented a fresh id.
        var despawned = _zone.Despawn(monsterId, out var removed);
        if (despawned)
        {
            // Drop any action state (cooldowns) for the dead monster so the cooldown map can't leak and a reused id
            // can't inherit a stale cooldown (N-action-cooldown-prune).
            _actionExecutor.ClearEntity(monsterId);
        }

        // LOOT P4b: roll this monster's loot and spawn a CORPSE at the death tile holding it, tagged with the
        // eligible-looter set (the contribution ledger's contributors) + the loot mode + a decay deadline. Done
        // BEFORE the type/ledger cleanup below (it reads the type's LootTableId + the ledger). Nothing is delivered
        // to any inventory here — a player loots the corpse by interacting with it.
        if (_monsterTypeOf.TryGetValue(monsterId, out var deadType))
        {
            RollAndSpawnCorpse(deadType, monsterId, deathTile);
        }

        // LOOT P4b: forget this monster's contribution ledger entry (snapshotted onto the corpse above) so the
        // ledger is cleaned up with the monster and never leaks — alongside the AI/type cleanup below.
        _contributionLedger.Forget(monsterId);

        // Clean up AI + type membership (fixes the former add-only leak — see todo/monster-types-followups.md P2/P3).
        // MONSTER-BEHAVIOR P3: resolve the dead monster's behavior from its type BEFORE removing the type below — at
        // Forget time the type is still in _monsterTypeOf, so the right brain (matching the same id that Register'd it)
        // forgets its per-monster state. (Order is load-bearing: remove the type first and the resolve would fall back
        // to the default brain, which for the single registered basicRoamer is the same instance, but stays correct as
        // P4 adds more brains.)
        if (_monsterTypeOf.TryGetValue(monsterId, out var typeToForget))
        {
            ResolveBehavior(typeToForget).Forget(monsterId);
        }
        else
        {
            _defaultBehavior.Forget(monsterId);
        }

        _monsterTypeOf.Remove(monsterId);

        // Notify the owning spawner so it schedules a respawn after the type's delay (read live). D10: a
        // region-spawned monster is NEVER in _spawnerOfMonster (SpawnMonsterForRegion registers it into
        // _regionSpawnerOfMonster instead), so exactly one of these two branches ever fires for a given monster.
        if (_spawnerOfMonster.Remove(monsterId, out var spawner))
        {
            var respawnTicks = _monsterTypes.RespawnTicks(spawner.Type);
            spawner.NotifyMonsterDied(monsterId, _serverTick, respawnTicks);
            Log.Info($"Monster {removed?.NetworkId} (spawner #{spawner.SpawnerId}) died; respawn in {respawnTicks} ticks.");
        }
        else if (_regionSpawnerOfMonster.Remove(monsterId, out var regionSpawner))
        {
            // ECOLOGY E2 (D1/D3 kill hook): a region-spawned monster died — permanently decrement its region×type's
            // stock by 1 (clamped at the D3 floor) and add 1 pressure (the "recently hunted" decaying memory).
            // ContributionLedger/loot flow above are UNTOUCHED — this only feeds the ecology math. NO legacy
            // respawn timer for a region-spawned monster: repopulation flows from the stock via
            // MaterializeRegionSpawners only (D1 "the timer-respawn model is deleted for ecology types").
            regionSpawner.RemoveLiveMonster(monsterId);
            _ecology.RecordKill(regionSpawner.RegionId, regionSpawner.TypeId);
            Log.Info(
                $"Monster {removed?.NetworkId} ({regionSpawner.RegionId}/{regionSpawner.TypeId}) died; " +
                $"ecology stock={_ecology.StockOf(regionSpawner.RegionId, regionSpawner.TypeId):0.###}, " +
                $"pressure={_ecology.PressureOf(regionSpawner.RegionId, regionSpawner.TypeId):0.###}.");
            // ECOLOGY E4: a kill can only ever change ITS OWN region×type, so check just that one region rather
            // than the full-registry scan EcologyTick does.
            if (_ecology.Registry.TryGet(regionSpawner.RegionId, out var killedRegion))
            {
                CheckRegionEcologyChange(killedRegion);
            }
        }

        // Now free the monster's network id — the corpse (if one spawned) has already rented a DIFFERENT id, so the
        // pool's reuse can't hand the corpse the still-known monster id and get its spawn skipped on in-AOI clients.
        if (despawned)
        {
            _networkIds.Return(removed.NetworkId);
        }
    }

    // LOOT P4b: roll the dead monster's loot table and, if it yielded anything, spawn a replicated Corpse entity at
    // the death tile holding the rolled stacks SERVER-SIDE. The corpse is tagged with the eligible-looter set (the
    // contribution ledger's contributors — solo = the killer), the loot mode (FfaAmongEligible default), and a decay
    // deadline (now + the live corpse-decay duration). An empty LootTableId or a roll that dropped nothing spawns NO
    // corpse (a no-loot kill is silent), so corpses only appear where there is something to take.
    private void RollAndSpawnCorpse(MonsterType type, ulong monsterId, TileCoord deathTile)
    {
        if (string.IsNullOrEmpty(type.LootTableId))
        {
            return;
        }

        var stacks = _lootTables.Roll(type.LootTableId, _lootRng);
        if (stacks.Count == 0)
        {
            return;
        }

        // The eligible-looter set = the contributors recorded as they damaged the monster (solo = just the killer).
        var eligible = _contributionLedger.Contributors(monsterId);

        // The death tile is where the monster stood (always walkable — it occupied it), so the corpse spawns there.
        // It is a transient world entity of EntityKind.Corpse, so it AOI-replicates + renders + is interactable
        // through the existing paths with no new replication fork. DisplayName drives the client's visual choice
        // (falls back to the Box archetype for a non-Player/Resource kind today).
        var corpseEntity = _zone.SpawnTransient(_networkIds.Rent(), EntityKind.Corpse, "Corpse", deathTile, Direction8.S);
        var decayAtTick = _serverTick + _tuning.CorpseDecayTicks;
        var corpse = new Corpse(corpseEntity.Id, stacks, eligible, LootMode.FfaAmongEligible, decayAtTick);
        _corpses[corpseEntity.Id] = corpse;

        Log.Info($"[LOOT P4b] {type.Id} died at {deathTile}; spawned corpse #{corpseEntity.NetworkId} holding " +
                 $"{corpse.Contents.Count} stack(s), eligible={eligible.Count}, decay@{decayAtTick}.");
    }

    // LOOT P4b: per-tick decay pass. Despawns any corpse whose decay deadline has arrived even if it still holds
    // unlooted loot (UO-style). Removing the entity from the world makes the AOI pass send EntityDespawn to viewers
    // (the SAME exit path a looted/forgotten entity uses) and frees the network id. Polls the tiny corpse set; no-op
    // when empty. The decay duration is a live-tunable global (loot.corpseDecayMs).
    private void DecayCorpses()
    {
        if (_corpses.Count == 0)
        {
            return;
        }

        _corpseDecayScratch.Clear();
        foreach (var corpse in _corpses.Values)
        {
            if (corpse.IsDecayed(_serverTick))
            {
                _corpseDecayScratch.Add(corpse.EntityId);
            }
        }

        foreach (var corpseId in _corpseDecayScratch)
        {
            DespawnCorpse(corpseId);
            Log.Info($"[LOOT P4b] corpse (entity {corpseId}) decayed unlooted; despawned.");
        }
    }

    // LOOT P4b/P4c: removes a corpse's world entity + its server-side loot payload, freeing the network id. The AOI
    // pass turns the world removal into an EntityDespawn for viewers. Shared by the loot-empties path (instant) and the
    // decay pass. LOOT P4c: also CLOSES the loot window for any session that had this corpse open (forgets the pairing
    // + sends Open=false) so no client is left with a window for a gone corpse. Idempotent-ish: a missing id is a no-op.
    private void DespawnCorpse(ulong corpseEntityId)
    {
        if (!_corpses.Remove(corpseEntityId))
        {
            return;
        }

        // Capture the network id BEFORE despawning so we can address the close to the viewers' windows.
        var corpseNetworkId = _zone.World.TryGet(corpseEntityId, out var corpseEntity) ? corpseEntity.NetworkId : 0u;
        foreach (var session in _sessions.Values)
        {
            if (session.OpenCorpseEntityId == corpseEntityId)
            {
                session.SetOpenCorpse(null);
                SendCorpseClosed(session, corpseNetworkId);
            }
        }

        if (_zone.Despawn(corpseEntityId, out var removed))
        {
            _networkIds.Return(removed.NetworkId);
        }
    }

    // LIVING-ENEMIES P3: per-tick spawner respawn pass. For each spawner whose respawn delay has elapsed, spawn a fresh
    // full-HP monster of its type at its tile. Iterates the (tiny) spawner set directly; SpawnMonsterForSpawner clears
    // the schedule via AttachMonster. The marker is already present (it persisted across the death), so a respawn just
    // re-adds a roaming monster under the existing red tile.
    private void RespawnMonsters()
    {
        if (_spawners.Count == 0)
        {
            return;
        }

        foreach (var spawner in _spawners.Values)
        {
            if (spawner.IsRespawnDue(_serverTick))
            {
                SpawnMonsterForSpawner(spawner);
            }
        }
    }

    // S60 live-tuning handler. ADMIN-GATED (the same role check as /speed and /metrics): a non-admin
    // request is ignored and logged, never applied. An admin request is looked up in the registry, which
    // clamps/validates and applies it to the mutable ServerTuning holder live; the next AOI pass / step
    // reads the new value. Unknown keys are ignored + logged. No echo message in v1 — the client shows the
    // value it sent (note: post-clamp authoritative value is only in the server log). No persistence.
    private void HandleAdminSetTuning(ClientSession sender, string key, double value)
    {
        if (sender.Role != ClientRole.Admin)
        {
            Log.Warn($"Denied AdminSetTuning from non-admin {sender.DisplayName}: {key}={value}.");
            return;
        }

        // LIVING-ENEMIES P2-POLISH: per-monster-TYPE keys ("<typeId>.<field>", e.g. slime.roamRadius) are owned by the
        // monster-type registry (the per-type tuning replaced the former global monster.* block). Try it first; on a
        // hit, broadcast the replicated per-type snapshot so the F1 Monster tab re-seeds to the post-clamp values.
        if (_monsterTypes.TryApply(key, value, out var monsterApplied))
        {
            BroadcastMonsterTuning();
            // LIVESPEED-DESYNC: the AI paces a monster off its TYPE's LIVE MoveSpeedMultiplier each tick, but the
            // monster ENTITY's SpeedMultiplier (the source of EntitySpawn / MovementSpeedChanged cadence the client
            // interpolates at) is copied only once at spawn. Editing "<typeId>.moveSpeed" therefore re-paced the
            // server AI while the client kept interpolating at the stale spawn cadence → a growing desync. Re-push the
            // edited type's MoveSpeedMultiplier onto every already-spawned monster of that type and re-broadcast the
            // cadence (reusing the player /speed path), so AI ticks, entity SpeedMultiplier, and client interpolation
            // stay in lockstep on a live edit. Re-applying an unchanged multiplier is a no-op and broadcasts nothing,
            // so this safely runs after ANY of the type's edits, not just moveSpeed.
            PropagateMonsterTypeSpeedToSpawned(key);
            SendSystem(sender, $"tuning: {key} = {ServerTuningRegistry.Format(monsterApplied)} (applied live).");
            Log.Info($"{sender.DisplayName} set tuning {key}={ServerTuningRegistry.Format(monsterApplied)} (requested {value}).");
            return;
        }

        if (!ServerTuningRegistry.TryApply(_tuning, key, value, out var applied))
        {
            Log.Warn($"Ignored AdminSetTuning from {sender.DisplayName}: unknown/invalid key '{key}' (value {value}).");
            return;
        }

        // Interest radius feeds the precomputed AOI query box; recompute it so a larger live radius still
        // gathers the full candidate superset (a smaller one just queries a tighter box). Cheap + only on
        // apply, never per tick. (The base step cooldown is no longer a live knob — SPEED1 pinned it.)
        if (key == ServerTuningRegistry.InterestRadiusKey)
        {
            _aoiQueryRadiusTiles = ResolveAoiQueryRadiusTiles(_tuning.InterestRadius);
        }

        // COMBAT-TUNING: a combat.* change must reach every client so the wedge mesh, swing-root prediction, and
        // radial cooldown viz re-derive from the new authoritative values — broadcast the full replicated snapshot.
        if (ServerTuningRegistry.IsCombatKey(key))
        {
            BroadcastCombatTuning();
        }

        SendSystem(sender, $"tuning: {key} = {ServerTuningRegistry.Format(applied)} (applied live).");
        Log.Info($"{sender.DisplayName} set tuning {key}={ServerTuningRegistry.Format(applied)} (requested {value}).");
    }

    // MONSTER-TUNING-SAVE: PERSIST the current live-tuned monster TYPE values back to the data manifest so they survive a
    // restart (AdminSetTuning is in-memory only — lost on restart). ADMIN-GATED with the EXACT same role check as
    // HandleAdminSetTuning — this WRITES A FILE from a network command, so the gate is the security boundary; a non-admin
    // send is logged + ignored (no write). On an admin Save we serialize every live type via MonsterTypeRegistry.
    // ToManifestJson (the faithful inverse of the loader) and write the file LoadMonsterTypes reads. Wrapped in try/catch
    // (in TrySaveMonsterTypes) so an IO error logs + replies but never crashes the tick loop. No AI/tuning behaviour
    // changes — Save only mirrors the current values to disk.
    private void HandleSaveMonsterTuning(ClientSession sender)
    {
        if (sender.Role != ClientRole.Admin)
        {
            Log.Warn($"Denied SaveMonsterTuning from non-admin {sender.DisplayName}.");
            return;
        }

        var path = MonsterManifestPath;
        if (TrySaveMonsterTypes(_monsterTypes, path, out var error))
        {
            SendSystem(sender, $"saved monster tuning to {path}");
            Log.Info($"{sender.DisplayName} saved monster tuning to {path}.");
        }
        else
        {
            SendSystem(sender, $"save FAILED: {error}");
            Log.Warn($"Failed to save monster tuning to {path}: {error}.");
        }
    }

    // MONSTER-TUNING-SAVE: serialize the registry to the manifest JSON shape and write it to `path`, returning false +
    // the error message on any IO/serialization failure (so the caller logs + replies but the tick loop never crashes on
    // a bad disk). `internal` + path-parameterised so it is unit-testable against a TEMP path without touching the live
    // Content/monsters.json (the network handler always passes MonsterManifestPath, the file a restart loads).
    internal static bool TrySaveMonsterTypes(MonsterTypeRegistry registry, string path, out string error)
    {
        // ATOMIC WRITE: serialize to a temp file IN THE SAME DIRECTORY, then File.Move(overwrite) it into place —
        // an atomic same-volume replace. A crash mid-write can no longer leave a half-written (corrupt)
        // monsters.json for the next startup to choke on; the worst case is a stray .tmp file (best-effort deleted
        // on failure below; harmless if a hard crash orphans one — LoadMonsterTypes only reads the exact manifest name).
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, registry.ToManifestJson());
            File.Move(temp, path, overwrite: true);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // Best-effort cleanup only — the original error is the one worth reporting.
            }

            error = ex.Message;
            return false;
        }
    }

    // Replicates an entity's new effective cadence to every authenticated viewer whose AOI currently
    // includes it (the entity's owner included). Reliable-ordered, like spawn/despawn — speed stays OFF the
    // hot snapshot path. Only viewers that already know the entity are notified; a viewer that has not yet
    // been sent the EntitySpawn will receive the up-to-date cadence in that spawn instead.
    private void BroadcastMovementSpeedChanged(WorldEntity entity, ushort stepCooldownMs)
    {
        var message = new MovementSpeedChangedMessage(entity.NetworkId, stepCooldownMs);
        foreach (var session in _sessions.Values)
        {
            if (!session.IsAuthenticated || !session.KnowsEntity(entity.NetworkId))
            {
                continue;
            }

            if (!TryGetSessionEntity(session, out var viewerEntity))
            {
                continue;
            }

            if (IsEntityInInterest(viewerEntity, entity, session, _tuning.InterestRadius))
            {
                TrySend(session.Peer, message, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    // LIVESPEED-DESYNC: after a per-type tuning edit ("<typeId>.<field>"), re-sync the edited type's already-spawned
    // monsters so their ENTITY SpeedMultiplier (the EntitySpawn / MovementSpeedChanged cadence source the client
    // interpolates at) matches the type's LIVE MoveSpeedMultiplier the AI now paces off (StepMonsterAi reads it fresh
    // each tick via EffectiveStepCooldownTicksFor). Without this the AI re-paced while clients kept interpolating at
    // the stale spawn cadence → the "VERY desynced" slime. We resolve the edited type from the key, then for each
    // spawned monster OF THAT TYPE re-apply the multiplier and, only when it actually changed, re-broadcast the new
    // effective cadence via the SAME player /speed path (so the client re-quantises its interpolation identically).
    // TrySetSpeedMultiplier is a no-op + returns false when unchanged, so a non-moveSpeed edit costs only the walk.
    private void PropagateMonsterTypeSpeedToSpawned(string key)
    {
        var dot = key.IndexOf('.');
        if (dot <= 0 || dot >= key.Length - 1)
        {
            return;
        }

        if (!_monsterTypes.TryGet(key[..dot], out var editedType))
        {
            return;
        }

        foreach (var entity in _zone.World.Entities)
        {
            if (entity.Kind != EntityKind.Monster)
            {
                continue;
            }

            // Compare by reference: _monsterTypeOf stores the SAME MonsterType instance the registry mutates in place,
            // so == is exact identity with the just-edited type (no id re-parse needed).
            if (!_monsterTypeOf.TryGetValue(entity.Id, out var type) || !ReferenceEquals(type, editedType))
            {
                continue;
            }

            if (entity.TrySetSpeedMultiplier(editedType.MoveSpeedMultiplier))
            {
                // Keep the tiles/sec speed stat tracking the retuned multiplier — LIVE for players in Phase 1.
                RefreshSpeedStat(entity);
                BroadcastMovementSpeedChanged(entity, EffectiveStepCooldownMs(entity));
            }
        }
    }

    // COMBAT-S1 dev-set handler. ADMIN-GATED (same role check as /speed and AdminSetTuning): a non-admin request
    // is ignored and logged, never applied. An admin request sets the CALLER's own local-player vital current
    // value (server clamps to [0, max] inside TrySetStatCurrent); on a real change the authoritative vitals are
    // replicated back to the owner via PlayerStatsMessage so the HUD bars track it. No damage/regen — this is the
    // Stage-1 hook to watch the bars move end-to-end.
    private void HandleAdminSetStat(ClientSession sender, byte stat, int value)
    {
        if (sender.Role != ClientRole.Admin)
        {
            Log.Warn($"Denied AdminSetStat from non-admin {sender.DisplayName}: stat={stat} value={value}.");
            return;
        }

        if (stat > (byte)StatKind.Stamina)
        {
            Log.Warn($"Ignored AdminSetStat from {sender.DisplayName}: unknown stat {stat}.");
            return;
        }

        if (!TryGetSessionEntity(sender, out var entity))
        {
            return;
        }

        if (entity.TrySetStatCurrent((StatKind)stat, value))
        {
            SendPlayerStats(sender, entity);
            Log.Info($"{sender.DisplayName} set stat {(StatKind)stat}={value} (now {entity.Stats}).");
        }
    }

    // COMBAT-S2B: server-authoritative resolution of an attack action. The first real combat path. Flow:
    //   1. Dedup on the session's OWN attack cursor (_lastAttackSeq, ClientSession) — entirely separate from the
    //      movement cursors. A stale/duplicate attack seq is rejected here and never resolves.
    //   2. Resolve the attacker entity; reject if it is still on its INDEPENDENT per-entity attack cooldown
    //      (WorldEntity.TryBeginAttack, ~600 ms) — the cooldown cannot be bypassed by spamming (a rejected attack
    //      mutates nothing and the cursor is already burned, so the re-send is also deduped).
    //   3. FREEAIM: decode the client's continuous aim angle and resolve a GEOMETRIC SECTOR (half-angle + radius)
    //      about the attacker's world position — replacing the facing-derived tile cone. The aim is a client-chosen
    //      continuous value the server validates purely by geometry (like the move direction), so it stays
    //      server-authoritative.
    //   4. Apply the live combat.damage to each ENEMY whose tile-centre falls in the sector — Dummy/Npc only, NEVER another
    //      Player (no friendly fire) and never the attacker itself. The reduced HP rides the existing 2a public-HP
    //      snapshot field, so the target's overhead bar drops automatically (no dedicated reply). HP may reach 0.
    private void HandleAttack(ClientSession session, uint sequence, AttackKind kind, ushort aimAngle, uint authoredTick)
    {
        // (1) Own attack cursor dedup — PARALLEL to, but independent of, the movement cursors. A stale or duplicate
        // attack seq is dropped before any cooldown/cone work. The cursor advances even on a later cooldown reject,
        // so a re-sent (already-seen) attack can never resolve twice.
        if (!session.TryConsumeAttackSequence(sequence))
        {
            return;
        }

        if (!TryGetSessionEntity(session, out var attacker))
        {
            return;
        }

        // Only the melee sector exists this stage; the codec already range-validated the kind, so anything else is a
        // future kind we don't resolve yet.
        if (kind != AttackKind.MeleeCone)
        {
            return;
        }

        // (2) Independent per-entity attack cooldown. A still-cooling attacker is rejected and changes nothing.
        // COMBAT-TUNING: read the cooldown LIVE from _tuning (combat.attackCooldownMs) so an admin tweak takes effect
        // on the next attack; the replicated snapshot keeps the client's radial cooldown indicator matching it.
        if (!attacker.TryBeginAttack(_serverTick, _tuning.AttackCooldownTicks))
        {
            return;
        }

        // SWING-COMMIT: an ACCEPTED swing briefly ROOTS the attacker's MOVEMENT (a committed swing) by bumping its
        // next-eligible MOVEMENT tick forward (a FLOOR, never a shorten). Same machinery as the step cooldown, so
        // the client predictor mirrors it exactly (MmoClient.SendAttack -> LocalPlayerPredictor) using the SAME
        // CombatTuning.RootTicks math.
        //
        // SWING-COMMIT-FIX: anchor the root on the CLIENT's AUTHORED tick (carried on the wire), not on _serverTick
        // (the RECEIVE tick). Under latency the server receives the attack ~d ticks after the client sent + rooted
        // its predictor, so a receive-tick anchor ends the server's root LATER than the predictor's → the predictor
        // steps before the server's root expires → reject → rubberband. Anchoring both sides on the same authored
        // tick (clamped to a window around _serverTick so a hostile client can't root far in the future/past, exactly
        // like TryCommitStepAuthored bounds its authored tick) makes the two root windows identical. This MIRRORS the
        // NET3 authored-tick step commit — same estimator on the client, same clamp bounds on the server.
        // COMBAT-TUNING: the swing-root duration is now the LIVE combat.rootMs (via _tuning.AttackRootTicks, which
        // uses the SAME CombatTuning.RootTicks conversion the client predictor mirrors off the replicated rootMs) —
        // so steady-state both sides root for the identical window. A brief transient mismatch right as rootMs is
        // tweaked is acceptable for a dev tuning tool (the per-client snapshot lands a frame later).
        attacker.ApplyAttackMovementRootAuthored(
            authoredTick,
            _serverTick,
            _tuning.AttackRootTicks,
            AuthoredTickPastWindow,
            AuthoredTickFutureLead);

        // (3+4) FREEAIM: resolve the geometric sector + apply damage. The whole resolution (the radius/angle test
        // against entity world positions, the grid occupancy query, the friendly-fire gate, and the damage) lives in
        // the testable static FreeAimSectorResolver — HandleAttack only owns the cursor dedup + cooldown gate and the
        // aim decode around it.
        // COMBAT-TUNING: half-angle / radius / damage are read LIVE from _tuning (combat.halfAngleDeg /
        // combat.radiusTiles / combat.damage) — the SAME values replicated to the client so the drawn wedge equals
        // the resolved danger area.
        var aimRadians = AimAngle.ToRadians(aimAngle);
        var damage = _tuning.AttackDamage;
        var hits = FreeAimSectorResolver.ResolveAndDamage(
            _zone.World,
            attacker,
            aimRadians,
            _tuning.FreeAimHalfAngleRadians,
            _tuning.FreeAimRadiusTiles,
            damage,
            _attackCandidateScratch,
            _damagedVictimScratch,
            // BOSS-2 (P1): the boss PLATING modifier — the SAME uniform hook the projectile/tether/detonation seam
            // (ApplyProjectileDamage) applies, so a plated Sunderer takes reduced MELEE damage too. Inert for non-boss.
            (victim, dmg) => _bossEncounter.ModifyIncomingDamage(victim.Id, dmg));
        if (hits > 0)
        {
            // COMBAT-QOL: float a cosmetic damage number over each victim that actually lost HP. AOI-gated to the
            // victim's viewers, EXCLUDING the attacker's OWN session — the attacker now PREDICTS its own numbers
            // client-side (instant on swing, no round-trip), so re-sending the server event would double the number.
            // Other AOI observers have no prediction, so they still receive it. Cosmetic only — the authoritative HP
            // already rode the snapshot via ApplyDamage. Regen never reaches here, so only real damage pops a number.
            foreach (var damaged in _damagedVictimScratch)
            {
                BroadcastDamageEvent(damaged.Victim, damaged.Amount, session);

                // LOOT P4b: record this attacker as a contributor to each MONSTER it damaged (the eligibility
                // groundwork). Keyed by the monster's entity id + the attacker's durable CharacterId, so on death the
                // contributor set becomes the corpse's eligibleLooters. Only monsters yield loot, so only they are
                // ledgered; a Dummy/Npc hit is ignored here.
                if (damaged.Victim.Kind == EntityKind.Monster && attacker.CharacterId is { } contributorId)
                {
                    _contributionLedger.RecordDamage(damaged.Victim.Id, contributorId, damaged.Amount);
                }
            }

            Log.Info($"{session.DisplayName} free-aim hit {hits} target(s) for {damage} each (aim {aimRadians:F2} rad).");

            // LIVING-ENEMIES P3: a MONSTER victim whose HP hit 0 DIES. Check after the damage numbers (so the final hit
            // still floats) and after the loop (the scratch buffer is reused inside KillMonster's despawn path). Only
            // monsters die from a player attack here; dummies floor at 0 + regen, players can't hit players.
            foreach (var damaged in _damagedVictimScratch)
            {
                if (damaged.Victim.Kind == EntityKind.Monster && damaged.Victim.Stats.Health <= 0)
                {
                    KillMonster(damaged.Victim);
                }
            }
        }
    }

    // MOVEMENT-ACTIONS Phase B1: handle an inbound ActionIntentMessage — the player-triggered movement action (jump).
    // Mirrors HandleAttack's shape: dedup on the DEDICATED action cursor, resolve the entity + def, validate can-act,
    // and start the action through the SAME ServerActionExecutor a Phase-A test / Phase-C AI drives. NO PREDICTION in
    // B1 — the action is server-executed and the local jump is server-confirmed (slightly delayed under latency, which
    // is intentional for B1). The action is ANCHORED AT THE SERVER RECEIPT TICK (_serverTick); `authoredTick` rides
    // the wire for B2 to consume (it will anchor the trajectory to the client's logical tick, like the swing-commit
    // fix) but B1 deliberately does NOT use it — no EstimateServerTick, no tick estimator (that is a B2 decision after
    // a measurement). The suppression seam (HandleMoveIntent skips integration while IsActive) + the StepAll tick-loop
    // call already exist from Phase A and are UNTOUCHED here.
    private void HandleActionIntent(ClientSession session, uint actionSeq, byte actionId, ushort heading, uint authoredTick)
    {
        // (1) Own action cursor dedup — PARALLEL to but independent of BOTH the movement and attack cursors (NET6: a
        // third stream gets a third cursor). A stale/duplicate action seq is dropped before any work. The cursor
        // advances even on a later can-act reject, so a re-sent already-seen trigger can never start twice.
        if (!session.TryConsumeActionSequence(actionSeq))
        {
            return;
        }

        // B1 anchors the action server-side; the authored tick only rides the wire for B2. Bind it so the unused
        // parameter is explicit (it is NOT consumed — no tick estimator in B1).
        _ = authoredTick;

        if (!TryGetSessionEntity(session, out var entity))
        {
            return;
        }

        // (2) Resolve the def from the SHARED registry. An unknown id (no registered def — a corrupt byte or a future
        // action's byte arriving early) is dropped, exactly like an unhandled attack kind. The codec passes the raw
        // byte through, so the validation is here against the live registry. Phase D: Jump, Charge and DodgeRoll all
        // resolve now — the two dashes ride this SAME handler with zero handler changes (the framework payoff).
        if (!_actionRegistry.TryGet((ActionId)actionId, out var def))
        {
            return;
        }

        // (3) can-act: a downed player never reaches here (dispatch suppresses ActionIntent while dead), but guard the
        // SESSION-level alive gate anyway; the ENTITY-level gates (one-at-a-time + cooldown + movement-root) are the
        // executor's CanStart, the single source of that truth (design §2.8 / §1.1 / §2.1 "not rooted"). A trigger
        // arriving while an action already owns the entity, inside its cooldown, or while the entity is swing-rooted
        // is rejected — no second start, no queue, no root-escape-by-jumping.
        if (session.IsDead)
        {
            return;
        }

        if (!_actionExecutor.CanStart(entity, def, _serverTick))
        {
            return;
        }

        // (4) Decode the launch heading (the wire bearing, reusing the AimAngle quantization) to a unit vector and
        // start the action ANCHORED AT THE SERVER RECEIPT TICK. The executor owns the entity's movement for the
        // action's duration (HandleMoveIntent already suppresses normal integration while IsActive — the Phase-A
        // seam); StepAll advances it each tick.
        var headingVector = AimAngle.ToUnitVector(heading);
        _actionExecutor.TryStart(entity, def, headingVector, _serverTick);
    }

    // COMBAT-QOL: send a cosmetic DamageEventMessage for `victim` to every authenticated viewer whose AOI currently
    // includes it — the SAME viewer-scoping as BroadcastMovementSpeedChanged, so a damage number appears exactly
    // where the entity is replicated and nowhere else. UNRELIABLE: a dropped number is harmless (the next snapshot
    // already carries the true HP), and reliable retransmit would only add latency. A viewer that does not yet know
    // the entity is skipped — it has no visual to float a number over and the spawn carries the HP.
    //
    // FREEAIM-PREDICT: `excludeSession` (the ATTACKER) is skipped — the attacker PREDICTS its own damage numbers
    // client-side (instant on swing), so sending it the event too would pop a SECOND number. Other observers have no
    // such prediction, so they still get the event. Pass null to broadcast to every viewer (no exclusion).
    private void BroadcastDamageEvent(WorldEntity victim, int amount, ClientSession? excludeSession = null)
    {
        var newHealth = (ushort)Math.Clamp(victim.Stats.Health, 0, ushort.MaxValue);
        var message = new DamageEventMessage(victim.NetworkId, amount, newHealth);
        foreach (var session in _sessions.Values)
        {
            if (ReferenceEquals(session, excludeSession))
            {
                continue;
            }

            if (!session.IsAuthenticated || !session.KnowsEntity(victim.NetworkId))
            {
                continue;
            }

            if (!TryGetSessionEntity(session, out var viewerEntity))
            {
                continue;
            }

            if (IsEntityInInterest(viewerEntity, victim, session, _tuning.InterestRadius))
            {
                TrySend(session.Peer, message, DeliveryMethod.Unreliable);
            }
        }
    }

    // Replicates an entity's authoritative vitals to its OWNER. Owner-only + reliable-ordered, like the inventory
    // snapshot — vitals stay off the hot snapshot path. Sent on login (initial truth) and on every change.
    private void SendPlayerStats(ClientSession session, WorldEntity entity)
    {
        TrySend(session.Peer, new PlayerStatsMessage(entity.Stats), DeliveryMethod.ReliableOrdered);
    }

    // COMBAT-TUNING: replicate the current combat feel-knobs to one client. Reliable-ordered, like PlayerStats —
    // sent on login (initial truth) and (via BroadcastCombatTuning) on every combat.* change. The snapshot is the
    // single source the client mirrors; its wedge/predictor/cooldown all re-derive from it.
    private void SendCombatTuning(ClientSession session)
    {
        TrySend(session.Peer, new CombatTuningMessage(_tuning.CombatSnapshot), DeliveryMethod.ReliableOrdered);
    }

    // COMBAT-TUNING: push the current combat snapshot to every authenticated client. Called when a combat.* tuning
    // key changes so all clients re-derive the wedge/predictor/cooldown from the new authoritative values. Combat
    // tuning is global (not per-entity/AOI-scoped), so every authenticated session gets it regardless of AOI.
    private void BroadcastCombatTuning()
    {
        var message = new CombatTuningMessage(_tuning.CombatSnapshot);
        foreach (var session in _sessions.Values)
        {
            if (session.IsAuthenticated)
            {
                TrySend(session.Peer, message, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    // LIVING-ENEMIES P2-POLISH: replicate the per-monster-TYPE tuning to one client (login initial truth). Reliable-
    // ordered, like SendCombatTuning. The client mirrors it so the F1 Monster tab shows + edits the live values.
    private void SendMonsterTuning(ClientSession session)
    {
        TrySend(session.Peer, new MonsterTuningMessage(_monsterTypes.BuildSnapshot()), DeliveryMethod.ReliableOrdered);
    }

    // LIVING-ENEMIES P2-POLISH: push the current per-type tuning to every authenticated client. Called when a
    // "<typeId>.<field>" key changes so any open F1 Monster tab re-seeds to the post-clamp values. Global (not
    // AOI-scoped), like BroadcastCombatTuning — every authenticated session gets it.
    private void BroadcastMonsterTuning()
    {
        var message = new MonsterTuningMessage(_monsterTypes.BuildSnapshot());
        foreach (var session in _sessions.Values)
        {
            if (session.IsAuthenticated)
            {
                TrySend(session.Peer, message, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    // PLAYER-COLLISION-TOGGLE: admin-gated handler for the live player↔player collision flip. ADMIN-GATED with the same
    // role check as HandleAdminSetTuning — it affects EVERYONE (a non-admin send is logged + ignored). On an admin flip
    // we write the authoritative flag onto the Zone (the source of truth GatherEntityObstacles reads) and broadcast the
    // new value to ALL clients so every client predictor's obstacle gather and the server integrator's gather flip
    // TOGETHER (prediction parity). Monster collision is unaffected. A no-op flip (same value) still re-broadcasts so any
    // just-opened panel re-seeds to the truth — cheap, rare, reliable.
    private void HandleAdminSetPlayerCollision(ClientSession sender, bool enabled)
    {
        if (sender.Role != ClientRole.Admin)
        {
            Log.Warn($"Denied AdminSetPlayerCollision from non-admin {sender.DisplayName}: enabled={enabled}.");
            return;
        }

        _zone.PlayerCollisionEnabled = enabled;
        BroadcastPlayerCollisionSetting();
        SendSystem(sender, $"player↔player collision: {(enabled ? "ON" : "OFF")} (applied live).");
        Log.Info($"{sender.DisplayName} set player↔player collision = {enabled}.");
    }

    // PLAYER-COLLISION-TOGGLE: replicate the authoritative player↔player collision flag to one client (login initial
    // truth). Reliable-ordered, like SendMonsterTuning. The client mirrors it so its obstacle gather gates on the same
    // value the server does.
    private void SendPlayerCollisionSetting(ClientSession session)
    {
        TrySend(session.Peer, new PlayerCollisionSettingMessage(_zone.PlayerCollisionEnabled), DeliveryMethod.ReliableOrdered);
    }

    // PLAYER-COLLISION-TOGGLE: push the current player↔player collision flag to every authenticated client. Called when
    // an admin flips it so every client's obstacle gather re-gates on the new authoritative value (prediction parity).
    // Global (not AOI-scoped), like BroadcastCombatTuning — every authenticated session gets it.
    private void BroadcastPlayerCollisionSetting()
    {
        var message = new PlayerCollisionSettingMessage(_zone.PlayerCollisionEnabled);
        foreach (var session in _sessions.Values)
        {
            if (session.IsAuthenticated)
            {
                TrySend(session.Peer, message, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    // NODE-FIELD N2 (docs/node-field-design.md D3/D4): one node's flip, GLOBAL (not AOI-scoped) — the design
    // doc's rationale: at community scale a harvest event is tiny (~5 bytes) and player-paced, so per-session
    // AOI diffing buys nothing over telling everyone. Mirrors BroadcastPlayerCollisionSetting's shape exactly.
    private void BroadcastNodeState(int nodeIndex, bool depleted)
    {
        // NodeCatalog.Build enforces the ushort entry cap, so every valid index fits.
        var message = new NodeStateMessage((ushort)nodeIndex, depleted);
        foreach (var session in _sessions.Values)
        {
            if (session.IsAuthenticated)
            {
                TrySend(session.Peer, message, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    // NODE-FIELD N2 (D4): the login snapshot of the field's live exceptions — only the currently-DEPLETED
    // indices (typically a handful among thousands), so a joining client's rendered field starts correct
    // without a per-node payload. Mirrors SendRegionEcology's "full truth on login" pattern.
    private void SendNodeStateBatch(ClientSession session)
    {
        TrySend(session.Peer, new NodeStateBatchMessage(_nodeField.DepletedIndices()), DeliveryMethod.ReliableOrdered);
    }

    // ECOLOGY E4 (docs/ecology-v1-design.md §3/§8 E4): replicate the FULL authored region set to one client — one
    // RegionEcologyMessage per region (mirrors the design's "full set on login"; each region is its own message,
    // never a bulk list, so a single region's later re-send is byte-identical in shape). Reliable-ordered, sent
    // once on login (initial truth) — the minimap has every region's legible state before the client ever moves.
    private void SendRegionEcology(ClientSession session)
    {
        foreach (var region in _ecology.Registry.Regions)
        {
            TrySend(session.Peer, EcologyWire.BuildMessage(_ecology, region), DeliveryMethod.ReliableOrdered);
        }
    }

    // ECOLOGY E4 (D6c): on login completion, announce the SINGLE most extreme authored region as one system
    // chat line — "max distance from Healthy in either direction; ties -> first" (EcologyLegibility.
    // DistanceFromHealthy over each region's WORST type-state). A no-op if no regions are authored at all.
    private void SendEcologyLoginRumor(ClientSession session)
    {
        EcologyRegion? mostExtreme = null;
        var bestDistance = -1;
        foreach (var region in _ecology.Registry.Regions)
        {
            var distance = EcologyLegibility.DistanceFromHealthy(EcologyWire.WorstStateOf(_ecology, region));
            if (distance > bestDistance)
            {
                mostExtreme = region;
                bestDistance = distance;
            }
        }

        if (mostExtreme is null)
        {
            return;
        }

        var worst = EcologyWire.WorstStateOf(_ecology, mostExtreme);
        SendSystem(session, EcologyRumors.LineFor(mostExtreme.DisplayName, worst));
    }

    // ECOLOGY E4: re-send ONE region's current legible state to every authenticated client. Called only when
    // CheckRegionEcologyChange finds an actual per-type state DIFFERENCE against the last-broadcast cache — state
    // flips are rare (D2), so this carries ~zero steady-state traffic. Global (not AOI-scoped, like
    // BroadcastPlayerCollisionSetting) — legibility is a pre-walk read, every client needs every region regardless
    // of proximity.
    private void BroadcastRegionEcology(EcologyRegion region)
    {
        var message = EcologyWire.BuildMessage(_ecology, region);
        foreach (var session in _sessions.Values)
        {
            if (session.IsAuthenticated)
            {
                TrySend(session.Peer, message, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    // ECOLOGY E4: the state-flip DETECTOR. Compares `region`'s CURRENT per-type state against the last-broadcast
    // cache (_lastSentEcologyState); if ANY type differs, re-sends the region (BroadcastRegionEcology) and — in
    // the SAME pass — brings the cache current for every type in the region (not just the one that changed), so
    // later calls never re-diff against a stale value. Called after each EcologyTick (once per authored region —
    // growth can move any/all of them) and after each RecordKill (once, for just the killed monster's region —
    // a kill can only ever change that one region×type) and after a forced /ecology set|pressure (so an admin's
    // live force-set is visible immediately, same "live, no restart" philosophy as the other admin tuning knobs).
    private void CheckRegionEcologyChange(EcologyRegion region)
    {
        var cache = _lastSentEcologyState[region.Id];
        var changed = false;
        foreach (var typeId in region.Types.Keys)
        {
            var current = _ecology.StateOf(region.Id, typeId);
            if (!cache.TryGetValue(typeId, out var previous) || previous != current)
            {
                changed = true;
            }

            cache[typeId] = current;
        }

        if (changed)
        {
            BroadcastRegionEcology(region);
        }
    }

    private string FormatWho()
    {
        var players = _sessions.Values
            .Where(session => session.IsAuthenticated)
            .OrderByDescending(session => session.Role)
            .ThenBy(session => session.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(session => $"{session.DisplayName}({session.Role}, {session.LastLatencyMs}ms)")
            .ToArray();

        return players.Length == 0
                ? "who: no authenticated players."
                : $"who: {string.Join(", ", players)}";
    }

    private void SendSystem(ClientSession session, string text)
    {
        TrySend(session.Peer, new ChatBroadcastMessage("server", text), DeliveryMethod.ReliableOrdered);
    }

    private ClientRole ResolveRole(string accountName, string displayName)
    {
        return _options.AdminNames.Contains(accountName) || _options.AdminNames.Contains(displayName)
            ? ClientRole.Admin
            : ClientRole.Player;
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        value = value.Trim();
        var lower = value.ToLowerInvariant();

        if (lower.EndsWith("ms", StringComparison.Ordinal)
            && double.TryParse(lower[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
        {
            duration = TimeSpan.FromMilliseconds(milliseconds);
            return true;
        }

        if (lower.EndsWith("s", StringComparison.Ordinal)
            && double.TryParse(lower[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            duration = TimeSpan.FromSeconds(seconds);
            return true;
        }

        if (lower.EndsWith("m", StringComparison.Ordinal)
            && double.TryParse(lower[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
        {
            duration = TimeSpan.FromMinutes(minutes);
            return true;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bareSeconds))
        {
            duration = TimeSpan.FromSeconds(bareSeconds);
            return true;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out duration))
        {
            return true;
        }

        duration = TimeSpan.Zero;
        return false;
    }

    private static TimeSpan ClampDuration(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalSeconds < 60
            ? $"{duration.TotalSeconds:0.#}s"
            : $"{duration.TotalMinutes:0.#}m";
    }

    private bool TrySend(NetPeer peer, IProtocolMessage message, DeliveryMethod deliveryMethod)
    {
        try
        {
            var packet = _messageEncodeBuffer.Encode(message);
            peer.Send(packet.Buffer.AsSpan(0, packet.Length), 0, deliveryMethod);
            _metrics.RecordSent(message, packet.Length);
            return true;
        }
        catch (Exception exception)
        {
            _metrics.RecordSendFailure();
            Log.Warn($"Failed to send {message.Type} to {FormatPeer(peer)}: {exception.Message}");
            return false;
        }
    }

    private bool TrySend(NetPeer peer, byte[] packet, int length, DeliveryMethod deliveryMethod)
    {
        try
        {
            peer.Send(packet.AsSpan(0, length), 0, deliveryMethod);
            return true;
        }
        catch (Exception exception)
        {
            _metrics.RecordSendFailure();
            Log.Warn($"Failed to send {length} bytes to {FormatPeer(peer)}: {exception.Message}");
            return false;
        }
    }

    private bool TrySend(NetPeer peer, EncodedPacket packet, DeliveryMethod deliveryMethod, MessageType messageType)
    {
        try
        {
            peer.Send(packet.Buffer.AsSpan(0, packet.Length), 0, deliveryMethod);
            _metrics.RecordSent(messageType, packet.Length);
            return true;
        }
        catch (Exception exception)
        {
            _metrics.RecordSendFailure();
            Log.Warn($"Failed to send {messageType} to {FormatPeer(peer)}: {exception.Message}");
            return false;
        }
    }

    private static string FormatPeer(NetPeer peer)
    {
        return $"{peer.Address}:{peer.Port}";
    }

    private void DrainMainThreadActions()
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Log.Error("Main-thread action failed", exception);
            }
        }
    }

    private long DrainPendingMovementElapsedTicks()
    {
        var elapsedTicks = _pendingMovementElapsedTicks;
        _pendingMovementElapsedTicks = 0;
        return elapsedTicks;
    }

    // Minimum/maximum effective step cooldown expressed in TICKS, derived once from the ms clamp and the
    // configured tick rate. The per-entity cadence (base ÷ speed multiplier) is clamped to this range so a
    // silly multiplier can never break the tick loop. (S51)
    private uint MinEffectiveStepCooldownTicks =>
        (uint)Math.Max(1, (int)Math.Ceiling(MinEffectiveStepCooldownMs / (1000d / _options.TickRate)));

    private uint MaxEffectiveStepCooldownTicks =>
        (uint)Math.Max(1, (int)Math.Ceiling(MaxEffectiveStepCooldownMs / (1000d / _options.TickRate)));

    // The entity's clamped effective step cooldown in TICKS (what the step loop enforces) — base cooldown
    // ÷ the entity's speed multiplier, clamped to the tick bounds. Default multiplier 1.0 returns the
    // global StepCooldownTicks unchanged (behaviour parity with pre-S51).
    private uint EffectiveStepCooldownTicks(WorldEntity entity) =>
        entity.EffectiveStepCooldownTicks(
            _tuning.StepCooldownTicks,
            MinEffectiveStepCooldownTicks,
            MaxEffectiveStepCooldownTicks);

    // LOOT P4c (monster-types follow-up #1): the effective step cooldown in TICKS for an arbitrary speed
    // MULTIPLIER, sharing the exact same base-cooldown ÷ multiplier + min/max clamp as the per-entity path
    // (WorldEntity.EffectiveStepCooldownTicks). StepMonsterAi feeds the monster's TYPE's LIVE MoveSpeedMultiplier
    // here each tick (not the entity's spawn-time-copied SpeedMultiplier), so editing "<typeId>.moveSpeed" on the
    // F1 Monster tab re-paces ALREADY-SPAWNED monsters on the next tick — consistent with the other live Tunables.
    // A non-positive multiplier (impossible after the registry's [0.1, 5] clamp) falls back to 1.0 defensively.
    private uint EffectiveStepCooldownTicksFor(double speedMultiplier)
    {
        var multiplier = speedMultiplier > 0 ? speedMultiplier : 1.0;
        var scaled = _tuning.StepCooldownTicks / multiplier;
        var ticks = (long)Math.Max(1, Math.Round(scaled, MidpointRounding.AwayFromZero));
        return (uint)Math.Clamp(ticks, (long)MinEffectiveStepCooldownTicks, (long)MaxEffectiveStepCooldownTicks);
    }

    // The entity's effective step cooldown in MS for the wire (EntitySpawn / MovementSpeedChanged). Derived
    // from the effective TICKS so it round-trips to the same tick count when the client re-quantises it via
    // MovementCadence.EffectiveStepCadenceMs — keeping server and client cadence in lockstep. Clamped to a
    // ushort (the wire field); the ms clamp keeps it well within range.
    private ushort EffectiveStepCooldownMs(WorldEntity entity)
    {
        var ms = EffectiveStepCooldownTicks(entity) * (1000d / _options.TickRate);
        return (ushort)Math.Clamp((int)Math.Round(ms, MidpointRounding.AwayFromZero), 1, ushort.MaxValue);
    }

    // Refresh an entity's tiles/sec speed stat from the base move speed × its current SpeedMultiplier. Called on
    // spawn + on every SpeedMultiplier change so the stat tracks the live multiplier. PLAYERS consume
    // SpeedUnitsPerSecond as the per-input continuous integrator's speed (base 1000/StepCooldownMs ⇒ one tile per
    // StepCooldownMs at multiplier 1.0, so a held cardinal crosses tiles at ≈ the old tile-step cadence). Monsters
    // keep the EffectiveStepCooldownTicks tile-step path, so for them this stat is dormant.
    private void RefreshSpeedStat(WorldEntity entity)
    {
        entity.SetSpeedUnitsPerSecond(_tuning.BaseMoveSpeedUnitsPerSecond * entity.SpeedMultiplier);
    }

    // CONTINUOUS MIGRATION (Phase 3, v36): integrate ONE per-input continuous MoveIntent on the RECEIVE path (the
    // experiment model). Only a FRESH input (InputSeq > session.LastInputSeq) is processed; a stale/duplicate input is
    // ignored. The fresh input ALWAYS advances LastInputSeq (so the snapshot ACKs it — the client trims its buffer),
    // then integrates by its OWN dt with the guards that formerly lived on the fixed-tick pass moved here:
    //   * dead player (downed) → no motion (the dead-player guard);
    //   * swing-root freeze (IsMovementFrozen) → the input ACKs but produces ZERO motion (the rooted player);
    //   * a (0,0) direction → STOP (zero velocity, IsMoving=false);
    //   * otherwise integrate Position by the dt-clamped velocity in the RAW direction (normalized by the integrator),
    //     through the shared swept-circle wall collision (Zone.IntegrateMovement).
    // ANTI-SPEEDHACK: the client-supplied dt is first SANITY-clamped to [0, MaxMoveInputDtSeconds], then debited
    // against the per-peer wall-clock dt BUDGET (ConsumeMoveDtBudget) — so the integration uses at most the dt the
    // budget allows. Over any window the peer's integrated sim-time cannot exceed real elapsed + the burst allowance.
    // MONSTERS are untouched (they never send MoveIntent; they tile-step via StepMonsterAi).
    private void HandleMoveIntent(ClientSession session, MoveIntentMessage intent)
    {
        if (!session.TryBeginMoveInput(intent.InputSeq, _serverTick))
        {
            return; // stale/duplicate input — ignore (no integrate, no re-ack).
        }

        if (!TryGetSessionEntity(session, out var entity))
        {
            return;
        }

        // The RAW client direction. A zero vector is the explicit STOP. NaN/Inf (tamper) collapses to a stop. The
        // continuous integrator (ComputeMoveDelta) scales the passed vector by SpeedUnitsPerSecond WITHOUT
        // normalizing, so NORMALIZE here — otherwise a (1,1) diagonal would travel √2 faster than a cardinal (the
        // whole point of Direction8.ToUnitVector in the old path). Magnitude therefore never throttles or boosts.
        var rawDir = new WorldVector(SanitizeAxis(intent.DirX), SanitizeAxis(intent.DirY));
        var moving = rawDir.LengthSquared > 0d;
        var unitDir = moving ? rawDir.Normalized() : WorldVector.Zero;

        // MOVEMENT-ACTIONS (Phase A, design §4): while a movement action OWNS this entity, normal input integration is
        // SUPPRESSED — the action drives the position. The input still ACKed (TryBeginMoveInput advanced the move
        // cursor above, so the buffer trims), it just produces NO motion, mirroring exactly how a rooted/dead player's
        // input "ACKs but produces zero motion". The executor's per-tick Step advances Velocity/Position; do NOT zero
        // its Velocity here. When the action ends, ordinary movement resumes from the action's end position. (No
        // trigger source in Phase A, so IsActive is always false today — this is the seam Phase B's predicted action
        // relies on.)
        if (_actionExecutor.IsActive(entity))
        {
            session.SetMoving(true);
            return;
        }

        // Guards: a DEAD (downed) player or a swing-root-frozen player ACKs the input but does NOT move. A (0,0)
        // input is a stop. In every non-moving case zero the velocity so the entity never glides.
        if (!moving || session.IsDead || entity.IsMovementFrozen(_serverTick))
        {
            session.SetMoving(false);
            entity.StopMovement();
            return;
        }

        // ANTI-SPEEDHACK: sanity-clamp the per-input dt, then debit the per-peer wall-clock budget. The integration
        // dt is whatever the budget allows (0 when a flood has drained it) — so a peer cannot out-integrate real time.
        var sanitizedDt = Math.Clamp(SanitizeAxis(intent.DtSeconds), 0d, MaxMoveInputDtSeconds);
        var dtSeconds = session.ConsumeMoveDtBudget(sanitizedDt);

        session.SetMoving(true);
        if (dtSeconds <= 0d)
        {
            // Budget exhausted (flood) — the input is acked + faces the direction (ComputeMoveDelta sets Velocity +
            // Facing), but a zero dt advances no distance. The entity holds position; no tile crossing.
            entity.ComputeMoveDelta(unitDir, 0d);
            return;
        }

        // Integrate through the shared swept-circle wall collision (the SAME walls + radius the Phase-4 predictor
        // will replay against). bodyRadius is read fresh from the live tuning so a continuous.bodyRadius retune
        // takes effect on the next input.
        if (_zone.IntegrateMovement(entity, unitDir, dtSeconds, _tuning.BodyRadiusUnits))
        {
            // A rounded-tile crossing — persist the new durable tile (mirrors the old tile-step path).
            MarkDirtyDurableTile(entity);
        }
    }

    // CONTINUOUS MIGRATION (Phase 3, v36): the per-tick housekeeping that replaced the fixed-tick player integrator.
    // Player integration is now 100% input-driven (HandleMoveIntent); this pass only (a) CREDITS each authenticated
    // session's anti-speedhack dt budget by the elapsed tick time (so the budget tracks real elapsed time) and (b)
    // does the keepalive/stop housekeeping for AOI/animation: a "moving" session that has gone silent past the
    // keepalive timeout (a wedged-but-connected client) is force-STOPPED (velocity zeroed, IsMoving cleared) so its
    // avatar doesn't appear stuck mid-stride and AOI/animation see it at rest. A dead/rooted player that is still
    // flagged moving is likewise zeroed. dtBudget credit is the FIXED tick interval (the server ticks at a fixed
    // cadence, so this equals real elapsed time over many ticks — and is deterministic for the flood test).
    private void CreditMoveDtBudgetsAndKeepalive()
    {
        // PREDICTION-REGRESSION FIX: credit the budget by the REAL elapsed wall-clock time since the previous credit
        // pass, NOT the fixed tick interval. A long tick (GC gen2 / the startup entities spike) makes the fixed
        // interval UNDER-credit the real time that actually elapsed — and the client, running independently, kept
        // sending real-time dt the whole stall, so the budget is left short and clamps those honest inputs (the
        // measured SOFT rubberband). Real elapsed refunds the full stall window. On a healthy tick this equals the
        // tick interval, so steady-state behaviour is unchanged; on a catch-up burst the FIRST tick credits the
        // whole real gap (capped at the burst allowance) and the back-to-back catch-up ticks credit ~0 — total
        // credit == real elapsed either way. The first-ever pass credits exactly one tick interval (no prior
        // timestamp), and the per-session seed still gives a fresh peer its initial burst allowance.
        var now = Stopwatch.GetTimestamp();
        double realElapsedSeconds;
        if (_lastBudgetCreditTimestamp == 0)
        {
            realElapsedSeconds = 1.0 / _options.TickRate;
        }
        else
        {
            realElapsedSeconds = Stopwatch.GetElapsedTime(_lastBudgetCreditTimestamp, now).TotalSeconds;
        }

        _lastBudgetCreditTimestamp = now;
        var keepaliveTimeoutTicks = (uint)Math.Max(1, (int)Math.Ceiling(MoveIntentKeepaliveTimeout.TotalMilliseconds / (1000d / _options.TickRate)));

        foreach (var session in _sessions.Values)
        {
            if (!session.IsAuthenticated)
            {
                continue;
            }

            // Credit the anti-speedhack budget by the REAL elapsed wall-clock time (capped at the burst allowance
            // inside CreditMoveDtBudget). The cap is unchanged, so the anti-speedhack bound (integrated sim-time <=
            // real elapsed + burst) holds — we only stopped UNDER-crediting honest play during server-tick stalls.
            session.CreditMoveDtBudget(realElapsedSeconds, MoveDtBurstAllowanceSeconds);

            if (!TryGetSessionEntity(session, out var entity))
            {
                continue;
            }

            // Keepalive/stop housekeeping. If the session is still flagged moving but has gone silent past the
            // timeout, or is dead/rooted, force it to rest so it neither animates as walking nor glides.
            var stale = _serverTick - session.LastMoveIntentTick >= keepaliveTimeoutTicks;
            if (session.IsMoving && (stale || session.IsDead || entity.IsMovementFrozen(_serverTick)))
            {
                session.ClearMoveIntent();
                entity.StopMovement();
            }
        }
    }

    // CONTINUOUS MIGRATION (Phase 3, v36): collapse a NaN/Inf wire axis (tamper / corruption) to 0 before it reaches
    // the integrator, so a hostile DirX/DirY/DtSeconds can never produce a NaN position or an unbounded step.
    private static double SanitizeAxis(float value)
    {
        return float.IsFinite(value) ? value : 0d;
    }

    // LIVING-ENEMIES P1 + CONTINUOUS MIGRATION (Phase 8): the per-tick monster AI pass (sibling to
    // StepHeldMovementIntents). For each Monster entity, the resolved IMonsterBehavior advances its Idle↔Roaming↔Chasing↔Returning
    // state machine and, when moving, HOPS once per move cadence toward its continuous nav target via the injected
    // HopLocomotion — a discrete collision-valid leap (Velocity stays Zero; the sparse-update jump is preserved). The
    // hop's own cadence gate (WorldEntity.TryBeginHop / _nextEligibleTick) paces the monster to one leap per cooldown
    // (fed each tick, leaps only on cadence), and the AI's pause timers keep it Idle (still) most of the time. The
    // per-type Tunables (Euclidean ranges) + the cadence are read fresh each tick so a "<typeId>.*" admin retune takes
    // effect immediately.
    //
    // Iterates the live entity collection directly (no per-tick allocation); the count is tiny. A hop mutates a
    // monster's Position and (on a tile cross) migrates its spatial-grid bucket but never adds/removes a dictionary
    // entry, so iterating Entities while stepping is safe — same as RegenEnemies.
    // MONSTER-SEPARATION (todo/N-monster-monster-collision-separation.md): the per-tick monster↔monster de-penetration
    // pass — gather the live monster participants into the reused buffer and run the separation primitive. Early-out
    // when there are <2 monsters (nothing can overlap), keyed off GameServer's own monster count (the same guard
    // StepMonsterAi uses). The primitive itself is a no-op when no pair overlaps, so the cost at rest is just the gather
    // + the spatial neighbour queries (grid-bounded, no O(n²) scan).
    private void SeparateMonsters()
    {
        if (_monsterTypeOf.Count < 2)
        {
            return;
        }

        _monsterSeparationScratch.Clear();
        _zone.World.CopyMonstersTo(_monsterSeparationScratch);
        // BOSS-4 REVIEW (LOW-1): a ROOTED Core-phase boss is a pillar, not a body to de-overlap — separation applies
        // half-penetration to BOTH parties, so enrage-trickle splinters (all converging on a player hugging the boss)
        // would otherwise walk the "rooted" boss off-centre cumulatively. Excluding it keeps the root exact; overlap
        // with the stationary boss body is visual-only and self-resolves when the other body moves on. The predicate
        // is false for every monster outside P3, so this is a no-op scan in the common case.
        _monsterSeparationScratch.RemoveAll(m => _bossEncounter.IsBossRooted(m.Id));

        _monsterSeparation.Separate(_monsterSeparationScratch);
    }

    private void StepMonsterAi()
    {
        // MONSTER-BEHAVIOR P3: skip the pass when there are no live monsters. Keyed off GameServer's OWN monster count
        // (_monsterTypeOf, set on spawn / removed on death) rather than any single behavior's tracked count, so the
        // guard is decoupled from which/how-many behavior instances exist (P4 adds a second brain — the count would
        // otherwise be split across them).
        if (_monsterTypeOf.Count == 0)
        {
            return;
        }

        // LIVING-ENEMIES P2-POLISH: each monster's Tunables come from ITS TYPE (read fresh from the live per-type
        // values so a "<typeId>.*" admin retune takes effect on the next tick), not a single global block. The
        // tick-quantised pause/cooldown/scan + the derived de-aggro hysteresis are computed by the type registry.
        // De-aggro range and the aggro-scan cadence stay DERIVED (coupled to their source values).
        // MONSTER-AI-DORMANCY (todo/monster-ai-dormancy.md, ecology-v1-design.md §8 E0): iterate the MONSTER-ONLY
        // index (WorldState.Monsters) instead of scanning every entity in the zone and filtering by Kind — the
        // O(all-entities) sweep the P1 review flagged. Every yielded entity is already Kind == Monster.
        foreach (var entity in _zone.World.Monsters)
        {
            // Resolve the monster's type (falls back to the default if somehow untracked — e.g. a legacy spawn).
            if (!_monsterTypeOf.TryGetValue(entity.Id, out var type))
            {
                type = _monsterTypes.Default;
            }

            // BOSS-4 (P3 root): the rooted Sunderer HOLDS at the arena centre — skip its chase brain entirely and zero
            // its velocity so the glider never walks/extrapolates. The encounter teleported it to centre at the P3 edge
            // and owns its position; the beam/knockback are the P3 kit (its melee is dormant while rooted). Inert for
            // every non-boss monster and for the boss before P3.
            if (_bossEncounter.IsBossRooted(entity.Id))
            {
                ResolveLocomotion(type).Stop(entity);
                continue;
            }

            // SLIME-FEEL-POLISH: the monster's HOP CADENCE (time between hop STARTS) is HopAirborneTicks + HopDelayTicks,
            // read fresh each tick so a "<typeId>.hop*" admin retune re-paces ALREADY-SPAWNED monsters next tick. The hop
            // starts at T, the executor keeps it airborne for HopAirborneTicks (lands at T+airborne), TryBeginHop then
            // arms _nextEligibleTick = T + (airborne+delay) so the monster sits IDLE on the ground for HopDelayTicks
            // before the next hop — DELAY is the real, tunable grounded rest the user asked for (the opaque move-speed
            // cadence is retired). NOTE: the entity's REPLICATED interp cadence (EntitySpawn / MovementSpeedChanged,
            // seeded from MoveSpeedMultiplier at spawn) is intentionally LEFT AS-IS and may differ from this hop cadence
            // — the slime is force-included densely every airborne tick (Phase C) and the interp is sample-driven, so the
            // nominal interp cadence differing from the hop cadence is tolerable.
            // MONSTER-AI-DORMANCY: DormancyRadius is NOT a per-type authored knob (BuildTunables never sets it) — it is
            // the live server-wide AOI interest radius (_tuning.InterestRadius, the SAME "can a player currently see
            // this" test replication already uses), stamped on via `with` after the per-type build.
            ResolveBehavior(type).StepMonster(
                entity,
                _serverTick,
                _monsterTypes.HopAirborneTicks(type) + _monsterTypes.HopDelayTicks(type),
                _monsterTypes.BuildTunables(type) with { DormancyRadius = _tuning.InterestRadius },
                ResolveLocomotion(type));
        }
    }

    // MONSTER-BEHAVIOR P3 (docs/monster-behavior-design.md): map a monster TYPE to its BEHAVIOR ("brain") via the
    // registry, keyed by the type's BehaviorId. An unknown/unregistered id falls back LOUD-BUT-SAFE to the "basicRoamer"
    // default (warn ONCE per distinct id, then carry on) — matching ResolveLocomotion + the manifest philosophy: a
    // typo'd id never crashes the tick loop, it degrades to the safe default. Today only "basicRoamer" is registered, so
    // every type resolves to the one shared BasicRoamerBehavior → byte-identical behavior; P4 registers a second brain.
    private IMonsterBehavior ResolveBehavior(MonsterType type)
    {
        if (_behaviors.TryGetValue(type.BehaviorId, out var behavior))
        {
            return behavior;
        }

        if (_warnedUnknownBehaviorIds.Add(type.BehaviorId))
        {
            Log.Warn($"unknown behaviorId '{type.BehaviorId}' for type '{type.Id}', falling back to basicRoamer");
        }

        return _defaultBehavior;
    }

    // MONSTER-BEHAVIOR P1 (docs/monster-behavior-design.md): map a monster TYPE to its LOCOMOTION ("body") via the
    // registry, keyed by the type's LocomotionId. An unknown/unregistered id falls back LOUD-BUT-SAFE to the "hop"
    // default (warn ONCE per distinct id, then carry on) — matching the manifest philosophy: a typo'd id never crashes
    // the tick loop, it degrades to the safe default. Today only "hop" is registered, so every type resolves to the one
    // shared HopLocomotion → byte-identical behavior; P2 registers GlideLocomotion and a type that selects it.
    private IMonsterLocomotion ResolveLocomotion(MonsterType type)
    {
        if (_locomotions.TryGetValue(type.LocomotionId, out var locomotion))
        {
            return locomotion;
        }

        if (_warnedUnknownLocomotionIds.Add(type.LocomotionId))
        {
            Log.Warn($"unknown locomotionId '{type.LocomotionId}' for type '{type.Id}', falling back to hop");
        }

        return _defaultLocomotion;
    }

    // MOVEMENT-ACTIONS (Phase C): the HopLocomotion's begin-hop seam — START the slime's hop as a REAL ballistic Jump on
    // the shared executor (design §3 "one Jump drives players + monsters"). The locomotion already chose the heading +
    // the clamped forward distance (the collision-valid decision); here we look up the monster's TYPE for its apex height
    // (HopHeightUnits) and per-hop AIRBORNE span (HopAirborneMs) and build a PER-HOP Jump def. DATA-DRIVEN tuning: the
    // arc spans HopAirborneTicks (a SHORT airborne span), NOT the whole move cadence — so the slime lands then RESTS on
    // the ground for the remainder of the cadence (fixing "hops too often"). `cooldownTicks` (the move cadence) is no
    // longer the arc length; it stays the locomotion's re-trigger gate (TryBeginHop). CooldownTicks = 0 on the def (the
    // AI's TryBeginHop cadence is the re-trigger gate, NOT the executor's own cooldown). A small record+closure alloc per
    // hop (~once per cadence per monster) — acceptable. Reuses ActionId.Jump (a per-entity cooldown key, no collision
    // with a player's jump). Returns the executor's TryStart result.
    // PLAYER↔MONSTER COLLISION: the monster-locomotion obstacle gather (injected into HopLocomotion + GlideLocomotion).
    // Fills `scratch` with the nearby PLAYER bodies (as Circles of the shared body radius) a monster move should collide
    // against, so a chasing monster STOPS at the player. The acting body is always a monster here (the locomotions run
    // only for monsters), so a plain Player-kind filter is the correct set; monster↔monster stays the separation pass.
    private void GatherPlayerObstacles(WorldVector start, WorldVector delta, double radius, List<ContinuousCollision.Circle> scratch)
    {
        FillPlayerCircles(start, delta, radius, scratch);
    }

    // PLAYER↔MONSTER COLLISION: the action-executor obstacle gather (injected into ServerActionExecutor). KIND-AWARE: a
    // MONSTER actor (a hop arc / charge dash) collides with nearby PLAYERS so it STOPS at the player.
    // MOVEMENT-ACTIONS Phase D: a PLAYER actor's GROUND DASH (charge / dodge-roll) now collides with the SAME body set
    // its walking integrate does — the Zone gather (monsters + other players when the toggle is on, self excluded,
    // stable Id order) — so the dash EARLY-STOPS at a body on the server exactly where the client predicts it (the
    // predictor feeds its per-frame obstacle set to action frames unconditionally, so the client was ALREADY stopping
    // at bodies mid-action; this closes the server half of that parity for the two new dashes). The player JUMP is
    // deliberately LEFT gathering nothing — the P5 status quo (a reviewed, shipped behavior; a predicted jump-vs-body
    // stop is a separate refinement) — so the jump path stays byte-identical to Phase B/C.
    private void GatherActionObstacles(WorldEntity actor, WorldVector start, WorldVector delta, double radius, List<ContinuousCollision.Circle> scratch)
    {
        scratch.Clear();
        if (actor.Kind != EntityKind.Monster)
        {
            // A player CHARGE / DODGE-ROLL collides with the walking body set (the shared Zone gather); a player JUMP
            // still avoids nothing (unchanged from P5). The executor is mid-Step for this actor, so ActiveAction is
            // exactly the def whose trajectory is being resolved.
            if (_actionExecutor.ActiveAction(actor.Id) is ActionId.Charge or ActionId.DodgeRoll)
            {
                _zone.GatherBodyObstacles(actor, start, radius, scratch);
            }

            return;
        }

        FillPlayerCircles(start, delta, radius, scratch);
    }

    // PLAYER↔MONSTER COLLISION: the shared body-circle gather — fills `scratch` (cleared first) with the live PLAYER
    // bodies near the swept move (start → start+delta) as Circles of `radius`. Queries the spatial grid for a tile box
    // sized to cover the move plus both bodies (a superset; the swept-circle resolver applies the exact circle-vs-circle
    // test). Deterministic order (the grid's stable candidate order). Reuses `_obstacleCandidateScratch`; NO per-tick
    // alloc beyond the caller's `scratch` growth.
    private void FillPlayerCircles(WorldVector start, WorldVector delta, double radius, List<ContinuousCollision.Circle> scratch)
    {
        scratch.Clear();

        // Box radius (tiles): the move length plus both body radii, rounded up with a +1 tile margin so the grid
        // superset never misses a player whose body just overlaps the swept circle at the far end of the step.
        var reachTiles = (int)Math.Ceiling(delta.Length + radius + radius) + 1;
        if (reachTiles < 1)
        {
            reachTiles = 1;
        }

        _zone.World.GatherInterestCandidates(start.ToTileRounded(), reachTiles, _obstacleCandidateScratch);
        for (var i = 0; i < _obstacleCandidateScratch.Count; i++)
        {
            var candidate = _obstacleCandidateScratch[i];
            if (candidate.Kind != EntityKind.Player)
            {
                continue;
            }

            var pos = candidate.Position;
            scratch.Add(new ContinuousCollision.Circle(pos.X, pos.Y, radius));
        }
    }

    private bool BeginMonsterHop(WorldEntity monster, WorldVector heading, double hopDistance, uint cooldownTicks, uint serverTick)
    {
        if (!_monsterTypeOf.TryGetValue(monster.Id, out var type))
        {
            type = _monsterTypes.Default;
        }

        // DATA-DRIVEN tuning (the "hops too often" fix): the arc spans HopAirborneMs (a SHORT airborne span), NOT the
        // whole move cadence (`cooldownTicks`). So the slime lands after HopAirborneTicks and RESTS on the ground for
        // (cadence − airborne) ticks before the next hop starts — the cadence (moveSpeed) still gates how OFTEN hops
        // begin (TryBeginHop arms it in the locomotion), this only controls how long each hop is in the air. If airborne
        // >= cadence the IsActive gate just makes the arc itself the effective cadence (safe; defaults keep real rest).
        var def = MovementActionRegistry.BuildForwardArcJump(
            ActionId.Jump,
            durationTicks: _monsterTypes.HopAirborneTicks(type),
            jumpHeight: type.HopHeightUnits,
            forwardDistanceUnits: hopDistance,
            cooldownTicks: 0,
            animationId: 1);

        return _actionExecutor.TryStart(monster, def, heading, serverTick);
    }

    // MONSTER-BEHAVIOR P5 (docs/monster-behavior-design.md): a fixed "fast dash" airborne span for the charge, in ms.
    // The charge is GROUNDED (jumpHeight 0) but the SAME ForwardArc executor primitive — DurationTicks is how long the
    // dash lasts; over it the monster covers ChargeDistanceUnits, so a short span = a FAST closing dash (e.g. 4 units
    // over 300 ms = ~13 u/s, well above the gnoll's ~3.6 u/s walk). Fixed (no per-type knob) for P5 — the human feel-test
    // + a later tuning pass can promote it to a knob if the dash wants to be faster/slower. The charge ANIMATION is P6.
    private const int MonsterChargeDurationMs = 300;

    // Tick-quantised charge dash span (Ceiling, >= 1 tick — same convention as the cooldowns), derived from the live
    // tick rate so the wall-clock dash duration is tick-rate-independent.
    private uint MonsterChargeDurationTicks =>
        (uint)Math.Max(1, (int)Math.Ceiling(MonsterChargeDurationMs / (1000d / _options.TickRate)));

    // MONSTER-BEHAVIOR P5: the brain's TryChargeDelegate — START a charge on `monster` toward `heading` (a unit vector
    // to its target) at `serverTick`. Resolves the monster's TYPE for the dash distance + the (tick-quantised) cooldown
    // and hands the actual MOTION to BeginMonsterCharge → the shared executor. The brain only decides WHEN; this owns
    // the type-param lookup + the HOW. Returns the executor's TryStart result (false ⇒ on cooldown / already acting →
    // the brain falls through to its normal approach). Only called for ChargeEnabled types, but resolves defensively.
    private bool TryBeginMonsterCharge(WorldEntity monster, WorldVector heading, double distanceToTarget, uint serverTick)
    {
        if (!_monsterTypeOf.TryGetValue(monster.Id, out var type))
        {
            type = _monsterTypes.Default;
        }

        // M1 (P5 review): CLAMP the dash to the actual gap (like HopLocomotion clamps to toTarget.Length) so the charge
        // lands ON/adjacent the target instead of overshooting PAST it (entities don't collide with each other, so an
        // unclamped fixed dash would carry the monster through and behind a nearer target). A far target (gap > the type
        // dash) still gets the full dash and the monster walks the remainder.
        return BeginMonsterCharge(
            monster,
            heading,
            Math.Min(type.ChargeDistanceUnits, distanceToTarget),
            MonsterChargeDurationTicks,
            _monsterTypes.ChargeCooldownTicks(type),
            serverTick);
    }

    // MONSTER-BEHAVIOR P5: START the monster's CHARGE as a REAL forward-arc action on the shared executor — the SAME
    // ForwardArc primitive the hop/player jump use, but GROUNDED (jumpHeight 0 → a flat fast forward dash, no Z arc).
    // Mirrors BeginMonsterHop EXCEPT: (a) ActionId.Charge (a distinct per-entity cooldown key from the hop's Jump), and
    // (b) the DEF carries `cooldownTicks` so the EXECUTOR's CanStart enforces the re-charge gate (the hop instead relies
    // on the locomotion's TryBeginHop cadence + a 0 def cooldown). The charge MOTION replicates via the existing action-
    // airborne dense-position force-include (forceActionAirborne = IsActive), exactly like the hop — NO protocol change
    // (ActionId.Charge is a pre-reserved wire byte). animationId 2 is a placeholder; the charge ANIMATION lands in P6.
    private bool BeginMonsterCharge(
        WorldEntity monster, WorldVector heading, double distanceUnits, uint durationTicks, uint cooldownTicks, uint serverTick)
    {
        var def = MovementActionRegistry.BuildForwardArcJump(
            ActionId.Charge,
            durationTicks: durationTicks,
            jumpHeight: 0d,                       // GROUNDED — a flat dash (BallisticArc gives a 0 height arc for H=0).
            forwardDistanceUnits: distanceUnits,
            cooldownTicks: cooldownTicks,         // the EXECUTOR enforces the charge cooldown (unlike the hop's cadence).
            animationId: 2);                      // placeholder; the charge animation is P6.

        var started = _actionExecutor.TryStart(monster, def, heading, serverTick);
        if (started)
        {
            // L1 (P5 review): zero the stale WALK velocity at charge start so the dash replicates like the hop — via the
            // dense action-airborne position force-include, with Velocity 0 — NOT a leftover ~walk-speed velocity the
            // remote would dead-reckon along under packet loss (wrong speed / one tick into a wall). The executor drives
            // the dash by position and leaves Velocity untouched, so it stays 0 for the dash. StopMovement's stop-edge
            // revision bump re-publishes the zeroed velocity on the walk→charge transition.
            monster.StopMovement();
        }

        return started;
    }

    // TELEGRAPH T1 (docs/ability-telegraph-sync-design.md): the brain's TrySlamDelegate — CAST the monster's slam by
    // SCHEDULING a circle telegraph LOCKED at the target's position AT CAST TIME (`targetPosition`, the position the
    // brain just resolved), resolving after the type's windup against positions AT that tick — locked origin +
    // resolve-time membership is exactly what makes it dodgeable. Resolves the monster's TYPE for the radius/windup/
    // damage (read fresh so a live retune applies to the next cast) and hands the schedule to the telegraph engine;
    // the brain owns the WHEN (in attack range + its own per-monster slam cooldown). The telegraph outlives a caster
    // that dies mid-windup (the scheduler's documented decision). Only called for SlamEnabled types, but resolves
    // defensively like the charge.
    //
    // SLIME-SLAM ROOT+LEAP (todo/S-slime-slam-root-and-leap.md): a successful cast now also hands the brain its CAST
    // PLAN (`cast`) — the wire-QUANTIZED locked origin plus the leap-start/resolve ticks — so the brain runs the
    // root-and-leap channel (rooted cast→leap-start, airborne leap-start→resolve, landing ON the resolve tick).
    //
    // LEAP TIMING MATH (pinned by the behavior tests): the tick loop runs StepMonsterAi BEFORE the executor's
    // StepAll, so a leap the brain starts at tick S takes its FIRST arc step that same tick and LANDS (i == D) on
    // tick S + D − 1; ResolveDue runs AFTER StepAll, so a landing on the resolve tick is on the ground when the
    // membership test runs — the landing IS the hit. Landing exactly on resolveTick therefore needs
    //   leapStartTick = resolveTick − D + 1,   D = min(HopAirborneTicks, windupTicks)
    // (capping D at the windup keeps leapStartTick strictly AFTER the cast tick even for a degenerate sub-airborne
    // windup; both terms are >= 1, so D >= 1 and the uint arithmetic can't wrap).
    //
    // REACHABILITY (the derived slam TRIGGER range — decided here, documented per the todo): the cast is DECLINED
    // when the locked origin is farther than the type's HopDistanceUnits from the caster, so the leap is ALWAYS
    // within one believable hop (the monster is rooted from cast, so the leap distance IS the cast distance). The
    // brain's trigger already requires the target within AttackRangeUnits, so the EFFECTIVE trigger range is
    // min(AttackRangeUnits, HopDistanceUnits) — for the slime both are 1.5, i.e. trigger-range ≈ hop-range (the
    // todo's preference) and live behavior is unchanged; a future type authored with attack range > hop range simply
    // waits for the target to close instead of launching a superhuman leap. A declined cast returns false (the brain
    // falls through to melee and re-tries next tick) — no telegraph is scheduled, so nothing false is advertised.
    private bool TryBeginMonsterSlam(WorldEntity monster, ulong targetId, WorldVector targetPosition, uint serverTick, out SlamCast cast)
    {
        cast = default;
        if (!_monsterTypeOf.TryGetValue(monster.Id, out var type))
        {
            type = _monsterTypes.Default;
        }

        if (!MonsterTypeRegistry.SlamEnabled(type))
        {
            return false;
        }

        // TELEGRAPH SHAPES WEDGE+LINE (docs/boss-encounter-sunderer-design.md): pick the slam SHAPE + the LEAP target by
        // the type's SlamShape selector. CIRCLE (the slime, default): the telegraph is LOCKED at the TARGET's cast
        // position and the leap lands ONTO it (unchanged — byte-identical). WEDGE (the Sunderer's Cleave): a 130° arc
        // whose APEX is the CASTER, aimed at the target's bearing at cast time — the boss stands and cleaves in front, so
        // the leap target is the caster's OWN position (an in-place hop). The reachability gate + cast plan below are
        // shared: cast.Origin is the quantized shape origin either way (leap-onto-target for the circle, leap-in-place
        // for the wedge), so the drawn shape, the resolve, and the landing all agree.
        var isWedge = string.Equals(type.SlamShape, "wedge", StringComparison.OrdinalIgnoreCase);
        TelegraphShape rawShape;
        WorldVector leapTarget;
        if (isWedge)
        {
            var toTarget = targetPosition - monster.Position;
            var aim = toTarget.LengthSquared > 0d
                ? Math.Atan2(toTarget.Y, toTarget.X)
                : Math.Atan2(monster.Facing.ToUnitVector().Y, monster.Facing.ToUnitVector().X);
            var halfAngle = type.SlamWedgeAngleDeg * 0.5d * Math.PI / 180d;
            rawShape = TelegraphShape.Wedge(monster.Position, type.SlamRadiusUnits, aim, halfAngle);
            leapTarget = monster.Position; // in-place cleave: the boss stands and swings, no lunge.
        }
        else
        {
            rawShape = TelegraphShape.Circle(targetPosition, type.SlamRadiusUnits);
            leapTarget = targetPosition;
        }

        // Reachability gate (see the header): decline a cast whose leap would exceed the type's hop range. For the wedge
        // the leap target is the caster itself (distance 0), so a wedge cleave is never declined for reach.
        if ((leapTarget - monster.Position).Length > type.HopDistanceUnits)
        {
            return false;
        }

        // GROUNDED gate (independent review of this change): a chasing slime can close to trigger range MID-HOP-ARC;
        // casting airborne would let the in-flight arc keep moving it for the remaining ticks AFTER the shape locks
        // — up to ~300 ms of drift inside the "rooted" channel. Decline and let the brain re-try next tick (it lands
        // within ≤6 ticks), so the root is literal: cast only ever happens standing still. Mirrors the leap's own
        // grounded requirement (the executor rejects a start while an action is active).
        if (_actionExecutor.IsActive(monster))
        {
            return false;
        }

        // Pre-quantize the shape to the EXACT wire/resolve geometry (Schedule re-quantizes — an idempotent no-op)
        // so the leap aims at the same center the client draws and the resolver tests: landing and shape agree.
        var shape = TelegraphScheduler.QuantizeToWire(rawShape);
        var windupTicks = _monsterTypes.SlamWindupTicks(type);
        var resolveTick = serverTick + windupTicks;
        var telegraphId = _telegraphs.Schedule(
            monster.Id,
            shape,
            serverTick,
            resolveTick,
            type.SlamDamage,
            $"{type.DisplayName} slam");

        var leapDurationTicks = Math.Min(_monsterTypes.HopAirborneTicks(type), windupTicks);
        cast = new SlamCast(shape.Origin, LeapStartTick: resolveTick - leapDurationTicks + 1, ResolveTick: resolveTick);
        // Logged (cooldown-paced, cannot spam) — the log is how a live test correlates cast/leap/resolve ticks.
        Log.Info(
            $"Monster {monster.NetworkId} ({type.Id}) cast slam #{telegraphId} at "
                + $"{shape.Origin.X:F2},{shape.Origin.Y:F2} r={type.SlamRadiusUnits} resolving at tick {resolveTick} "
                + $"(target #{targetId}, leap at tick {cast.LeapStartTick}).");
        return true;
    }

    // TELEGRAPH SHAPES WEDGE+LINE (docs/boss-encounter-sunderer-design.md, the Sunderer's Lunge): the brain's LUNGE
    // trigger — CAST a telegraphed LINE charge. Schedules a LINE telegraph LOCKED at the caster's cast position along the
    // bearing to the target (length = the planned dash distance = min(ChargeDistanceUnits, gap-to-target), half-width =
    // ChargeWidthUnits/2), resolving after ChargeWindupMs against positions AT the resolve tick, dealing ChargeDamage to
    // players still inside the corridor. Hands back a SlamCast whose Origin is the FAR END of the line: the SHARED slam
    // channel roots through the windup, then the leap DASHES the boss along the line to that far end, landing ON the
    // resolve tick — so the drawn corridor IS the swept path AND the hit test (honest), and the ~ChargeDamage rides the
    // telegraph RESOLVE (positions at T, line membership), never the dash body. Reuses BeginMonsterSlamLeap (the slam
    // leap) verbatim — a longer forward hop-arc — so no new motion primitive is needed; the reachability cap is skipped
    // (a lunge dash is longer than the hop range). Only called for LungeEnabled types; resolves defensively like the slam.
    private bool TryBeginMonsterLunge(WorldEntity monster, ulong targetId, WorldVector targetPosition, uint serverTick, out SlamCast cast)
    {
        cast = default;
        if (!_monsterTypeOf.TryGetValue(monster.Id, out var type))
        {
            type = _monsterTypes.Default;
        }

        if (!MonsterTypeRegistry.LungeEnabled(type))
        {
            return false;
        }

        // Grounded gate (like the slam): cast only while standing still so the LOCKED line matches the caster's position.
        if (_actionExecutor.IsActive(monster))
        {
            return false;
        }

        var toTarget = targetPosition - monster.Position;
        var distance = toTarget.Length;
        if (distance <= 0d)
        {
            return false; // target on top of the caster — no line direction; the brain falls through to its approach.
        }

        var aim = Math.Atan2(toTarget.Y, toTarget.X);
        var length = Math.Min(type.ChargeDistanceUnits, distance); // the planned dash distance (up to the type's reach).
        var halfWidth = type.ChargeWidthUnits * 0.5d;
        var shape = TelegraphScheduler.QuantizeToWire(TelegraphShape.Line(monster.Position, length, aim, halfWidth));
        var windupTicks = _monsterTypes.ChargeWindupTicks(type);
        var resolveTick = serverTick + windupTicks;
        var telegraphId = _telegraphs.Schedule(
            monster.Id, shape, serverTick, resolveTick, type.ChargeDamage, $"{type.DisplayName} lunge");

        // The leap DASHES to the FAR END of the LOCKED line (origin + aim·length, computed off the QUANTIZED shape so the
        // dash target sits on the drawn line's far edge), landing exactly ON the resolve tick: leapStart = resolve − D + 1,
        // D = min(HopAirborneTicks, windup) — the same landing math as the slam leap.
        var farEnd = shape.Origin + (new WorldVector(Math.Cos(shape.AimRadians), Math.Sin(shape.AimRadians)) * shape.Radius);
        var leapDurationTicks = Math.Min(_monsterTypes.HopAirborneTicks(type), windupTicks);
        cast = new SlamCast(farEnd, LeapStartTick: resolveTick - leapDurationTicks + 1, ResolveTick: resolveTick);
        Log.Info(
            $"Monster {monster.NetworkId} ({type.Id}) cast lunge #{telegraphId} from "
                + $"{shape.Origin.X:F2},{shape.Origin.Y:F2} len={shape.Radius:F2} w={type.ChargeWidthUnits} "
                + $"resolving at tick {resolveTick} (target #{targetId}, leap at tick {cast.LeapStartTick}).");
        return true;
    }

    // SLIME-SLAM ROOT+LEAP: the brain's BeginSlamLeapDelegate — START the slam LEAP as a REAL ballistic Jump on the
    // shared executor, aimed at the LOCKED (quantized) telegraph `origin`. PRESENTATION-THROUGH-SIMULATION: the leap
    // moves the body so the landing reads as the advertised hit, but resolve/damage stay the TelegraphScheduler's
    // (positions at T, center-point membership — this path deals NO damage). Mirrors BeginMonsterHop deliberately:
    // the SAME ActionId.Jump + hop height + animation, so the leap replicates through the EXACT channel real hops
    // already use (the action-airborne dense force-include — no new protocol, clients just see a hop).
    //
    // Duration: normally the plan's min(HopAirborneTicks, windup) — computed HERE from the ticks remaining so a
    // DEFERRED start (the executor declined at the planned tick — an in-flight pre-cast hop arc, or a pre-armed
    // longer cadence the root's max-floor could not shorten — and the brain retried) SHORTENS the arc toward the
    // deadline instead of landing late: started at S it lands at S + D − 1, so D = resolveTick − S + 1 lands exactly
    // ON resolveTick (floored at 1; a start AFTER the resolve tick — only reachable through pathological retries —
    // degrades to a 1-tick hop, still toward the origin). Distance = the full gap to the origin (reachability was
    // gated at cast, and the monster was rooted since, so this is <= the type's hop range); the executor re-resolves
    // the arc per tick against walls AND player bodies (GatherActionObstacles), so a player standing on the origin
    // stops the leap at their body exactly like a normal hop/charge — never a teleport, never an overlap.
    private bool BeginMonsterSlamLeap(WorldEntity monster, WorldVector origin, uint resolveTick, uint serverTick)
    {
        if (!_monsterTypeOf.TryGetValue(monster.Id, out var type))
        {
            type = _monsterTypes.Default;
        }

        var toOrigin = origin - monster.Position;
        var remainingTicks = resolveTick >= serverTick ? resolveTick - serverTick + 1u : 1u;
        var durationTicks = Math.Max(1u, Math.Min(_monsterTypes.HopAirborneTicks(type), remainingTicks));
        var def = MovementActionRegistry.BuildForwardArcJump(
            ActionId.Jump,
            durationTicks: durationTicks,
            jumpHeight: type.HopHeightUnits,
            forwardDistanceUnits: toOrigin.Length,
            cooldownTicks: 0,        // like the hop: the movement cadence (armed below), not the executor, re-gates.
            animationId: 1);         // the hop animation — the leap IS a hop, just aimed and timed.

        // A zero heading (already standing on the origin — e.g. the target cast-distance was ~0) is a legal in-place
        // hop: the executor treats a zero heading as no forward travel, so the slime still visibly hops the slam.
        var heading = toOrigin.LengthSquared > 0d ? toOrigin.Normalized() : WorldVector.Zero;
        if (!_actionExecutor.TryStart(monster, def, heading, serverTick))
        {
            return false; // declined (in-flight arc / still rooted past plan) — the brain retries next tick.
        }

        // Mirror BeginMonsterCharge's L1: zero any stale velocity so the leap replicates purely via the dense
        // action-airborne force-include (a no-op for the hop-bodied slime, safety for a future glider slammer).
        monster.StopMovement();

        // Arm the movement cadence like a normal hop (begin FIRST, then arm — the frozen/ready complement rule
        // HopLocomotion documents): airborne + the type's grounded rest, so the slime does not insta-hop the tick
        // after its slam landing but rests HopDelayTicks like any other hop.
        monster.TryBeginHop(serverTick, durationTicks + _monsterTypes.HopDelayTicks(type));
        return true;
    }

    // LIVING-ENEMIES P2: the AI's aggro-scan callback — find the NEAREST ALIVE PLAYER within `aggroRadius` (Chebyshev)
    // of `monster`, via the SAME spatial index the combat resolver uses (GatherInterestCandidates), so occupancy can
    // never diverge from AOI/replication. Players only (never another monster/dummy/resource), and only alive ones
    // (Health > 0 — a downed player at 0 HP is not a fresh aggro target; an already-chasing monster keeps attacking it
    // via the resolve path, but a NEW acquisition skips it). Returns the closest by Chebyshev distance, ties broken by
    // the smaller entity id for determinism. THROTTLED by the AI (it only calls this every ~0.5 s per monster), so the
    // per-tick scan the P1 review flagged is avoided.
    private bool FindMonsterAggroTarget(WorldEntity monster, int gatherRadius, out ulong targetId, out WorldVector targetPosition)
    {
        // CONTINUOUS MIGRATION (Phase 8): `gatherRadius` is the AI's COARSE tile/Chebyshev pre-filter (⌈Euclidean
        // aggro⌉ + 1) — a strict superset of the Euclidean aggro disc, so this gather drops no in-range target. The
        // precise Euclidean range test is done by the AI on the returned Position; here we just return the NEAREST
        // alive player by Euclidean distance on Position (ties → smaller id for determinism), and the AI accepts it
        // only if it is actually within the Euclidean aggro radius.
        _zone.World.GatherInterestCandidates(monster.TileCoord, gatherRadius, _monsterAggroScratch);

        var bestDistSq = double.MaxValue;
        WorldEntity? best = null;
        foreach (var candidate in _monsterAggroScratch)
        {
            if (candidate.Kind != EntityKind.Player || candidate.Stats.Health <= 0)
            {
                continue;
            }

            var distSq = (candidate.Position - monster.Position).LengthSquared;
            if (distSq < bestDistSq || (distSq == bestDistSq && (best is null || candidate.Id < best.Id)))
            {
                bestDistSq = distSq;
                best = candidate;
            }
        }

        if (best is null)
        {
            targetId = 0;
            targetPosition = default;
            return false;
        }

        targetId = best.Id;
        targetPosition = best.Position;
        return true;
    }

    // LIVING-ENEMIES P2: the AI's target-resolve callback — look up a chased target's CURRENT tile + alive flag so the
    // chase re-reads the player's live position each step and detects target-lost (despawn / logout) for de-aggro.
    // Returns false if the entity no longer exists at all; `alive` is its Health > 0 (a player whose HP hit 0 is still
    // resolvable but not alive → the AI de-aggros and returns home, since there is no death/respawn this phase).
    private bool TryResolveMonsterTarget(ulong targetId, out WorldVector targetPosition, out bool alive)
    {
        if (_zone.World.TryGet(targetId, out var target) && target.Kind == EntityKind.Player)
        {
            // CONTINUOUS MIGRATION (Phase 8): return the target's CONTINUOUS Position so the chase re-reads the
            // player's live sub-tile position each hop and the Euclidean de-aggro/leash test is exact.
            targetPosition = target.Position;
            alive = target.Stats.Health > 0;
            return true;
        }

        targetPosition = default;
        alive = false;
        return false;
    }

    // LIVING-ENEMIES P2: the AI's attack callback — the monster faces its target and deals `attackDamage` to the
    // PLAYER through the SAME ApplyDamage + DamageEventMessage path a player's attack uses (no combat fork). The AI
    // owns the WHEN (adjacency + the monster's own attack cooldown); this owns the HOW. The damage number is broadcast
    // to the victim AND nearby viewers — the victim is the PLAYER, NOT the attacker, so it is NOT excluded (it has no
    // client-side prediction of incoming damage, unlike its own outgoing swings). LIVING-ENEMIES P3: HP reaching 0 now
    // KILLS the player (marks the session dead + schedules the respawn); a dead/downed player is guarded out (no further
    // hits, no double-death) until RespawnPlayers teleports it back to spawn at full HP.
    private void ApplyMonsterAttack(WorldEntity monster, ulong targetId, int attackDamage)
    {
        if (!_zone.World.TryGet(targetId, out var target))
        {
            return;
        }

        // Face the victim (turn-in-place, no move) so the attack reads on the client; bumps StateRevision to replicate.
        // Sign-of-delta → the 8-direction facing (the same greedy mapping the chase step uses); same-tile leaves
        // facing unchanged (can't happen — the AI only attacks at Chebyshev >= 1, but guard anyway).
        if (TileDeltaToFacing(monster.TileCoord, target.TileCoord) is { } facing)
        {
            monster.TrySetFacing(facing);
        }

        // TELEGRAPH T1 (closes todo/N-iframe-gate-choke-point.md): the dead-guard + the dodge-roll i-frame gate +
        // ApplyDamage + the landed tail (damage broadcast + the death edge) now live in the SINGLE player-damage
        // choke point (PlayerDamageGate.TryDamagePlayer) this call routes through — the SAME seam the telegraph
        // resolve uses, so every current and future player-damage path shares ONE gate. Behaviourally identical to
        // the former inline sequence (same order, same log lines — the source string reproduces "Monster <netId>").
        _playerDamage.TryDamagePlayer(target, attackDamage, _serverTick, $"Monster {monster.NetworkId}");
    }

    // TELEGRAPH T1: the landed-damage tail of the choke point (PlayerDamageGate) — broadcast the damage number / HP
    // drop and handle the death edge. Extracted VERBATIM from ApplyMonsterAttack so the melee path is unchanged and
    // the telegraph resolve gets identical replication + death handling for free.
    private void OnPlayerDamageLanded(WorldEntity target, int amount, string source)
    {
        // Authoritative damage rides the snapshot HP field (the HUD bar falls); the event floats the number.
        // Broadcast to ALL viewers incl. the victim (it has no client-side prediction of incoming damage).
        BroadcastDamageEvent(target, amount);
        Log.Info($"{source} hit {target.DisplayName} for {amount} (hp now {target.Stats.Health}).");

        // LIVING-ENEMIES P3: HP hit 0 → the player DIES. Mark the session dead + schedule the respawn (a global
        // delay). The actual teleport-to-spawn + HP refill happens in the per-tick RespawnPlayers pass once the
        // delay elapses, so the gate's dead-guard window is honoured. MarkDead is a no-op if already dead.
        if (target.Stats.Health <= 0 && target.OwnerSession is { } session && session.MarkDead(_serverTick, _tuning.PlayerRespawnTicks))
        {
            SendSystem(session, "You died.");
            Log.Info($"{target.DisplayName} died; respawn in {_tuning.PlayerRespawnTicks} ticks.");
        }
    }

    // LIVING-ENEMIES P3: per-tick player respawn pass. For each dead session whose respawn delay has elapsed, teleport
    // its entity back to the spawn point, refill HP, replicate the new vitals to the owner, and clear the dead flag.
    // Minimal — no corpse/loot/penalty/death-screen (Phase 4+). The teleport rides the snapshot (the client sees the
    // jump) and SendPlayerStats refills the owner's HUD bar.
    private void RespawnPlayers()
    {
        foreach (var session in _sessions.Values)
        {
            if (!session.IsAuthenticated || !session.IsRespawnDue(_serverTick))
            {
                continue;
            }

            if (!TryGetSessionEntity(session, out var entity))
            {
                // Entity gone (disconnected mid-death) — just clear the flag so we stop polling it.
                session.MarkAlive();
                continue;
            }

            // AUTHORED-MAP M3 review fix: death respawn goes to the zone's SPAWN ANCHORS (the town plaza on the
            // authored map; the historical distribution grid on genVersion 1) — NOT the legacy DefaultSpawnTile,
            // which on the 384x384 world resolves to bare wilderness in the far southwest (it is walkable, so
            // ResolveSpawnTile returned it verbatim). Same round-robin the login path uses, so death and login
            // land players in the same place.
            var spawnTile = _zone.NextSpawnTile();
            _zone.Teleport(entity, spawnTile);
            entity.RestoreFullHealth();
            session.MarkAlive();
            // The teleport also resets the held move intent so the respawned player doesn't keep walking from a
            // pre-death key-hold.
            session.ClearMoveIntent();
            SendPlayerStats(session, entity);
            SendSystem(session, "You respawned.");
            Log.Info($"{session.DisplayName} respawned at {spawnTile}.");
        }
    }

    // LIVING-ENEMIES P2: the 8-direction facing from `from` toward `to` by the SIGN of each axis delta (the same
    // greedy mapping the monster behavior uses to step). Null only when the two tiles coincide (no facing). Server-local so
    // the server doesn't depend on the client's CursorHeading.
    private static Direction8? TileDeltaToFacing(TileCoord from, TileCoord to)
    {
        var sx = Math.Sign(to.X - from.X);
        var sy = Math.Sign(to.Y - from.Y);
        return (sx, sy) switch
        {
            (0, -1) => Direction8.N,
            (1, -1) => Direction8.NE,
            (1, 0) => Direction8.E,
            (1, 1) => Direction8.SE,
            (0, 1) => Direction8.S,
            (-1, 1) => Direction8.SW,
            (-1, 0) => Direction8.W,
            (-1, -1) => Direction8.NW,
            _ => null,
        };
    }

    private void MarkDirtyDurableTile(WorldEntity entity)
    {
        if (!entity.IsDurable || !entity.CharacterId.HasValue)
        {
            return;
        }

        // CONTINUOUS MIGRATION (Phase 10): checkpoint the entity's CONTINUOUS position (the float WorldVector), not
        // the rounded tile, so a relog restores the exact sub-tile spot. Still triggered only on a rounded-tile
        // crossing (the caller's existing cadence gate), so the write frequency is unchanged.
        _dirtyDurableTiles[entity.CharacterId.Value] = new PendingTileSave(
            entity.CharacterId.Value,
            entity.DisplayName,
            entity.Position);
    }

    // Single write-behind checkpoint for all durable per-character state. Gated to the configured
    // checkpoint cadence so no DB writes happen in the tick hot path; flushes dirty tiles and dirty
    // inventories together on the same boundary.
    private void CheckpointDirtyDurableState()
    {
        if (_serverTick < _nextPersistenceCheckpointTick)
        {
            return;
        }

        _nextPersistenceCheckpointTick = _serverTick + _options.PersistenceCheckpointTicks;

        FlushDirtyDurableTiles();
        FlushDirtyInventories();
        CheckpointEcologyPopulations();
    }

    private void FlushDirtyDurableTiles()
    {
        if (_dirtyDurableTiles.Count == 0)
        {
            return;
        }

        foreach (var save in _dirtyDurableTiles.Values)
        {
            _persistence.EnqueuePosition(save.CharacterId, save.DisplayName, save.Position);
        }

        _dirtyDurableTiles.Clear();
    }

    // Scans authenticated players' inventories and enqueues only those with pending changes (the
    // inventory itself tracks dirty template keys, so this is cheap when nothing changed). Draining
    // clears the inventory's dirty set, so each change is persisted at most once.
    private void FlushDirtyInventories()
    {
        foreach (var session in _sessions.Values)
        {
            if (!session.IsAuthenticated || !TryGetSessionEntity(session, out var entity))
            {
                continue;
            }

            FlushInventory(entity);
        }
    }

    private void FlushInventory(WorldEntity entity)
    {
        if (entity.Inventory is not { HasPendingChanges: true } inventory || !entity.CharacterId.HasValue)
        {
            return;
        }

        _persistence.EnqueueItems(entity.CharacterId.Value, entity.DisplayName, inventory.DrainDirtyKeys());
    }

    private void QueueConnectedPlayersForPersistence()
    {
        foreach (var session in _sessions.Values.Where(session => session.IsAuthenticated))
        {
            QueueTileSave(session);
        }
    }

    private void QueueTileSave(ClientSession session)
    {
        if (TryGetSessionEntity(session, out var entity))
        {
            QueueTileSave(session, entity.Position);
            FlushInventory(entity);
        }
    }

    // CONTINUOUS MIGRATION (Phase 10): queue the CONTINUOUS position (the float WorldVector) for write-behind —
    // disconnect/takeover/checkpoint flushes now persist the exact sub-tile spot, not the rounded tile.
    private void QueueTileSave(ClientSession session, WorldVector position)
    {
        _dirtyDurableTiles.Remove(session.CharacterId);
        _persistence.EnqueuePosition(session.CharacterId, session.DisplayName, position);
    }

    private readonly record struct PendingTileSave(Guid CharacterId, string DisplayName, WorldVector Position);

    // What a kicked session hands off to the login that took it over: its last tile and its live
    // in-memory inventory (both null when there was no existing session to kick).
    private readonly record struct TakeoverState(WorldVector? Position, Inventory? Inventory);
}

internal readonly record struct EncodedPacket(byte[] Buffer, int Length);

internal sealed class ProtocolEncodeBuffer
{
    private readonly MemoryStream _stream;
    private readonly BinaryWriter _writer;

    public ProtocolEncodeBuffer(int initialCapacity = 1500)
    {
        _stream = new MemoryStream(initialCapacity);
        _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
    }

    public EncodedPacket Encode(IProtocolMessage message)
    {
        Reset();
        ProtocolCodec.Encode(message, _writer);
        return Finish();
    }

    public EncodedPacket EncodeWorldSnapshot(
        uint serverTick,
        uint snapshotSequence,
        uint recipientStepSeq,
        uint lastInputSeq,
        int totalEntities,
        bool isComplete,
        int chunkIndex,
        int chunkCount,
        IReadOnlyList<EntityStateSnapshot> entities)
    {
        Reset();
        ProtocolCodec.EncodeWorldSnapshot(
            _writer,
            serverTick,
            snapshotSequence,
            recipientStepSeq,
            lastInputSeq,
            totalEntities,
            isComplete,
            chunkIndex,
            chunkCount,
            entities);
        return Finish();
    }

    public EncodedPacket EncodeEntitySpawn(
        uint networkId,
        Guid characterId,
        EntityKind kind,
        string displayName,
        TileCoord tile,
        Direction8 facing,
        ushort stepCooldownMs,
        uint tintRgb = 0xFFFFFFu,
        ushort scaleMilli = 1000)
    {
        Reset();
        ProtocolCodec.EncodeEntitySpawn(_writer, networkId, characterId, kind, displayName, tile, facing, stepCooldownMs, tintRgb, scaleMilli);
        return Finish();
    }

    public EncodedPacket EncodeEntityDespawn(uint serverTick, uint networkId)
    {
        Reset();
        ProtocolCodec.EncodeEntityDespawn(_writer, serverTick, networkId);
        return Finish();
    }

    private void Reset()
    {
        _stream.Position = 0;
        _stream.SetLength(0);
    }

    private EncodedPacket Finish()
    {
        _writer.Flush();
        return new EncodedPacket(_stream.GetBuffer(), checked((int)_stream.Length));
    }
}
