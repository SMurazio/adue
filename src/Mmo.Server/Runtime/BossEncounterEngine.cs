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

    // HP scaled at spawn by participant count (design "Boss stats"): 1200 duo / 700 solo.
    public const int DuoBossHealth = 1200;
    public const int SoloBossHealth = 700;

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

    private readonly uint _countdownTicks;
    private readonly uint _emptyResetTicks;
    private readonly uint _victoryEjectTicks;

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

    public BossEncounterEngine(
        int tickRate,
        SpawnBossDelegate spawnBoss,
        DespawnBossDelegate despawnBoss,
        TryResolveDelegate tryResolve,
        TeleportPlayerDelegate teleport,
        NotifyDelegate notify)
    {
        _tickRate = tickRate;
        _spawnBoss = spawnBoss ?? throw new ArgumentNullException(nameof(spawnBoss));
        _despawnBoss = despawnBoss ?? throw new ArgumentNullException(nameof(despawnBoss));
        _tryResolve = tryResolve ?? throw new ArgumentNullException(nameof(tryResolve));
        _teleport = teleport ?? throw new ArgumentNullException(nameof(teleport));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        _countdownTicks = SecondsToTicks(CountdownSeconds);
        _emptyResetTicks = SecondsToTicks(EmptyResetSeconds);
        _victoryEjectTicks = SecondsToTicks(VictoryEjectSeconds);
    }

    // Test/diagnostic visibility (like BasicRoamerBehavior's TryGetPhase) — never drives replication.
    public EncounterState State => _state;
    public int ParticipantCount => _participants.Count;
    public ulong BossId => _bossId;
    public bool BossSpawned => _bossSpawned;

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
            SpawnBossNow();
        }
    }

    private void SpawnBossNow()
    {
        var health = _participants.Count >= 2 ? DuoBossHealth : SoloBossHealth;
        var boss = _spawnBoss(BossArena.BossSpawnTile, health);
        _bossId = boss.Id;
        _bossSpawned = true;
        _state = EncounterState.Active;
        _emptyTimerArmed = false;
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
        }
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

    private void AnnounceAll(string text)
    {
        foreach (var p in _participants)
        {
            _notify(p.EntityId, text);
        }
    }
}
