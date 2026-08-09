using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// ADUE P1 (todo/S-adue-p1-run-loop-chassis.md, docs/duo-standalone-plan.md P1): the ROGUELITE RUN CHASSIS — the
// state machine that replaces "persistent world" as the moment-to-moment loop:
//
//     LOBBY (ready up in town) -> ACTIVE (the run) -> SUMMARY (the end screen) -> LOBBY -> ...
//
// all in one process, with no server restart between runs.
//
// SHAPE, and why it is this small. P1 is a CHASSIS, not content: the run's only room is the Sunderer arena, which
// already exists as BossEncounterEngine + the sealed BossArena. So this engine deliberately owns NOTHING about
// combat, the arena, or the boss — it owns WHO is in a run, WHEN one starts and ends, WHAT the ending was, and the
// clean reset. Everything inside the room stays the boss encounter's. Written in the BossEncounterEngine/
// TelegraphScheduler mould: own file, a delegate for every world touch, so it is headlessly unit-testable against
// a bare WorldState + lambdas and GameServer stays the single wiring point.
//
// THE FRONT DOOR is ready-up (RunReadyMessage / the /ready chat verb), not `/boss`. `/boss` survives as a DEV
// SHORTCUT straight into the arena with no run wrapper — that path simply never reaches this engine (its outcome
// callback is ignored while the phase is Lobby), so a dev poke can't corrupt run state.
//
// READY GATE. A player's roster is themself plus their `/pair` partner when paired AND online:
//   * unpaired  -> readying up SOLO-STARTS immediately (the task's "or solo-start");
//   * paired    -> the run starts on the tick the SECOND partner readies.
// Un-readying (Ready=false) is supported so a mis-press isn't a forced run.
//
// DEATH RULES (the task's item 3). Inside a run there is NO town respawn: GameServer's RespawnPlayers skips any
// entity IsRunParticipant reports, so a dead player stays a body in the arena instead of being teleported to town
// after the respawn delay. That is what lets the boss encounter's existing "all participants dead in the arena"
// check mean WIPE (before P1 it raced the respawn pass). One dead partner therefore keeps the current boss rules
// running until the survivor clears or wipes. On EITHER ending every roster member is revived + returned to town by
// the `returnPlayer` seam — the single place a run's death debt is settled.
//
// P2 SEAM (surfaced as a fork in the review briefing, NOT decided here): the plan's "arena/floor -> waves -> boss"
// descent has no representation yet. P1 treats ACTIVE as one room because that is what the acceptance criteria pin
// (ready->run->wipe->reset and ready->run->clear->reset). When floors arrive they become a SUB-STAGE of Active —
// this engine gains a stage cursor and the `beginBossRoom` seam becomes `beginStage`; no phase changes.
//
// PARKED SYSTEMS (the prune-on-friction rule): persistence, ecology, and the region spawners are NOT frozen or
// bypassed inside a run. They were checked and do not interfere — the run happens in the sealed BossArena pocket,
// which is authored DungeonStone (no node scatter) and belongs to no ecology region or spawner, and the boss/adds
// are spawned via SpawnMonsterCore rather than any spawner. Nothing was deleted or disabled.
public sealed class RunEngine
{
    // Resolve an entity id to its live WorldEntity, or null if it is gone (disconnected / despawned). Used for the
    // roster liveness prune and to hand real entities to `returnPlayer`.
    public delegate WorldEntity? TryResolveDelegate(ulong entityId);

    // Open the run's boss room for `issuer` (+ `partner` when the pair is running together) — GameServer wires
    // BossEncounterEngine.TryBegin, i.e. EXACTLY the seam `/boss` uses, so the run front door and the dev shortcut
    // enter through one code path. Returns false + a refusal line when the arena is unavailable (a dev `/boss` run
    // is live, or victors from the last fight have not cleared out); the run then never starts and the pair stays
    // in the lobby.
    public delegate bool BeginBossRoomDelegate(WorldEntity issuer, WorldEntity? partner, uint serverTick, out string message);

    // Settle one roster member at the end of a run: revive them if they died, refill HP, and teleport them out of the
    // arena back to the town lobby (GameServer wires the same spawn-anchor + full-heal + clear-intent sequence the
    // ordinary respawn pass uses). Called for EVERY roster member on clear AND on wipe — a run leaves nobody behind.
    public delegate void ReturnPlayerDelegate(WorldEntity player);

    // Send a system/chat line to one player (a no-op if they have no live session).
    public delegate void NotifyDelegate(ulong entityId, string text);

    // Push the end-of-run summary to one player (GameServer maps it to RunSummaryMessage).
    public delegate void SendSummaryDelegate(ulong entityId, RunSummary summary);

    // Announce that the run status changed (phase and/or ready set), so GameServer can re-push RunStatusMessage.
    // Edge-driven by design: this fires on real transitions only, never per tick.
    public delegate void StatusChangedDelegate();

    // The end screen's payload — every field a counter the server already keeps (the task's "whatever the server
    // already tracks cheaply"). DamageDealt/BossHealthPercent come straight off the boss encounter's end edge.
    public readonly record struct RunSummary(
        RunOutcome Outcome, uint DurationSeconds, uint DamageDealt, byte BossHealthPercent, byte Deaths);

    // What one recipient's RunStatusMessage should say. Computed per recipient because SelfReady (and, in the lobby,
    // the roster the ready gate is waiting on) differ per player.
    public readonly record struct RunStatusView(RunPhase Phase, byte RosterCount, byte ReadyCount, bool SelfReady);

    // How long the end screen stays up before the phase drops back to Lobby on its own. A safety net, not the
    // intended exit: readying up during the summary dismisses it immediately (see TryReady), which is how a pair
    // chains runs. Long enough to actually read the numbers, short enough that an idle pair isn't stuck.
    private const double SummarySeconds = 30d;

    private readonly int _tickRate;
    private readonly TryResolveDelegate _tryResolve;
    private readonly BeginBossRoomDelegate _beginBossRoom;
    private readonly ReturnPlayerDelegate _returnPlayer;
    private readonly NotifyDelegate _notify;
    private readonly SendSummaryDelegate _sendSummary;
    private readonly StatusChangedDelegate _statusChanged;
    private readonly uint _summaryTicks;

    // Players who have readied up but whose run has not started (Lobby only — cleared on start and on every reset).
    private readonly HashSet<ulong> _ready = [];

    // The live run's roster: the entity ids that entered together. Never more than 2 in P1 (a pair), so a list is
    // cheaper than a set and keeps the notification order stable (issuer first).
    private readonly List<ulong> _roster = [];

    private RunPhase _phase = RunPhase.Lobby;
    private uint _startTick;
    private uint _summaryEndTick;
    private int _deaths;
    private RunSummary? _lastSummary;

    // The boss room's end edge, captured by OnBossRoomEnded and consumed on the NEXT Step. Deferred rather than acted
    // on inline because the callback fires from INSIDE BossEncounterEngine.Step — ending the run there would re-enter
    // world mutation (teleports, revives) in the middle of the encounter's own teardown.
    private BossEncounterEngine.EncounterResult? _pendingResult;

    public RunEngine(
        int tickRate,
        TryResolveDelegate tryResolve,
        BeginBossRoomDelegate beginBossRoom,
        ReturnPlayerDelegate returnPlayer,
        NotifyDelegate notify,
        SendSummaryDelegate sendSummary,
        StatusChangedDelegate statusChanged)
    {
        _tickRate = tickRate > 0 ? tickRate : throw new ArgumentOutOfRangeException(nameof(tickRate));
        _tryResolve = tryResolve ?? throw new ArgumentNullException(nameof(tryResolve));
        _beginBossRoom = beginBossRoom ?? throw new ArgumentNullException(nameof(beginBossRoom));
        _returnPlayer = returnPlayer ?? throw new ArgumentNullException(nameof(returnPlayer));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        _sendSummary = sendSummary ?? throw new ArgumentNullException(nameof(sendSummary));
        _statusChanged = statusChanged ?? throw new ArgumentNullException(nameof(statusChanged));
        _summaryTicks = (uint)Math.Max(1, (int)Math.Ceiling(SummarySeconds * tickRate));
    }

    // Test/diagnostic visibility (the BossEncounterEngine convention) — never drives replication.
    public RunPhase Phase => _phase;
    public int RosterCount => _roster.Count;
    public int ReadyCount => _ready.Count;
    public RunSummary? LastSummary => _lastSummary;
    public bool IsReady(ulong entityId) => _ready.Contains(entityId);

    // THE death-rule hook (task item 3): true while `entityId` is inside a live run, which is exactly when the town
    // respawn must NOT fire for it. GameServer's RespawnPlayers consults this and skips the entity — the body stays
    // down in the arena until the run resolves, and `returnPlayer` settles it then.
    public bool IsRunParticipant(ulong entityId) => _phase == RunPhase.Active && _roster.Contains(entityId);

    // Ready-up / un-ready. `partner` is the caller's `/pair` partner when paired AND online (GameServer resolves it),
    // else null. Returns false only when the request cannot be honoured at all (a run is already under way); the
    // out `message` is the chat line for the caller either way.
    public bool TryReady(WorldEntity self, WorldEntity? partner, bool ready, uint serverTick, out string message)
    {
        if (_phase == RunPhase.Active)
        {
            message = _roster.Contains(self.Id)
                ? "You are already in a run."
                : "A run is already under way — wait for it to end.";
            return false;
        }

        // Pressing ready on the END SCREEN dismisses it and drops straight back to the lobby, so a pair can chain
        // runs with one key. Done BEFORE the ready is recorded (EnterLobby clears the ready set).
        if (_phase == RunPhase.Summary)
        {
            EnterLobby();
        }

        var hasPartner = partner is not null && partner.Id != self.Id;

        if (!ready)
        {
            _ready.Remove(self.Id);
            message = "You are no longer ready.";
            _statusChanged();
            return true;
        }

        _ready.Add(self.Id);

        if (!hasPartner)
        {
            // SOLO START: an unpaired player is their own full roster, so their ready is the whole gate.
            message = "Ready. Starting a solo run...";
            StartRun(self, null, serverTick);
            _statusChanged();
            return true;
        }

        if (!_ready.Contains(partner!.Id))
        {
            message = "Ready. Waiting for your partner.";
            _notify(partner!.Id, $"{self.DisplayName} is ready. Ready up to begin the run.");
            _statusChanged();
            return true;
        }

        message = "Both ready. The run begins.";
        StartRun(self, partner, serverTick);
        _statusChanged();
        return true;
    }

    // The per-tick pump. GameServer calls it once per tick, right AFTER BossEncounterEngine.Step, so a boss-room end
    // edge reported this tick is acted on this same tick (before the snapshot goes out).
    public void Step(uint serverTick)
    {
        switch (_phase)
        {
            case RunPhase.Active:
                StepActive(serverTick);
                break;

            case RunPhase.Summary:
                if (serverTick >= _summaryEndTick)
                {
                    EnterLobby();
                    _statusChanged();
                }

                break;
        }
    }

    // BossEncounterEngine's end edge. Recorded, not acted on (see _pendingResult). Ignored unless a run is live —
    // which is precisely what makes a bare `/boss` dev run invisible to the chassis.
    public void OnBossRoomEnded(BossEncounterEngine.EncounterResult result, uint serverTick)
    {
        if (_phase != RunPhase.Active)
        {
            return;
        }

        _pendingResult = result;
    }

    // A player died. Counted only for live-run roster members (the summary's Deaths stat). GameServer calls it from
    // the player-death edge inside the damage choke point's landed tail.
    public void OnPlayerDied(ulong entityId)
    {
        if (_phase == RunPhase.Active && _roster.Contains(entityId))
        {
            _deaths++;
        }
    }

    // A player left the world (disconnect / despawn). Drops any lobby ready flag; a live-run roster entry is dropped
    // by the next Step's liveness prune (which is also where an emptied roster becomes an abandoned run).
    public void ForgetPlayer(ulong entityId)
    {
        if (_ready.Remove(entityId))
        {
            _statusChanged();
        }
    }

    // What `selfId`'s RunStatusMessage should say. `partnerId` is their paired partner when paired AND online.
    public RunStatusView StatusFor(ulong selfId, ulong? partnerId)
    {
        if (_phase == RunPhase.Active)
        {
            // Inside a live run "ready" is moot: the whole roster is committed. SelfReady doubles as "you are in it",
            // which is what the HUD needs to distinguish a runner from a bystander.
            var count = (byte)Math.Min(_roster.Count, byte.MaxValue);
            return new RunStatusView(RunPhase.Active, count, count, _roster.Contains(selfId));
        }

        var hasPartner = partnerId.HasValue && partnerId.Value != selfId;
        var roster = hasPartner ? 2 : 1;
        var ready = 0;
        if (_ready.Contains(selfId))
        {
            ready++;
        }

        if (hasPartner && _ready.Contains(partnerId!.Value))
        {
            ready++;
        }

        return new RunStatusView(_phase, (byte)roster, (byte)ready, _ready.Contains(selfId));
    }

    private void StepActive(uint serverTick)
    {
        // Liveness prune: a DISCONNECTED member leaves the roster. A DEAD member does NOT — being a body in the arena
        // is the whole point of the no-mid-run-respawn rule, and the boss room reads those bodies as the wipe.
        for (var i = _roster.Count - 1; i >= 0; i--)
        {
            if (_tryResolve(_roster[i]) is null)
            {
                _roster.RemoveAt(i);
            }
        }

        if (_pendingResult is { } result)
        {
            _pendingResult = null;
            var outcome = result.Outcome switch
            {
                BossEncounterEngine.EncounterOutcome.Victory => RunOutcome.Clear,
                BossEncounterEngine.EncounterOutcome.Wipe => RunOutcome.Wipe,
                _ => RunOutcome.Abandoned,
            };

            EndRun(outcome, result, serverTick);
            return;
        }

        if (_roster.Count == 0)
        {
            // Everyone disconnected mid-run. There is nobody to show an end screen to, so the run is abandoned; the
            // boss room notices its own empty arena and tears the boss down on its grace timer.
            EndRun(RunOutcome.Abandoned, default, serverTick);
        }
    }

    private void StartRun(WorldEntity issuer, WorldEntity? partner, uint serverTick)
    {
        _roster.Clear();
        _roster.Add(issuer.Id);
        if (partner is not null && partner.Id != issuer.Id)
        {
            _roster.Add(partner.Id);
        }

        if (!_beginBossRoom(issuer, partner, serverTick, out var refusal))
        {
            // The room refused (a dev /boss run is live, or last fight's victors are still inside). Stay in the lobby
            // and tell everyone who had readied why, rather than silently swallowing their press.
            _roster.Clear();
            foreach (var id in _ready)
            {
                _notify(id, $"The run cannot start yet: {refusal}");
            }

            _ready.Clear();
            return;
        }

        _phase = RunPhase.Active;
        _startTick = serverTick;
        _deaths = 0;
        _pendingResult = null;
        _lastSummary = null;
        _ready.Clear();
    }

    private void EndRun(RunOutcome outcome, BossEncounterEngine.EncounterResult result, uint serverTick)
    {
        // Settle EVERY roster member first — revive the dead, refill, and teleport out of the arena — so nobody is
        // ever left as a body in a room that no longer has a run in it. Done before the phase flips so a resolve
        // failure (a member who vanished this very tick) can't skip anyone still present.
        foreach (var id in _roster)
        {
            if (_tryResolve(id) is { } player)
            {
                _returnPlayer(player);
            }
        }

        if (outcome == RunOutcome.Abandoned)
        {
            // Nobody to show an end screen to (the roster emptied, or the room reported an abandon). Clean reset.
            EnterLobby();
            _statusChanged();
            return;
        }

        var elapsedTicks = serverTick >= _startTick ? serverTick - _startTick : 0u;
        var summary = new RunSummary(
            outcome,
            elapsedTicks / (uint)_tickRate,
            (uint)Math.Clamp(result.BossDamageTaken, 0L, uint.MaxValue),
            (byte)Math.Clamp(result.BossHealthPercentRemaining, 0, 100),
            (byte)Math.Clamp(_deaths, 0, byte.MaxValue));

        foreach (var id in _roster)
        {
            _notify(id, outcome == RunOutcome.Clear
                ? "RUN CLEARED — the Sunderer falls."
                : "RUN OVER — the Sunderer still stands.");
            _sendSummary(id, summary);
        }

        _phase = RunPhase.Summary;
        _summaryEndTick = serverTick + _summaryTicks;
        _lastSummary = summary;
        _roster.Clear();
        _ready.Clear();
        _statusChanged();
    }

    // The clean reset the task's "reset cleanly into a new run without server restart" asks for: every piece of
    // per-run state returns to its construction value, so run N+1 starts from the same blank slate run 1 did.
    private void EnterLobby()
    {
        _phase = RunPhase.Lobby;
        _roster.Clear();
        _ready.Clear();
        _pendingResult = null;
        _lastSummary = null;
        _deaths = 0;
        _startTick = 0;
        _summaryEndTick = 0;
    }
}
