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

    private readonly uint _countdownTicks;
    private readonly uint _emptyResetTicks;
    private readonly uint _victoryEjectTicks;
    private readonly uint _fusionGoodWindowTicks;
    private readonly uint _fusionPerfectWindowTicks;
    private readonly uint _soloShatterWindowTicks;
    private readonly uint _droneFirstSpawnTicks;
    private readonly uint _droneRespawnTicks;

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

    public BossEncounterEngine(
        int tickRate,
        SpawnBossDelegate spawnBoss,
        DespawnBossDelegate despawnBoss,
        TryResolveDelegate tryResolve,
        TeleportPlayerDelegate teleport,
        NotifyDelegate notify,
        SpawnDroneDelegate spawnDrone,
        DespawnAddDelegate despawnAdd,
        BroadcastPlatingDelegate broadcastPlating)
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
        _countdownTicks = SecondsToTicks(CountdownSeconds);
        _emptyResetTicks = SecondsToTicks(EmptyResetSeconds);
        _victoryEjectTicks = SecondsToTicks(VictoryEjectSeconds);
        _fusionGoodWindowTicks = SecondsToTicks(FusionGoodWindowSeconds);
        _fusionPerfectWindowTicks = SecondsToTicks(FusionPerfectWindowSeconds);
        _soloShatterWindowTicks = SecondsToTicks(SoloShatterWindowSeconds);
        _droneFirstSpawnTicks = SecondsToTicks(DroneFirstSpawnSeconds);
        _droneRespawnTicks = SecondsToTicks(DroneRespawnSeconds);
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
            CrumbleForGood();
            return; // below 70% the plating + drone are done; nothing else to pump.
        }

        if (_platingPermanentlyOff)
        {
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

    // The plating crumbles for good at the 70% boundary: latch it off, drop any open window, stop + tear down the
    // interposer drone (the mechanics are done this run), and broadcast + chat the permanent-off (Laws 4/7).
    private void CrumbleForGood()
    {
        _platingPermanentlyOff = true;
        _windowOpen = false;
        _soloHitTicks.Clear();
        TearDownDrone();
        _broadcastPlating(_bossId, false);
        AnnounceAll("The plating crumbles for good!");
    }

    // Despawn the live interposer drone (leak-free, idempotent) and clear its schedule — used by CrumbleForGood and
    // the full teardown.
    private void TearDownDrone()
    {
        if (_droneAlive)
        {
            _despawnAdd(_droneId);
        }

        _droneAlive = false;
        _droneId = 0;
        _droneSpawnScheduled = false;
    }

    // Full teardown of the P1 mechanics — the drone add + the plating window/hit-count state — on any encounter end
    // (victory / wipe / empty / countdown-cancel). The plating on/off latches themselves are re-initialised fresh in
    // SpawnBossNow, so this only needs to clear what could leak a live entity or stale window across runs.
    private void TearDownEncounterMechanics()
    {
        TearDownDrone();
        _windowOpen = false;
        _soloHitTicks.Clear();
    }

    private void AnnounceAll(string text)
    {
        foreach (var p in _participants)
        {
            _notify(p.EntityId, text);
        }
    }
}
