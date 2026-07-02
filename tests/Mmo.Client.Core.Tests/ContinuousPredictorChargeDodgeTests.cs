using System;
using System.Collections.Generic;
using Mmo.Client.Core.Continuous;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Actions;
using Xunit;

namespace Mmo.Client.Core.Tests;

// MOVEMENT-ACTIONS Phase D — the determinism GATE for the two new client-predicted actions, CHARGE and DODGE-ROLL,
// mirroring the Phase-B2 jump gate (ContinuousPredictorActionTests): the REAL ContinuousPredictor (client predict)
// against the REAL ServerActionExecutor (server execute), end to end, using the SHIPPED registry defs — not
// re-implemented models. Model A throughout: the client predicts the dash on its local clock from the trigger and
// LEADS the server by the one-way latency along the SAME path; under no loss the reconcile is SILENT; a server
// rejection is absorbed as a bounded, converging correction by the SAME reconcile (design §2.6).
//
// The dashes add two things the jump gate didn't cover: (1) the GROUNDED invariant — a dash predicts ZERO vertical
// the whole way (the flat H=0 arc); (2) the ENTITY early-stop — the Phase-D server-side player-actor obstacle gather
// means a charge into a stationary body stops at the SAME contact on both sides (the client was already feeding its
// per-frame obstacle set to action frames; the server half is new). The i-frame window is deliberately ABSENT here:
// it is server-authoritative damage state with no client prediction at all (design §2.7) — its tests live in the
// server suites (ChargeDodgeRollActionTests / ActionIntentHandlerTests).
public sealed class ContinuousPredictorChargeDodgeTests
{
    private const int TickRate = 20;
    private const double TickDt = 1.0d / TickRate; // 1 client frame per server tick — clean 1:1 alignment for the gate
    private const double Radius = CollisionDefaults.BodyRadius; // 0.5
    private const double MoveSpeed = 5d; // the ordinary move speed (well below both dash speeds)

    private static MovementActionDef ChargeDef => MovementActionRegistry.Default.Get(ActionId.Charge);
    private static MovementActionDef DodgeRollDef => MovementActionRegistry.Default.Get(ActionId.DodgeRoll);

    // The action's average ground speed + duration (seconds) — the same derivation MmoClient.DeriveActionMotion uses.
    private static (double Speed, double DurationSeconds) Motion(MovementActionDef def)
        => (def.ForwardDistanceUnits * TickRate / def.DurationTicks, def.DurationTicks / (double)TickRate);

    private static WorldEntity NewEntity(TileCoord spawn)
    {
        var ent = new WorldEntity(
            id: 1, networkId: 1, EntityKind.Player, spawn, Direction8.S, "P1", Guid.NewGuid(), ownerSession: null, isDurable: true);
        ent.SetSpeedUnitsPerSecond(MoveSpeed);
        return ent;
    }

    // Run the REAL server executor to completion, returning per-tick positions (index k = position AFTER k Steps;
    // index 0 = the trigger/origin). Padded past the end so a lagged reconcile always indexes a valid sample. The
    // optional `obstacle` wires a single stationary BODY into the executor's player-actor obstacle gather (the
    // Phase-D dash gather: GameServer forwards to the Zone walking gather; one stationary circle is its deterministic
    // essence for a headless test).
    private static List<WorldVector> RunServer(
        TileGrid grid, MovementActionDef def, WorldVector heading, WorldEntity ent, WorldVector? obstacle = null, int padTicks = 8)
    {
        ServerActionExecutor.QueryObstaclesDelegate? queryObstacles = null;
        if (obstacle is { } body)
        {
            queryObstacles = (_, _, _, radius, scratch) =>
            {
                scratch.Clear();
                scratch.Add(new ContinuousCollision.Circle(body.X, body.Y, radius));
            };
        }

        var exec = new ServerActionExecutor(
            TickRate, () => Radius, grid.QueryNearbyWalls, (e, p) => e.ApplyResolvedMove(p), queryObstacles);

        Assert.True(exec.TryStart(ent, def, heading, serverTick: 0));
        var positions = new List<WorldVector> { ent.Position };
        for (uint t = 1; t <= def.DurationTicks + (uint)padTicks; t++)
        {
            exec.Step(ent, t);
            positions.Add(ent.Position);
        }

        return positions;
    }

    [Fact]
    public void Charge_NoLoss_PredictMatchesServerExecutor_ZeroCorrection_LeadPreserved()
    {
        // THE GATE, dash edition: open ground, no loss, 1 frame per tick, snapshots lagging D ticks. The reconcile
        // must stay SILENT (no-loss == no-correction extended to the charge) and the predicted must LEAD the server
        // base by exactly D ticks of dash travel while both sides are mid-dash — the Model A temporal lead.
        var def = ChargeDef;
        var (speed, durationSeconds) = Motion(def);
        var heading = Direction8.E.ToUnitVector();

        var grid = new TileGrid(64, 64, Array.Empty<TileCoord>());
        var serverEnt = NewEntity(new TileCoord(8, 8));
        var serverPos = RunServer(grid, def, heading, serverEnt);
        var origin = serverPos[0];

        var predictor = new ContinuousPredictor(MoveSpeed, origin.X, origin.Y, blocked: null, radius: Radius);
        Assert.True(predictor.BeginAction(heading.X, heading.Y, speed, durationSeconds, def.JumpHeight, def.AirborneTicks, TickRate));

        const int d = 3; // one-way latency in ticks
        var perTick = def.ForwardDistanceUnits / def.DurationTicks;
        var frames = (int)def.DurationTicks + 6;
        for (var i = 1; i <= frames; i++)
        {
            predictor.PredictAndBuffer(0d, 0d, TickDt); // held input zero — the dash overrides it
            var ackTick = i - d;
            if (ackTick < 0)
            {
                continue; // the trigger is still in flight to the server
            }

            var sp = serverPos[Math.Min(ackTick, serverPos.Count - 1)];
            predictor.Reconcile(sp, (uint)ackTick);
            Assert.True(predictor.LastCorrectionUnits < 1e-6, $"correction at frame {i}: {predictor.LastCorrectionUnits}");

            if (ackTick >= 1 && i <= (int)def.DurationTicks)
            {
                Assert.Equal(d * perTick, predictor.ServerVsPredictedUnits, 6);
            }
        }

        // Both sides land at the identical full-reach spot; the lead was purely temporal.
        Assert.Equal(origin.X + def.ForwardDistanceUnits, predictor.PredictedX, 6);
        Assert.Equal(origin.Y, predictor.PredictedY, 6);
    }

    [Fact]
    public void Charge_UnderSnapshotLoss_SparseReconciles_StaySilent()
    {
        // LOSS: drop most snapshots (a reconcile only every 4th tick, still D ticks behind). The unacked window is
        // longer, but the replay reproduces the identical dash from the older base — the reconcile stays silent, the
        // charge's early ticks self-heal exactly like ordinary movement under loss.
        var def = ChargeDef;
        var (speed, durationSeconds) = Motion(def);
        var heading = Direction8.E.ToUnitVector();

        var grid = new TileGrid(64, 64, Array.Empty<TileCoord>());
        var serverEnt = NewEntity(new TileCoord(8, 8));
        var serverPos = RunServer(grid, def, heading, serverEnt);
        var origin = serverPos[0];

        var predictor = new ContinuousPredictor(MoveSpeed, origin.X, origin.Y, blocked: null, radius: Radius);
        Assert.True(predictor.BeginAction(heading.X, heading.Y, speed, durationSeconds, def.JumpHeight, def.AirborneTicks, TickRate));

        const int d = 3;
        var frames = (int)def.DurationTicks + 8;
        var reconciles = 0;
        for (var i = 1; i <= frames; i++)
        {
            predictor.PredictAndBuffer(0d, 0d, TickDt);
            var ackTick = i - d;
            if (ackTick < 0 || i % 4 != 0)
            {
                continue; // this tick's snapshot was DROPPED (or the trigger is still in flight)
            }

            reconciles++;
            var sp = serverPos[Math.Min(ackTick, serverPos.Count - 1)];
            predictor.Reconcile(sp, (uint)ackTick);
            Assert.True(predictor.LastCorrectionUnits < 1e-6, $"correction at sparse frame {i}: {predictor.LastCorrectionUnits}");
        }

        Assert.True(reconciles >= 2, "the sparse schedule must still reconcile at least twice to prove anything");
        Assert.Equal(origin.X + def.ForwardDistanceUnits, predictor.PredictedX, 6);
    }

    [Fact]
    public void Charge_IntoWall_PerFrameVsPerTick_SubTileResidual_StopsAtSameFace()
    {
        // The Option-2 tradeoff measured for the dash (mirrors the jump wall test): client predicts at 60fps, server
        // steps at 20Hz — a charge INTO A WALL resolves at different granularity, so the raw geometric divergence is
        // a bounded SUB-TILE residual, and the client stops at the SAME wall face (x = 10.5 − 0.5 = 10.0), never
        // entering the blocked tile. The reconcile would smooth the residual; here we measure it raw.
        var def = ChargeDef;
        var (speed, durationSeconds) = Motion(def);
        var heading = Direction8.E.ToUnitVector();

        var blockedArr = new[] { new TileCoord(11, 8) }; // two tiles east of the (8,8) spawn — inside the 4u dash
        var grid = new TileGrid(64, 64, blockedArr);
        var blocked = new HashSet<TileCoord>(blockedArr);
        var serverEnt = NewEntity(new TileCoord(8, 8));
        var serverPos = RunServer(grid, def, heading, serverEnt);
        var origin = serverPos[0];
        var serverLanding = serverPos[^1];

        var predictor = new ContinuousPredictor(MoveSpeed, origin.X, origin.Y, blocked: blocked, radius: Radius);
        Assert.True(predictor.BeginAction(heading.X, heading.Y, speed, durationSeconds, def.JumpHeight, def.AirborneTicks, TickRate));
        const double frameDt = 1d / 60d;
        var guard = 0;
        while (predictor.IsActionActive && guard++ < 10_000)
        {
            predictor.PredictAndBuffer(0d, 0d, frameDt);
        }

        var residual = Math.Abs(predictor.PredictedX - serverLanding.X);
        Assert.True(residual < 1.0d, $"per-frame vs per-tick wall residual too large: {residual}");
        Assert.True(predictor.PredictedX < origin.X + def.ForwardDistanceUnits - 1e-6, "client did not stop short of the wall");
        Assert.True(predictor.PredictedX <= 10.0d + 1e-6, $"client entered/passed the wall face: x={predictor.PredictedX}");
    }

    [Fact]
    public void Charge_IntoStationaryBody_ClientAndServerStopAtTheSameContact_SilentReconcile()
    {
        // THE ENTITY EARLY-STOP (the Phase-D server-side player-dash obstacle gather): a stationary body sits 2 tiles
        // into the dash. The server executor resolves the dash against it per tick; the client predicts against the
        // SAME circle per frame (the predictor already takes the obstacle set on action frames). Both must stop AT
        // contact (centre distance 2×radius, x = 9.0), never pass through, and — at 1:1 frame/tick alignment — the
        // per-tick reconcile stays silent, so a charge into a monster does not rubber-band.
        var def = ChargeDef;
        var (speed, durationSeconds) = Motion(def);
        var heading = Direction8.E.ToUnitVector();
        var body = new WorldVector(10d, 8d);
        var obstacles = new List<ContinuousCollision.Circle> { new(body.X, body.Y, Radius) };

        var grid = new TileGrid(64, 64, Array.Empty<TileCoord>());
        var serverEnt = NewEntity(new TileCoord(8, 8));
        var serverPos = RunServer(grid, def, heading, serverEnt, obstacle: body);
        var origin = serverPos[0];
        var serverLanding = serverPos[^1];

        // The server stopped AT contact and never passed through.
        Assert.Equal(body.X - (2d * Radius), serverLanding.X, 1e-6);

        var predictor = new ContinuousPredictor(MoveSpeed, origin.X, origin.Y, blocked: null, radius: Radius);
        Assert.True(predictor.BeginAction(heading.X, heading.Y, speed, durationSeconds, def.JumpHeight, def.AirborneTicks, TickRate));

        const int d = 3;
        var frames = (int)def.DurationTicks + 6;
        for (var i = 1; i <= frames; i++)
        {
            predictor.PredictAndBuffer(0d, 0d, TickDt, obstacles);
            var ackTick = i - d;
            if (ackTick < 0)
            {
                continue;
            }

            var sp = serverPos[Math.Min(ackTick, serverPos.Count - 1)];
            predictor.Reconcile(sp, (uint)ackTick, obstacles);
            Assert.True(predictor.LastCorrectionUnits < 1e-6, $"correction at frame {i}: {predictor.LastCorrectionUnits}");
        }

        // The client converged onto the same contact stop as the server.
        Assert.Equal(serverLanding.X, predictor.PredictedX, 6);
        Assert.Equal(origin.Y, predictor.PredictedY, 6);
    }

    [Fact]
    public void DodgeRoll_NoLoss_ZeroCorrection_AndPredictsZeroVerticalThroughout()
    {
        // The roll rides the identical Model A path AND must stay GROUNDED: a dash def has H=0, so the predicted
        // vertical is exactly 0 on every frame (no phantom arc on the local avatar while rolling).
        var def = DodgeRollDef;
        var (speed, durationSeconds) = Motion(def);
        var heading = Direction8.N.ToUnitVector();

        var grid = new TileGrid(64, 64, Array.Empty<TileCoord>());
        var serverEnt = NewEntity(new TileCoord(8, 8));
        var serverPos = RunServer(grid, def, heading, serverEnt);
        var origin = serverPos[0];

        var predictor = new ContinuousPredictor(MoveSpeed, origin.X, origin.Y, blocked: null, radius: Radius);
        Assert.True(predictor.BeginAction(heading.X, heading.Y, speed, durationSeconds, def.JumpHeight, def.AirborneTicks, TickRate));
        Assert.Equal(0d, predictor.PredictedVerticalOffset, 9); // grounded at trigger

        const int d = 2;
        var frames = (int)def.DurationTicks + 6;
        for (var i = 1; i <= frames; i++)
        {
            predictor.PredictAndBuffer(0d, 0d, TickDt);
            Assert.Equal(0d, predictor.PredictedVerticalOffset, 9); // grounded EVERY frame — a roll never arcs

            var ackTick = i - d;
            if (ackTick < 0)
            {
                continue;
            }

            var sp = serverPos[Math.Min(ackTick, serverPos.Count - 1)];
            predictor.Reconcile(sp, (uint)ackTick);
            Assert.True(predictor.LastCorrectionUnits < 1e-6, $"correction at frame {i}: {predictor.LastCorrectionUnits}");
        }

        // Converged onto the server's roll end (origin + the short roll distance along the heading).
        Assert.Equal(serverPos[^1].X, predictor.PredictedX, 6);
        Assert.Equal(serverPos[^1].Y, predictor.PredictedY, 6);
    }

    [Fact]
    public void SpammedSecondTrigger_DeclinedLocally_AcrossActionIds_NothingToMispredict()
    {
        // One-at-a-time (design §2.8) + the mirrored cooldown, dash edition. While a charge is predicted, a dodge-roll
        // trigger is DECLINED locally (BeginAction false ⇒ MmoClient sends nothing) — mirroring the server's can-act,
        // so the spam case produces NO divergence at all. After the charge ends, the predictor's mirrored cooldown is
        // a SINGLE CONSERVATIVE slot: it declines ANY action (even a dodge-roll the per-action SERVER clock would
        // accept) until it elapses. That cross-action lockout is DELIBERATE and safe-side — a local decline sends
        // nothing and mispredicts nothing (the server-authoritative per-action model is pinned in
        // ChargeDodgeRollActionTests.Charge_Cooldown_...); pinned here so a future per-action mirror is a conscious
        // change, not a drive-by.
        var charge = ChargeDef;
        var roll = DodgeRollDef;
        var (chargeSpeed, chargeDuration) = Motion(charge);
        var (rollSpeed, rollDuration) = Motion(roll);
        var chargeCooldown = charge.CooldownTicks / (double)TickRate;
        var heading = Direction8.E.ToUnitVector();
        var predictor = new ContinuousPredictor(MoveSpeed, 0d, 0d, blocked: null, radius: Radius);

        Assert.True(predictor.BeginAction(heading.X, heading.Y, chargeSpeed, chargeDuration, charge.JumpHeight, charge.AirborneTicks, TickRate, chargeCooldown));

        // Mid-charge: a dodge-roll spam is declined (one-at-a-time across DIFFERENT action ids).
        Assert.False(predictor.BeginAction(heading.X, heading.Y, rollSpeed, rollDuration, roll.JumpHeight, roll.AirborneTicks, TickRate));

        // Run the charge out locally.
        for (var i = 0; i < (int)charge.DurationTicks; i++)
        {
            predictor.PredictAndBuffer(0d, 0d, TickDt);
        }

        Assert.False(predictor.IsActionActive);

        // Inside the mirrored cooldown: the conservative single slot declines even the OTHER action locally.
        Assert.False(predictor.BeginAction(heading.X, heading.Y, rollSpeed, rollDuration, roll.JumpHeight, roll.AirborneTicks, TickRate));

        // Once the mirrored cooldown elapses, a fresh trigger is accepted again.
        var cooldownFrames = (int)Math.Ceiling(chargeCooldown / TickDt) + 1;
        for (var i = 0; i < cooldownFrames; i++)
        {
            predictor.PredictAndBuffer(0d, 0d, TickDt);
        }

        Assert.True(predictor.BeginAction(heading.X, heading.Y, rollSpeed, rollDuration, roll.JumpHeight, roll.AirborneTicks, TickRate));
    }

    [Fact]
    public void Charge_RejectedByServer_ConvergesToServer_Bounded_NoOscillation()
    {
        // THE REJECTED PATH for the dash (mirrors the jump rejected test): the server denies the charge (cooldown
        // mis-estimate / rooted / dead) and integrates the heading MoveIntents at the ordinary MOVE speed instead.
        // The client predicted the (much faster) dash, so it leads; the SAME reconcile pulls it back — bounded by the
        // speed gap × duration (2.5u for the shipped def, under the 4u snap threshold ⇒ smoothed, not snapped) — and
        // converges with no oscillation. No special rollback path (design §2.6).
        var def = ChargeDef;
        var (speed, durationSeconds) = Motion(def);
        var heading = Direction8.E.ToUnitVector();
        var origin = new WorldVector(8d, 8d);

        var predictor = new ContinuousPredictor(MoveSpeed, origin.X, origin.Y, blocked: null, radius: Radius);
        Assert.True(predictor.BeginAction(heading.X, heading.Y, speed, durationSeconds, def.JumpHeight, def.AirborneTicks, TickRate));

        var serverBySeq = new List<WorldVector> { origin };
        var sx = origin.X;
        const int d = 3;
        var frames = (int)def.DurationTicks + 10;
        var corrections = new List<double>();
        for (var i = 1; i <= frames; i++)
        {
            predictor.PredictAndBuffer(0d, 0d, TickDt);

            // The rejecting server: ordinary MOVE-speed motion along the heading while the dash frames flow, then stop.
            sx += i <= (int)def.DurationTicks ? MoveSpeed * TickDt : 0d;
            serverBySeq.Add(new WorldVector(sx, origin.Y));

            var ackSeq = i - d;
            if (ackSeq < 0)
            {
                continue;
            }

            predictor.Reconcile(serverBySeq[ackSeq], (uint)ackSeq);
            corrections.Add(predictor.LastCorrectionUnits);
        }

        var gap = (speed - MoveSpeed) * durationSeconds;
        Assert.All(corrections, c => Assert.True(c <= gap + 1e-6, $"correction {c} exceeded the bounded gap {gap}"));
        Assert.Equal(serverBySeq[^1].X, predictor.PredictedX, 4); // converged onto the server's (rejected) position
        Assert.Equal(origin.Y, predictor.PredictedY, 6);

        // Re-applying the final authoritative state moves nothing (no oscillation / runaway).
        var settled = predictor.PredictedX;
        predictor.Reconcile(serverBySeq[^1], (uint)(frames - d));
        Assert.Equal(settled, predictor.PredictedX, 6);
    }
}
