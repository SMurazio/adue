using System.Collections.Generic;
using Mmo.Client.Core.Continuous;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// CONTINUOUS MIGRATION (Phase 4): headless, deterministic tests for the ported continuous predict -> reconcile ->
// replay loop. Ported near-verbatim from the exp/continuous-movement spike (Z->Y), adapted to the new API:
// Reconcile(in WorldVector, uint lastInputSeq) instead of Reconcile(in ContinuousState), and SHARED collision
// (ZoneModel.BlockedTiles via TileWalls.NeighborhoodWallsForMove) instead of the spike's standalone wall array.
// They assert the netcode invariants the migration hinges on: no-loss == no-correction, a forced divergence
// corrects within bound (no oscillation/runaway), acked inputs are trimmed, the buffer stays bounded, and the
// render never retreats on stop. The integrator math is the SAME normalize-then-scale the server uses, so a no-loss
// round trip reproduces the predicted path exactly. The collision-slide test exercises the shared resolver against
// a REAL blocked tile.
public sealed class ContinuousPredictorTests
{
    private const double Speed = 6.0d;
    private const double Dt = 1.0d / 20d; // 20Hz fixed tick

    // Integrate the same way the server/predictor do (open field, no collision), so a test "server" can mirror the
    // client's inputs exactly.
    private static (double X, double Y) Integrate(double x, double y, double dirX, double dirY, double dt)
    {
        var len = System.Math.Sqrt((dirX * dirX) + (dirY * dirY));
        if (len <= 1e-6 || dt <= 0) return (x, y);
        var inv = 1d / len;
        return (x + dirX * inv * Speed * dt, y + dirY * inv * Speed * dt);
    }

    [Fact]
    public void NoLoss_ReplayReproducesPrediction_NoCorrection()
    {
        var predictor = new ContinuousPredictor(Speed);

        // The "server": integrates every input it receives, in order, with the same fixed dt.
        double serverX = 0, serverY = 0;
        uint lastServerSeq = 0;

        // Drive 50 ticks of held east input; after each, the server processes the input and the client reconciles.
        for (int i = 0; i < 50; i++)
        {
            var seq = predictor.PredictAndBuffer(1d, 0d, Dt);

            // Server receives + integrates this exact input (no loss).
            (serverX, serverY) = Integrate(serverX, serverY, 1d, 0d, Dt);
            lastServerSeq = seq;

            predictor.Reconcile(new WorldVector(serverX, serverY), lastServerSeq);

            // No loss + matching integration => reconcile recomputes the SAME predicted present => no correction.
            Assert.Equal(0d, predictor.LastCorrectionUnits, 4);
        }

        // After every input is acked, the buffer is empty and predicted == server.
        Assert.Equal(0, predictor.BufferedInputCount);
        Assert.Equal(serverX, predictor.PredictedX, 4);
        Assert.Equal(serverY, predictor.PredictedY, 4);
    }

    [Fact]
    public void LaggedServer_PredictionLeads_ButReplayKeepsItConsistent()
    {
        var predictor = new ContinuousPredictor(Speed);

        // The server lags 5 inputs behind (a fixed in-flight window). Predict 20 ticks; the server only ever acks up
        // to seq-5. The prediction should run AHEAD by ~5 ticks of motion, with no spurious correction.
        double serverX = 0, serverY = 0;
        for (int i = 0; i < 20; i++)
        {
            var seq = predictor.PredictAndBuffer(1d, 0d, Dt);

            uint ackUpTo = seq > 5 ? seq - 5 : 0;
            serverX = ackUpTo * (Speed * Dt);
            serverY = 0;

            if (ackUpTo > 0)
            {
                predictor.Reconcile(new WorldVector(serverX, serverY), ackUpTo);
                Assert.True(predictor.LastCorrectionUnits < 1e-3, $"unexpected correction {predictor.LastCorrectionUnits}");
                Assert.Equal(5, predictor.BufferedInputCount);
                Assert.Equal(5 * Speed * Dt, predictor.ServerVsPredictedUnits, 4);
            }
        }
    }

    [Fact]
    public void ForcedDivergence_CorrectsTowardServer_WithinBound_NoOscillation()
    {
        var predictor = new ContinuousPredictor(Speed);

        // Predict 10 ticks east, but the SERVER only ever saw a STOPPED player (origin) at the same final seq — a
        // mispredict. Reconcile must pull the predicted present back toward truth (origin), bounded, then STAY
        // converged (no oscillation on repeated identical states).
        uint lastSeq = 0;
        for (int i = 0; i < 10; i++)
        {
            lastSeq = predictor.PredictAndBuffer(1d, 0d, Dt);
        }

        var predictedBefore = predictor.PredictedX;
        Assert.True(predictedBefore > 0);

        predictor.Reconcile(new WorldVector(0d, 0d), lastSeq);
        Assert.Equal(0, predictor.BufferedInputCount);
        Assert.Equal(0d, predictor.PredictedX, 4);
        Assert.Equal(0d, predictor.PredictedY, 4);
        var correction = predictor.LastCorrectionUnits;
        Assert.True(correction > 0, "expected a correction on the mispredict");
        Assert.True(correction <= predictedBefore + 1e-6, "correction must be bounded by the divergence");

        // Re-applying the SAME authoritative state must NOT move anything (no oscillation / runaway).
        predictor.Reconcile(new WorldVector(0d, 0d), lastSeq);
        Assert.Equal(0d, predictor.LastCorrectionUnits, 6);
        Assert.Equal(0d, predictor.PredictedX, 6);
    }

    [Fact]
    public void SteadyMotion_RenderEqualsPredicted_NoLead()
    {
        var predictor = new ContinuousPredictor(Speed);

        // PER-FRAME prediction: the predicted advances every frame, so there is ZERO render lead/lag — the render is
        // the predicted DIRECTLY in steady state. Drive steady eastward motion (server keeping up), advance render
        // each frame, assert render == predicted.
        uint seq = 0;
        for (int i = 0; i < 30; i++)
        {
            seq = predictor.PredictAndBuffer(1d, 0d, Dt);
            predictor.Reconcile(new WorldVector(seq * Speed * Dt, 0d), seq);
            predictor.AdvanceRender(Dt);

            Assert.Equal(predictor.PredictedX, predictor.RenderX, 4);
            Assert.Equal(predictor.PredictedY, predictor.RenderY, 4);
        }
    }

    [Fact]
    public void Stop_RenderNeverRetreats_SettlesAtPredicted()
    {
        var predictor = new ContinuousPredictor(Speed);

        // THE BUG THIS GUARDS: on releasing the move key the rendered dot must STOP IN PLACE, never snap BACKWARD.
        // Predict several frames east (server keeping up), then several frames of ZERO input (the stop). RenderX must
        // be monotonic non-decreasing the whole way and settle exactly at the predicted present.
        uint seq = 0;
        double prevRender = predictor.RenderX;

        for (int i = 0; i < 12; i++)
        {
            seq = predictor.PredictAndBuffer(1d, 0d, Dt);
            predictor.Reconcile(new WorldVector(seq * Speed * Dt, 0d), seq);
            predictor.AdvanceRender(Dt);
            Assert.True(predictor.RenderX >= prevRender - 1e-4, $"render retreated while moving: {predictor.RenderX} < {prevRender}");
            prevRender = predictor.RenderX;
        }

        var serverXAtStop = seq * Speed * Dt;

        for (int i = 0; i < 12; i++)
        {
            seq = predictor.PredictAndBuffer(0d, 0d, Dt);
            predictor.Reconcile(new WorldVector(serverXAtStop, 0d), seq);
            predictor.AdvanceRender(Dt);
            Assert.True(predictor.RenderX >= prevRender - 1e-4, $"render snapped BACK on stop: {predictor.RenderX} < {prevRender}");
            prevRender = predictor.RenderX;
        }

        Assert.Equal(predictor.PredictedX, predictor.RenderX, 4);
        Assert.Equal(serverXAtStop, predictor.RenderX, 4);
    }

    [Fact]
    public void Correction_RenderCatchUp_MonotonicNoOvershoot()
    {
        var predictor = new ContinuousPredictor(Speed);

        // Force a small mispredict so an offset opens, then assert the visible catch-up (the offset) shrinks
        // MONOTONICALLY to zero and never overshoots. Predict 5 ticks east; the server reports it only got 3 at the
        // SAME final seq, so the predicted pulls back a little.
        uint seq = 0;
        for (int i = 0; i < 5; i++)
        {
            seq = predictor.PredictAndBuffer(1d, 0d, Dt);
        }

        predictor.Reconcile(new WorldVector(3 * Speed * Dt, 0d), seq);
        var offset0 = predictor.RenderVsPredictedUnits;
        Assert.True(offset0 > 0, "expected an offset to open on the mispredict");

        double prevOffset = double.MaxValue;
        for (int frame = 0; frame < 200; frame++)
        {
            predictor.AdvanceRender(1d / 60d);
            var offset = predictor.RenderVsPredictedUnits;
            Assert.True(offset <= prevOffset + 1e-12, "offset must shrink monotonically (no overshoot)");
            prevOffset = offset;
        }

        Assert.Equal(0d, predictor.RenderVsPredictedUnits, 4);
        Assert.Equal(predictor.PredictedX, predictor.RenderX, 4);
    }

    [Fact]
    public void Buffer_StaysBounded_WhenServerSilent()
    {
        var predictor = new ContinuousPredictor(Speed);

        // The server never acks. The unacked buffer must NOT grow without limit — the hard cap drops the oldest.
        for (int i = 0; i < 5000; i++)
        {
            predictor.PredictAndBuffer(1d, 0d, Dt);
        }

        Assert.True(predictor.BufferedInputCount <= 256, $"buffer grew unbounded: {predictor.BufferedInputCount}");
    }

    [Fact]
    public void StaleState_OutOfOrder_Ignored()
    {
        var predictor = new ContinuousPredictor(Speed);
        for (int i = 0; i < 10; i++)
        {
            predictor.PredictAndBuffer(1d, 0d, Dt);
        }

        // Apply a newer ack (seq 8), then a STALE one (seq 3). The stale one must be ignored.
        var serverAt8 = 8 * Speed * Dt;
        predictor.Reconcile(new WorldVector(serverAt8, 0d), 8);
        var bufAfter8 = predictor.BufferedInputCount;
        var predAfter8 = predictor.PredictedX;

        predictor.Reconcile(new WorldVector(3 * Speed * Dt, 0d), 3);
        Assert.Equal(bufAfter8, predictor.BufferedInputCount);
        Assert.Equal(predAfter8, predictor.PredictedX, 6);
    }

    [Fact]
    public void DtClamp_BuffersClampedDt_NotRawDt()
    {
        var predictor = new ContinuousPredictor(Speed);

        // A single huge-dt frame must integrate AT MOST the shared MaxInputDtSeconds (the predictor clamps inside
        // PredictAndBuffer and BUFFERS the clamped dt). Predict one frame with dt = 10s; the predicted advance must be
        // Speed * MaxInputDtSeconds, not Speed * 10. And the buffered dt the server replays must be the clamped one,
        // so reconciling against a server that integrated the CLAMPED dt opens no correction.
        var seq = predictor.PredictAndBuffer(1d, 0d, 10d);
        Assert.Equal(Speed * ContinuousMovement.MaxInputDtSeconds, predictor.PredictedX, 6);

        // Server integrated the same clamped dt → no correction.
        var (serverX, serverY) = Integrate(0d, 0d, 1d, 0d, ContinuousMovement.MaxInputDtSeconds);
        predictor.Reconcile(new WorldVector(serverX, serverY), seq);
        Assert.Equal(0d, predictor.LastCorrectionUnits, 6);
    }

    [Fact]
    public void Collision_SlidesAlongRealWall_NoCorrectionVsServerResolver()
    {
        // The Phase-2 payoff vs the SHARED resolver: the predictor collides against a REAL blocked WALL COLUMN via
        // TileWalls.NeighborhoodWallsForMove, exactly as the server does. Drive NE into a wall column directly east;
        // the into-wall (X) component is blocked at the face, the tangential (Y) component slides north along the
        // column. A "server" that runs the IDENTICAL shared resolver over the SAME blocked set + radius lands
        // byte-identically, so no correction — even while grinding along the wall.
        var blocked = new HashSet<TileCoord>();
        for (var ty = 0; ty <= 30; ty++) // a tall column at x=10 so the north slide stays blocked the whole run
        {
            blocked.Add(new TileCoord(10, ty));
        }

        const double radius = CollisionDefaults.BodyRadius; // 0.5
        var predictor = new ContinuousPredictor(Speed, startX: 8d, startY: 8d, blocked: blocked, radius: radius);

        // Mirror the server: integrate each input through the same shared collision from the running server pos.
        double serverX = 8d, serverY = 8d;
        var scratch = new List<ContinuousCollision.Wall>();

        (double X, double Y) ServerIntegrate(double x, double y, double dirX, double dirY, double dt)
        {
            var len = System.Math.Sqrt((dirX * dirX) + (dirY * dirY));
            if (len <= 1e-6 || dt <= 0) return (x, y);
            var inv = 1d / len;
            var dx = dirX * inv * Speed * dt;
            var dy = dirY * inv * Speed * dt;
            var start = new WorldVector(x, y);
            var delta = new WorldVector(dx, dy);
            TileWalls.NeighborhoodWallsForMove(blocked, start, delta, radius, scratch);
            return ContinuousCollision.Resolve(x, y, dx, dy, radius, scratch);
        }

        for (int i = 0; i < 40; i++)
        {
            // NE: into the wall's column (+X) AND north (+Y). The slide preserves +Y, blocks +X at the face.
            var seq = predictor.PredictAndBuffer(1d, 1d, Dt);
            (serverX, serverY) = ServerIntegrate(serverX, serverY, 1d, 1d, Dt);
            predictor.Reconcile(new WorldVector(serverX, serverY), seq);

            // Zero corrections reconciling against a REAL wall — the determinism contract at walls.
            Assert.True(predictor.LastCorrectionUnits < 1e-9, $"wall correction at i={i}: {predictor.LastCorrectionUnits}");
        }

        // The predictor never entered the blocked column (X pinned at the -X face minus radius = 9.5 - 0.5 = 9.0).
        Assert.True(predictor.PredictedX <= 9.0d + 1e-6, $"entered/passed the wall: x={predictor.PredictedX}");
        // It DID slide north (the tangential component was preserved).
        Assert.True(predictor.PredictedY > 8.5d, $"did not slide along the wall: y={predictor.PredictedY}");
    }

    // PLAYER↔MONSTER COLLISION: the predicted local player collides against a MONSTER obstacle (a Circle) via the SAME
    // shared resolver the server uses, so the prediction stops at the radius-sum and a "server" running the IDENTICAL
    // resolve over the SAME obstacle set opens NO correction. PARITY NOTE: this unit test feeds client + server the
    // SAME obstacle set, so it is trivially equal — exactly the point. The real-world divergence is NOT modelled here:
    // it is the MOVING-obstacle STALENESS (the client predicts vs the monster's last replicated position while the
    // server uses its authoritative current one), which is a bounded reconcile feel-tested live, not a headless unit.
    [Fact]
    public void PredictsCollisionAgainstAMonsterObstacle_StopsAtRadiusSum_ParityNoCorrection()
    {
        const double radius = CollisionDefaults.BodyRadius; // 0.5
        var predictor = new ContinuousPredictor(Speed, startX: 0d, startY: 0d, blocked: null, radius: radius);

        // A stationary monster body at (3,0). Bodies overlap within radius+radius = 1.0, so the predicted centre must
        // stop at x = 2.0 (the radius-sum west of the obstacle), never overlapping.
        var obstacles = new List<ContinuousCollision.Circle> { new(3d, 0d, radius) };
        var noWalls = new List<ContinuousCollision.Wall>();

        double serverX = 0d, serverY = 0d;
        for (int i = 0; i < 60; i++)
        {
            var seq = predictor.PredictAndBuffer(1d, 0d, Dt, obstacles);

            // The "server" runs the IDENTICAL shared resolve over the SAME obstacle set (open field → empty walls).
            var dx = Speed * Dt;
            (serverX, serverY) = ContinuousCollision.Resolve(serverX, serverY, dx, 0d, radius, noWalls, obstacles);

            predictor.Reconcile(new WorldVector(serverX, serverY), seq, obstacles);
            Assert.Equal(0d, predictor.LastCorrectionUnits, 6); // same obstacle both sides → no correction (parity).
        }

        Assert.Equal(2.0d, predictor.PredictedX, 4); // stopped exactly at the radius-sum, never overlapping.
        Assert.Equal(0d, predictor.PredictedY, 6);
    }

    // N (entity-collision walk anim): ResolvedSpeedSquared is the REAL per-frame translation the collider produced,
    // NOT the raw input — so the local avatar's walk/idle can key off actual motion. Open field ⇒ dir×speed; a
    // zero-input (stopped) frame ⇒ 0; driving head-on into a wall ⇒ ~0 (pinned, so the visual idles like a flat wall);
    // sliding along a wall ⇒ the tangential component (non-zero, so it keeps walking).

    [Fact]
    public void ResolvedSpeed_OpenField_EqualsWalkSpeed()
    {
        var predictor = new ContinuousPredictor(Speed);

        predictor.PredictAndBuffer(1d, 0d, Dt); // held east on open ground

        // The resolved motion is the full walk step, so the resolved speed magnitude == the walk speed.
        Assert.Equal(Speed * Speed, predictor.ResolvedSpeedSquared, 6);
    }

    [Fact]
    public void ResolvedSpeed_NoInput_IsZero()
    {
        var predictor = new ContinuousPredictor(Speed);

        predictor.PredictAndBuffer(1d, 0d, Dt); // move once
        Assert.True(predictor.ResolvedSpeedSquared > 0d);

        predictor.PredictAndBuffer(0d, 0d, Dt); // release input → no translation this frame → resolved velocity 0

        Assert.Equal(0d, predictor.ResolvedSpeedSquared, 9);
    }

    [Fact]
    public void ResolvedSpeed_BlockedHeadOnIntoWall_IsNearZero()
    {
        // A wall column at x=10; the player pinned at the -X face (x = 9.5 - radius 0.5 = 9.0) pushing straight east is
        // fully blocked → the resolve produces no translation → ResolvedSpeedSquared ~0 → the avatar idles (like a flat
        // wall). Well BELOW the walk/idle epsilon (0.25 = (0.5 u/s)²) that MmoClient.ToRenderState keys the anim off.
        var blocked = new HashSet<TileCoord>();
        for (var ty = 0; ty <= 30; ty++)
        {
            blocked.Add(new TileCoord(10, ty));
        }

        const double radius = CollisionDefaults.BodyRadius; // 0.5
        var predictor = new ContinuousPredictor(Speed, startX: 9.0d, startY: 8d, blocked: blocked, radius: radius);

        for (var i = 0; i < 10; i++)
        {
            predictor.PredictAndBuffer(1d, 0d, Dt); // shove east into the wall face
        }

        Assert.True(predictor.ResolvedSpeedSquared < 0.25d,
            $"blocked-into-wall resolved speed not idle: {predictor.ResolvedSpeedSquared}");
    }

    [Fact]
    public void ResolvedSpeed_SlidingAlongWall_KeepsTangentialMotion()
    {
        // Driving NE into a wall column east: the +X (into-wall) component is blocked, the +Y (tangential) component
        // slides north — a genuine translation, so ResolvedSpeedSquared stays well ABOVE the idle epsilon (the avatar
        // keeps walking while grinding along the wall, which is correct).
        var blocked = new HashSet<TileCoord>();
        for (var ty = 0; ty <= 30; ty++)
        {
            blocked.Add(new TileCoord(10, ty));
        }

        const double radius = CollisionDefaults.BodyRadius; // 0.5
        var predictor = new ContinuousPredictor(Speed, startX: 8d, startY: 8d, blocked: blocked, radius: radius);

        // A few frames to reach the wall face and settle into the pure north slide.
        for (var i = 0; i < 20; i++)
        {
            predictor.PredictAndBuffer(1d, 1d, Dt);
        }

        Assert.True(predictor.ResolvedSpeedSquared > 0.25d,
            $"sliding resolved speed read as idle: {predictor.ResolvedSpeedSquared}");
        // The slide is the tangential half of the diagonal walk (into-wall component blocked), so its magnitude is
        // ~Speed/√2; assert it is a clear, non-degenerate translation.
        Assert.True(predictor.ResolvedSpeedSquared < Speed * Speed,
            "a fully-blocked axis should reduce the resolved speed below the open-field walk speed");
    }
}
