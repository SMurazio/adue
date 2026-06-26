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

        // Charge (id 2) is reserved but has NO registered def in Phase B1 — the handler drops it after advancing the
        // cursor (the seq is fresh and consumed; the def lookup fails).
        Assert.False(HandleActionIntentCore(session, executor, entity, registry, 1, (byte)ActionId.Charge, 0, 100));
        Assert.False(executor.IsActive(entity));
        Assert.Equal(1u, session.LastActionSeq);
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
