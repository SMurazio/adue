using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Actions;
using Xunit;

namespace Mmo.Server.Tests;

// MOVEMENT-ACTIONS Phase B1: tests for the server action-intent path. GameServer.HandleActionIntent is private and only
// reachable behind a live LiteNetLib transport, so these exercise its CORE LOGIC at the lowest reachable seams — the
// ClientSession action-cursor dedup + the ServerActionExecutor start/CanStart — composed in the SAME order the handler
// runs them (dedup -> resolve def -> CanStart -> TryStart). This pins: a fresh trigger starts the executor; a
// duplicate/stale ActionSeq is dedup'd (no second start); a distinct trigger while already active is rejected
// (one-at-a-time via CanStart); and the action cursor advances INDEPENDENTLY of the move + attack cursors (the NET6
// "two streams, one cursor" lesson — a third stream gets a third cursor). The gap vs the live wire (the actual
// GameServer dispatch + _serverTick anchor) is noted in the review briefing.
public sealed class ActionIntentHandlerTests
{
    private const int TickRate = 20;
    private const double Radius = CollisionDefaults.BodyRadius; // 0.5

    private static (ServerActionExecutor executor, WorldEntity entity) BuildExecutor(double speed = 5d)
    {
        var grid = new TileGrid(64, 64, System.Array.Empty<TileCoord>());
        var executor = new ServerActionExecutor(
            TickRate,
            () => Radius,
            grid.QueryNearbyWalls,
            (entity, resolved) => entity.ApplyResolvedMove(resolved));

        var ent = new WorldEntity(
            id: 1,
            networkId: 1,
            EntityKind.Player,
            new TileCoord(8, 8),
            Direction8.S,
            "Player1",
            System.Guid.NewGuid(),
            ownerSession: null,
            isDurable: true);
        ent.SetSpeedUnitsPerSecond(speed);
        return (executor, ent);
    }

    // Reproduce HandleActionIntent's core decision sequence for one inbound ActionIntent against a session + executor +
    // entity, returning whether it STARTED the action (true) or was dropped (false) at any gate. ActionSeq dedup first
    // (the cursor advances even on a later reject), then resolve the def, then CanStart, then TryStart — exactly the
    // handler's order.
    private static bool HandleActionIntentCore(
        ClientSession session,
        ServerActionExecutor executor,
        WorldEntity entity,
        MovementActionRegistry registry,
        uint actionSeq,
        byte actionId,
        ushort heading,
        uint serverTick)
    {
        if (!session.TryConsumeActionSequence(actionSeq))
        {
            return false;
        }

        if (!registry.TryGet((ActionId)actionId, out var def))
        {
            return false;
        }

        if (!executor.CanStart(entity, def, serverTick))
        {
            return false;
        }

        return executor.TryStart(entity, def, AimAngle.ToUnitVector(heading), serverTick);
    }

    [Fact]
    public void FreshJumpIntent_StartsTheExecutor()
    {
        var session = new ClientSession(null!);
        var (executor, entity) = BuildExecutor();
        var registry = MovementActionRegistry.Default;

        Assert.False(executor.IsActive(entity));

        var started = HandleActionIntentCore(
            session, executor, entity, registry,
            actionSeq: 1, actionId: (byte)ActionId.Jump, heading: AimAngle.Quantize(0d), serverTick: 100);

        Assert.True(started);
        Assert.True(executor.IsActive(entity));
        Assert.Equal(ActionId.Jump, executor.ActiveAction(entity.Id));
        Assert.Equal(1u, session.LastActionSeq);
    }

    [Fact]
    public void DuplicateOrStaleActionSeq_IsDedupedWithNoSecondStart()
    {
        var session = new ClientSession(null!);
        var (executor, entity) = BuildExecutor();
        var registry = MovementActionRegistry.Default;

        // First trigger (seq 5) starts the action — the cursor advances to 5.
        Assert.True(HandleActionIntentCore(session, executor, entity, registry, 5, (byte)ActionId.Jump, 0, 100));
        Assert.True(executor.IsActive(entity));
        Assert.Equal(5u, session.LastActionSeq);

        // A RE-SENT identical seq (5) is dropped at the cursor (the handler's FIRST gate, before def-resolve / CanStart
        // / TryStart) — it never even reaches the executor, so a reliable-retransmit duplicate can't start twice. The
        // cursor is unchanged.
        Assert.False(HandleActionIntentCore(session, executor, entity, registry, 5, (byte)ActionId.Jump, 0, 101));
        Assert.Equal(5u, session.LastActionSeq);

        // A LOWER (out-of-order, already-superseded) seq (3) is equally stale and dropped at the cursor.
        Assert.False(HandleActionIntentCore(session, executor, entity, registry, 3, (byte)ActionId.Jump, 0, 102));
        Assert.Equal(5u, session.LastActionSeq);

        // The original action is still the only one running (no duplicate started behind the dedup'd retries).
        Assert.True(executor.IsActive(entity));
        Assert.Equal(ActionId.Jump, executor.ActiveAction(entity.Id));
    }

    [Fact]
    public void SecondDistinctTriggerWhileActive_IsRejectedOneAtATime()
    {
        var session = new ClientSession(null!);
        var (executor, entity) = BuildExecutor();
        var registry = MovementActionRegistry.Default;

        Assert.True(HandleActionIntentCore(session, executor, entity, registry, 1, (byte)ActionId.Jump, 0, 100));
        Assert.True(executor.IsActive(entity));

        // A DISTINCT, fresh ActionSeq while an action already owns the entity: the cursor advances (fresh seq) but
        // CanStart rejects it (one-at-a-time, design §2.8) — no second action starts, the original keeps running.
        Assert.False(HandleActionIntentCore(session, executor, entity, registry, 2, (byte)ActionId.Jump, 0, 101));
        Assert.Equal(2u, session.LastActionSeq); // cursor still advanced on the fresh seq
        Assert.True(executor.IsActive(entity));
        Assert.Equal(ActionId.Jump, executor.ActiveAction(entity.Id));
    }

    [Fact]
    public void UnknownActionId_IsDropped()
    {
        var session = new ClientSession(null!);
        var (executor, entity) = BuildExecutor();
        var registry = MovementActionRegistry.Default;

        // Phase D registered Charge/DodgeRoll, so an "unknown id" is now a byte with NO def at all (a corrupt byte or
        // a future action's byte arriving early) — the handler drops it after advancing the cursor (the seq is fresh
        // and consumed; the def lookup fails).
        Assert.False(HandleActionIntentCore(session, executor, entity, registry, 1, actionId: 99, 0, 100));
        Assert.False(executor.IsActive(entity));
        Assert.Equal(1u, session.LastActionSeq);
    }

    // MOVEMENT-ACTIONS Phase D: the two new player defs resolve from the SAME shared registry through the SAME
    // handler sequence with ZERO handler changes — the framework's "adding an action is cheap" payoff, pinned.
    [Fact]
    public void FreshChargeAndDodgeRollIntents_StartTheExecutor()
    {
        var session = new ClientSession(null!);
        var (executor, entity) = BuildExecutor();
        var registry = MovementActionRegistry.Default;

        Assert.True(HandleActionIntentCore(
            session, executor, entity, registry,
            actionSeq: 1, actionId: (byte)ActionId.Charge, heading: AimAngle.Quantize(0d), serverTick: 100));
        Assert.Equal(ActionId.Charge, executor.ActiveAction(entity.Id));

        // Run the charge out (its cooldown arms at the end tick).
        var tick = 100u;
        while (executor.IsActive(entity))
        {
            tick++;
            executor.Step(entity, tick);
        }

        // SERVER cooldowns are PER (entity, action) — the charge's armed cooldown does NOT gate a dodge-roll, so the
        // roll starts IMMEDIATELY after the charge ends. (The CLIENT's mirrored cooldown is a single conservative
        // slot that declines cross-action triggers locally — a documented, safe-side divergence: the client declines
        // and sends nothing, so nothing mispredicts; the server model pinned here is the authoritative one.)
        tick++;
        Assert.True(HandleActionIntentCore(
            session, executor, entity, registry,
            actionSeq: 2, actionId: (byte)ActionId.DodgeRoll, heading: AimAngle.Quantize(0d), serverTick: tick));
        Assert.Equal(ActionId.DodgeRoll, executor.ActiveAction(entity.Id));
    }

    [Fact]
    public void SpammedSecondTrigger_WhileCharging_IsRejectedOneAtATime_EvenAcrossActionIds()
    {
        // One-at-a-time (design §2.8) holds ACROSS distinct action ids: a dodge-roll trigger arriving mid-charge is
        // rejected by CanStart (the charge keeps running), not queued — the cursor still advances on the fresh seq.
        var session = new ClientSession(null!);
        var (executor, entity) = BuildExecutor();
        var registry = MovementActionRegistry.Default;

        Assert.True(HandleActionIntentCore(session, executor, entity, registry, 1, (byte)ActionId.Charge, 0, 100));
        Assert.False(HandleActionIntentCore(session, executor, entity, registry, 2, (byte)ActionId.DodgeRoll, 0, 101));
        Assert.Equal(2u, session.LastActionSeq);
        Assert.Equal(ActionId.Charge, executor.ActiveAction(entity.Id));
    }

    // MOVEMENT-ACTIONS Phase D — I-FRAME AUTHORITY (design §2.7). The wire carries ONLY (actionSeq, actionId, heading,
    // authoredTick) — there is NO field a client could put an i-frame claim in. The window is the SERVER def's data
    // anchored at the SERVER receipt tick (B1/B2 deliberately IGNORE authoredTick), so a FORGED authoredTick cannot
    // move the window, and damage resolution (the ApplyMonsterAttack gate order: dead-guard → HasActiveIFrames →
    // ApplyDamage) negates a hit ONLY inside the def's window. This reproduces that gate order at the same seams the
    // handler-core tests use.
    [Fact]
    public void IFrameAuthority_WindowAnchorsAtServerReceiptTick_ForgedAuthoredTickChangesNothing()
    {
        var session = new ClientSession(null!);
        var (executor, entity) = BuildExecutor();
        var registry = MovementActionRegistry.Default;
        var def = registry.Get(ActionId.DodgeRoll);
        Assert.True(def.HasIFrameWindow); // the roll ships a real window

        // The client FORGES a far-future authoredTick — the handler ignores it (B1/B2 anchor at the receipt tick),
        // exactly as HandleActionIntent binds `_ = authoredTick`. The window therefore spans the SERVER's clock.
        const uint receiptTick = 200;
        Assert.True(HandleActionIntentCore(
            session, executor, entity, registry, 1, (byte)ActionId.DodgeRoll, 0, serverTick: receiptTick));

        // Inside the def's window (elapsed ∈ [IFrameStartTick, IFrameEndTick]) the server reports i-frames…
        for (var k = def.IFrameStartTick; k <= def.IFrameEndTick; k++)
        {
            Assert.True(executor.HasActiveIFrames(entity.Id, receiptTick + k), $"expected i-frames at elapsed {k}");
        }

        // …and OUTSIDE it (the trigger tick before the window opens, and past the window's end) it reports none —
        // including at the forged "authored" anchor a cheating client hoped for.
        Assert.False(executor.HasActiveIFrames(entity.Id, receiptTick));
        Assert.False(executor.HasActiveIFrames(entity.Id, receiptTick + def.IFrameEndTick + 1));
        Assert.False(executor.HasActiveIFrames(entity.Id, 900_000)); // the forged anchor bought nothing
    }

    [Fact]
    public void IFrameAuthority_DamageInsideWindowNegated_OutsideWindowLands_NoActionAlwaysLands()
    {
        var session = new ClientSession(null!);
        var (executor, entity) = BuildExecutor();
        var registry = MovementActionRegistry.Default;
        var def = registry.Get(ActionId.DodgeRoll);
        var initialHp = entity.Stats.Health;
        const int damage = 5;

        // The ApplyMonsterAttack gate order, reproduced at the seam: a hit lands ONLY when HasActiveIFrames is false.
        void ResolveHit(uint serverTick)
        {
            if (!executor.HasActiveIFrames(entity.Id, serverTick))
            {
                entity.ApplyDamage(damage);
            }
        }

        // NO action running: every hit lands (a client "claiming" i-frames has no wire to claim them on).
        ResolveHit(100);
        Assert.Equal(initialHp - damage, entity.Stats.Health);

        // Mid-roll, INSIDE the window: the hit is NEGATED server-side (HP unchanged).
        Assert.True(HandleActionIntentCore(session, executor, entity, registry, 1, (byte)ActionId.DodgeRoll, 0, 200));
        var hpBeforeRoll = entity.Stats.Health;
        ResolveHit(200 + def.IFrameStartTick);
        Assert.Equal(hpBeforeRoll, entity.Stats.Health);

        // Still mid-roll but PAST the window (the vulnerable recovery tick): the hit LANDS.
        var vulnerableTick = 200 + def.IFrameEndTick + 1;
        Assert.True(vulnerableTick <= 200 + def.DurationTicks, "the def must leave a vulnerable in-roll tick");
        ResolveHit(vulnerableTick);
        Assert.Equal(hpBeforeRoll - damage, entity.Stats.Health);
    }

    [Fact]
    public void ActionCursor_AdvancesIndependentlyOfMoveAndAttackCursors()
    {
        var session = new ClientSession(null!);

        // Advance the move + attack cursors first.
        Assert.True(session.TryBeginMoveInput(inputSeq: 50, serverTick: 1));
        Assert.True(session.TryConsumeAttackSequence(sequence: 9));

        // An action seq of 1 is FRESH on the (independent) action cursor even though it is far below the move (50) and
        // attack (9) cursors — the streams share nothing. The NET6 lesson: a third stream gets a third cursor.
        Assert.True(session.TryConsumeActionSequence(1));
        Assert.Equal(1u, session.LastActionSeq);
        Assert.Equal(50u, session.LastInputSeq);
        Assert.Equal(9u, session.LastAttackSeq);

        // And the action cursor never disturbs the others.
        Assert.True(session.TryConsumeActionSequence(2));
        Assert.Equal(2u, session.LastActionSeq);
        Assert.Equal(50u, session.LastInputSeq);
        Assert.Equal(9u, session.LastAttackSeq);
    }
}
