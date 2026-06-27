using System;
using System.Collections.Generic;
using Mmo.Client.Core.Continuous;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Actions;
using Xunit;

namespace Mmo.Client.Core.Tests;

// MOVEMENT-ACTIONS Phase B2 — the determinism GATE for the client-predicted movement action (the netcode crux; the 3
// historical misses all trusted a model instead of testing the LIVE symptom). These drive the REAL ContinuousPredictor
// (client predict) against the REAL ServerActionExecutor (server execute) — this test project references BOTH — so the
// contract is checked end to end, not against a re-implemented model.
//
// THE MODEL (Model A, decided by the measure-first probe 6881c71): the client predicts the action on its LOCAL clock
// from the trigger and LEADS the server by the one-way latency along the SAME arc; the server runs the same action from
// receipt and force-includes the entity each tick; the client reconciles every snapshot and the LEAD lives in the
// unacked input buffer, so under no loss the reconcile is SILENT (no-loss == no-correction) and a server REJECTION is
// absorbed as a bounded, converging correction by the SAME reconcile (no special rollback path, design §2.6). B2 rides
// the existing per-frame predict/reconcile/replay: while an action owns movement the predictor integrates the LOCKED
// heading at the action speed (def.ForwardDistanceUnits × tickRate ÷ DurationTicks), so a jump's XY is constant-velocity
// — byte-equal to the server's per-tick ForwardArc on open ground (to float epsilon; the speed derivation rounds), and
// only a sub-tile residual at a wall (per-frame vs per-tick collision granularity), well under SnapThresholdUnits.
public sealed class ContinuousPredictorActionTests
{
    private const int TickRate = 20;
    private const double TickDt = 1.0d / TickRate; // 1 client frame per server tick — clean 1:1 alignment for the gate
    private const double Radius = CollisionDefaults.BodyRadius; // 0.5
    private const double MoveSpeed = 5d; // the ordinary move speed (distinct from the action speed)

    private static MovementActionDef Jump(uint duration = 12, double height = 2.25d, double distance = 6d, uint cooldown = 0)
        => MovementActionRegistry.BuildForwardArcJump(ActionId.Jump, duration, height, distance, cooldown, animationId: 1);

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

    private static ServerActionExecutor NewExecutor(TileGrid grid)
        => new(TickRate, () => Radius, grid.QueryNearbyWalls, (e, p) => e.ApplyResolvedMove(p));

    // Run the REAL server executor to completion on `grid`, returning the per-tick positions (index k = position AFTER
    // k Steps; index 0 = the trigger/origin, no XY move at tick 0). Padded a few ticks past the end (positions hold at
    // the landing) so a lagged reconcile can always index a valid "server D ticks ago".
    private static List<WorldVector> RunServer(TileGrid grid, MovementActionDef def, WorldVector heading, WorldEntity ent, int padTicks = 8)
    {
        var exec = NewExecutor(grid);
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
    public void Action_NoLoss_PredictMatchesServerExecutor_ZeroCorrection_LeadPreserved()
    {
        // THE GATE: open ground, no loss. The client predicts the jump per frame; the server runs it per tick, lagging
        // by D ticks. Reconciling each frame against the server's position D ticks ago must open ~ZERO correction (the
        // no-loss == no-correction contract extended to actions) AND the predicted must LEAD the server base by ~D ticks
        // of action travel (Model A's temporal lead — never re-based onto the lagging server position).
        var def = Jump();
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
            var seq = predictor.PredictAndBuffer(0d, 0d, TickDt); // held input zero — the action overrides it
            var ackTick = i - d;
            if (ackTick < 0)
            {
                continue; // server hasn't received/processed the trigger yet (still in flight)
            }

            var sp = serverPos[Math.Min(ackTick, serverPos.Count - 1)];
            predictor.Reconcile(sp, (uint)ackTick);

            // No loss + identical open-ground arc ⇒ replay reproduces the predicted present ⇒ no correction.
            Assert.True(predictor.LastCorrectionUnits < 1e-6, $"correction at frame {i}: {predictor.LastCorrectionUnits}");

            // While the action is still in flight on BOTH sides (ackTick within the arc, client still arcing), the
            // predicted LEADS the server base by exactly d ticks of action travel — the temporal lead, not a desync.
            if (ackTick >= 1 && i <= (int)def.DurationTicks)
            {
                Assert.Equal(d * perTick, predictor.ServerVsPredictedUnits, 6);
            }
        }

        // Both sides land at the IDENTICAL spot (origin + full reach); the predicted has fully converged there once the
        // last action tick is acked. No residual divergence — the lead was purely temporal.
        Assert.Equal(origin.X + def.ForwardDistanceUnits, predictor.PredictedX, 6);
        Assert.Equal(origin.Y, predictor.PredictedY, 6);
    }

    [Fact]
    public void Action_IntoWall_PerFrameVsPerTick_SubTileResidual_LandsShortAtSameFace()
    {
        // THE OPTION-2 TRADEOFF, measured: the client predicts per FRAME (60fps) while the server executor steps per
        // TICK (20Hz), so a jump INTO A WALL resolves at slightly different granularity. This pins that the divergence
        // is a SUB-TILE residual (≪ the 4u snap threshold, well under one tile) and that the client lands SHORT at the
        // SAME wall face the server does (never entering the blocked tile). The reconcile would smooth this residual
        // away; here we measure the raw geometric difference (no reconcile) so the bound is explicit.
        var def = Jump();
        var (speed, durationSeconds) = Motion(def);
        var heading = Direction8.E.ToUnitVector();

        var blockedArr = new[] { new TileCoord(12, 8) }; // a wall east of the (8,8) spawn — the jump lands short of it
        var grid = new TileGrid(64, 64, blockedArr);
        var blocked = new HashSet<TileCoord>(blockedArr);
        var serverEnt = NewEntity(new TileCoord(8, 8));
        var serverPos = RunServer(grid, def, heading, serverEnt);
        var origin = serverPos[0];
        var serverLanding = serverPos[^1];

        // Client predicts the SAME jump at 60fps (3 frames per 20Hz tick) — the realistic frame/tick mismatch.
        var predictor = new ContinuousPredictor(MoveSpeed, origin.X, origin.Y, blocked: blocked, radius: Radius);
        Assert.True(predictor.BeginAction(heading.X, heading.Y, speed, durationSeconds, def.JumpHeight, def.AirborneTicks, TickRate));
        const double frameDt = 1d / 60d;
        var guard = 0;
        while (predictor.IsActionActive && guard++ < 10_000)
        {
            predictor.PredictAndBuffer(0d, 0d, frameDt);
        }

        // Sub-tile residual vs the server's per-tick landing — far below the snap threshold (smoothed, not snapped).
        var residual = Math.Abs(predictor.PredictedX - serverLanding.X);
        Assert.True(residual < 1.0d, $"per-frame vs per-tick wall residual too large: {residual}");

        // Landed SHORT and never entered the blocked tile (the wall face is x = 11.5 - 0.5 = 11.0, like the executor test).
        Assert.True(predictor.PredictedX < origin.X + def.ForwardDistanceUnits - 1e-6, "client did not land short of the wall");
        Assert.True(predictor.PredictedX <= 11.0d + 1e-6, $"client entered/passed the wall face: x={predictor.PredictedX}");
    }

    [Fact]
    public void Action_Rejected_ConvergesToServer_Bounded_NoOscillation()
    {
        // THE REJECTED PATH: the server DENIES the action (cooldown mis-estimate / dead / rooted) and runs ordinary
        // movement from the heading MoveIntents instead (at the slower MOVE speed). The client predicted the faster
        // action, so it leads; the SAME reconcile pulls it back toward the server, bounded by the speed gap × duration,
        // converging with NO oscillation — no special rollback path (design §2.6). Modeled with an inline "server" that
        // integrates the heading at MOVE speed (the rejection) and acks every seq with a fixed lag.
        var def = Jump();
        var (speed, durationSeconds) = Motion(def);
        var heading = Direction8.E.ToUnitVector();
        var origin = new WorldVector(8d, 8d);

        var predictor = new ContinuousPredictor(MoveSpeed, origin.X, origin.Y, blocked: null, radius: Radius);
        Assert.True(predictor.BeginAction(heading.X, heading.Y, speed, durationSeconds, def.JumpHeight, def.AirborneTicks, TickRate));

        // The rejecting server: integrate the heading at MOVE speed each tick it processes (normal movement), recording
        // the position per processed input seq. seq k advances the server by one frame of MOVE-speed motion.
        var serverBySeq = new List<WorldVector> { origin };
        var sx = origin.X;
        const int d = 3;
        var frames = (int)def.DurationTicks + 10;
        var corrections = new List<double>();
        for (var i = 1; i <= frames; i++)
        {
            predictor.PredictAndBuffer(0d, 0d, TickDt); // action frames send the heading; afterwards zero input

            // The server processes seq i at MOVE speed along the heading while the action frames flow (i <= duration),
            // then stops (the client sends zero input after the action ends locally).
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

        // Bounded: the correction never exceeds the action-vs-move travel gap over the duration (a finite, modest pull —
        // not a runaway). And it converges — the predicted ends at the server's (shorter) move-speed position.
        var gap = (speed - MoveSpeed) * durationSeconds;
        Assert.All(corrections, c => Assert.True(c <= gap + 1e-6, $"correction {c} exceeded the bounded gap {gap}"));
        Assert.Equal(serverBySeq[^1].X, predictor.PredictedX, 4); // converged onto the server's actual (rejected) position
        Assert.Equal(origin.Y, predictor.PredictedY, 6);

        // Re-applying the final authoritative state does not move anything (no oscillation / runaway).
        var settled = predictor.PredictedX;
        predictor.Reconcile(serverBySeq[^1], (uint)(frames - d));
        Assert.Equal(settled, predictor.PredictedX, 6);
    }

    [Fact]
    public void Action_OneAtATime_SecondTriggerDeclinedUntilFirstEnds()
    {
        // Strictly serial (design §2.8): BeginAction returns false while an action is active, so the client never
        // predicts a chain the server would reject — the spam case produces no mispredict. Once the action elapses
        // locally, a fresh trigger is accepted again.
        var def = Jump(duration: 6, distance: 3);
        var (speed, durationSeconds) = Motion(def);
        var heading = Direction8.E.ToUnitVector();
        var predictor = new ContinuousPredictor(MoveSpeed, 0d, 0d, blocked: null, radius: Radius);

        Assert.True(predictor.BeginAction(heading.X, heading.Y, speed, durationSeconds, def.JumpHeight, def.AirborneTicks, TickRate));
        Assert.True(predictor.IsActionActive);

        // A second trigger while active is DECLINED (no second predicted action).
        Assert.False(predictor.BeginAction(heading.X, heading.Y, speed, durationSeconds, def.JumpHeight, def.AirborneTicks, TickRate));

        // Drive the action to completion (one frame per tick).
        for (var i = 0; i < (int)def.DurationTicks; i++)
        {
            predictor.PredictAndBuffer(0d, 0d, TickDt);
        }

        Assert.False(predictor.IsActionActive); // ended locally
        // Now a fresh trigger is accepted (one-at-a-time gate cleared).
        Assert.True(predictor.BeginAction(heading.X, heading.Y, speed, durationSeconds, def.JumpHeight, def.AirborneTicks, TickRate));
    }

    [Fact]
    public void Action_PredictedVerticalOffset_ArcsToApex_ThenReturnsToZero()
    {
        // Carry-forward #1: the LOCAL player renders its PREDICTED Z. The predicted vertical offset is 0 before the
        // action, arcs to ~JumpHeight near the midpoint, and returns to exactly 0 the instant the action ends locally
        // (so the end seam never pops up to a lagging server height). One frame per tick so the samples line up to the
        // ballistic ticks.
        const double height = 2d;
        var def = Jump(duration: 10, height: height, distance: 5);
        var (speed, durationSeconds) = Motion(def);
        var heading = Direction8.E.ToUnitVector();
        var predictor = new ContinuousPredictor(MoveSpeed, 0d, 0d, blocked: null, radius: Radius);

        Assert.Equal(0d, predictor.PredictedVerticalOffset, 9); // grounded before any action
        Assert.True(predictor.BeginAction(heading.X, heading.Y, speed, durationSeconds, def.JumpHeight, def.AirborneTicks, TickRate));

        var samples = new List<double> { predictor.PredictedVerticalOffset };
        for (var i = 0; i < (int)def.DurationTicks; i++)
        {
            predictor.PredictAndBuffer(0d, 0d, TickDt);
            samples.Add(predictor.PredictedVerticalOffset);
        }

        // Airborne strictly between takeoff and landing; apex ~JumpHeight at the midpoint; exactly 0 once it ends.
        for (var i = 1; i < (int)def.DurationTicks; i++)
        {
            Assert.True(samples[i] > 0d, $"tick {i} should be airborne (z={samples[i]})");
        }

        Assert.Equal(height, samples[(int)def.DurationTicks / 2], 6); // apex at the midpoint tick == JumpHeight
        Assert.False(predictor.IsActionActive);
        Assert.Equal(0d, predictor.PredictedVerticalOffset, 9); // landed: predicted Z back to exactly 0
    }
}
