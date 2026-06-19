using System.Collections.Generic;
using Mmo.Client.Core;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// S53 movement-prediction bar: the predictor mirrors the server's held-intent step loop for the local
// player, re-bases on each authoritative self-snapshot, and reconciles EXACTLY to the server on a
// disagreement while NOT rubber-banding on the steady path. These tests drive the predictor against a
// tiny in-process model of the server's step rule (one tile per cadence in the held direction, validating
// IsWalkable) and assert tile-for-tile agreement plus the correction outcomes.
public sealed class LocalPlayerPredictorTests
{
    private const double Cadence = 150d;

    // Open field: every tile walkable.
    private static bool OpenField(TileCoord _) => true;

    private static TileInterpolator NewInterpolator(TileCoord start) => new(start, Cadence, 0);

    // ---- Predict: snappy first step + faithful stepping -------------------------------------------

    [Fact]
    public void FirstStepFiresImmediatelyOnKeydown_NoRoundTrip()
    {
        var predictor = new LocalPlayerPredictor(new TileCoord(0, 0), Direction8.S, Cadence, OpenField, NewInterpolator(new TileCoord(0, 0)));

        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        var stepped = predictor.Tick(TimeSpan.Zero);

        Assert.True(stepped);
        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);
        Assert.Equal(Direction8.E, predictor.Facing);
    }

    [Fact]
    public void StepsOneTilePerCadence_NotPerFrame()
    {
        var predictor = new LocalPlayerPredictor(new TileCoord(0, 0), Direction8.E, Cadence, OpenField, NewInterpolator(new TileCoord(0, 0)));
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);

        predictor.Tick(TimeSpan.Zero);                              // step -> (1,0)
        // Many frames inside the same cadence window: no further step.
        for (var ms = 10; ms < 150; ms += 10)
        {
            Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(ms)));
        }

        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);

        Assert.True(predictor.Tick(TimeSpan.FromMilliseconds(150)));  // cadence elapsed -> (2,0)
        Assert.Equal(new TileCoord(2, 0), predictor.PredictedTile);
    }

    [Fact]
    public void BlockedTarget_HoldsAtWall_DoesNotConsumeCooldown()
    {
        // Wall at x>=2: the avatar can reach (1,0) but not step onto (2,0).
        bool walkable(TileCoord t) => t.X < 2;
        var predictor = new LocalPlayerPredictor(new TileCoord(0, 0), Direction8.E, Cadence, walkable, NewInterpolator(new TileCoord(0, 0)));
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);

        predictor.Tick(TimeSpan.Zero);                  // (0,0)->(1,0)
        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);

        // Next cadence: target (2,0) blocked -> hold at the wall, no movement, still facing E.
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(150)));
        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);
        Assert.Equal(Direction8.E, predictor.Facing);
    }

    [Fact]
    public void Keyup_StopsProjectingForward()
    {
        var predictor = new LocalPlayerPredictor(new TileCoord(0, 0), Direction8.E, Cadence, OpenField, NewInterpolator(new TileCoord(0, 0)));
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        predictor.Tick(TimeSpan.Zero);                  // (1,0)
        predictor.Tick(TimeSpan.FromMilliseconds(150));  // (2,0)

        predictor.SetIntent(false, Direction8.E, TimeSpan.FromMilliseconds(160));

        // No further steps no matter how much time passes.
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(1000)));
        Assert.Equal(new TileCoord(2, 0), predictor.PredictedTile);
        Assert.False(predictor.IsMoving);
    }

    // ---- No-divergence steady state: prediction == server, no correction fires --------------------

    [Fact]
    public void SteadyState_MatchingMapAndCadence_NoCorrectionFires()
    {
        // Mirror the server: it steps the same held intent at the same cadence, so its confirmations climb
        // the same tiles the client predicts. The confirmed tile trails the prediction by the in-flight
        // steps; every Reconcile must return Matched (no rubber-band on normal play).
        var predictor = new LocalPlayerPredictor(new TileCoord(0, 0), Direction8.E, Cadence, OpenField, NewInterpolator(new TileCoord(0, 0)));
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);

        var serverTile = new TileCoord(0, 0);
        for (var step = 0; step < 10; step++)
        {
            var now = TimeSpan.FromMilliseconds(step * 150);
            predictor.Tick(now);

            // Server confirms its position from the PREVIOUS step (trails by one in-flight step), the worst
            // realistic steady-state lag. Must not yank the prediction back.
            var outcome = predictor.Reconcile(serverTile, now);
            Assert.Equal(LocalPlayerPredictor.ReconcileOutcome.Matched, outcome);

            // Predicted tile is exactly `step+1` tiles east of origin — tile-for-tile with what the server
            // will confirm next.
            Assert.Equal(new TileCoord(step + 1, 0), predictor.PredictedTile);

            serverTile = new TileCoord(step + 1, 0); // server catches up to where the client just predicted
        }
    }

    [Fact]
    public void SteadyState_ConfirmExactlyEqualsPrediction_NoCorrection()
    {
        // Zero-latency limit (LAN): the confirmed tile equals the predicted tile each snapshot.
        var predictor = new LocalPlayerPredictor(new TileCoord(0, 0), Direction8.E, Cadence, OpenField, NewInterpolator(new TileCoord(0, 0)));
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);

        for (var step = 0; step < 10; step++)
        {
            var now = TimeSpan.FromMilliseconds(step * 150);
            predictor.Tick(now);
            var outcome = predictor.Reconcile(predictor.PredictedTile, now);
            Assert.Equal(LocalPlayerPredictor.ReconcileOutcome.Matched, outcome);
        }
    }

    // ---- Convergence: a server disagreement reconciles EXACTLY ------------------------------------

    [Fact]
    public void ServerRejectsAStep_PredictionReconcilesExactly()
    {
        // The client thinks (2,0) is walkable and predicts onto it, but the server rejects that step (its
        // authoritative map blocks it / a race). The server stops at (1,0). The prediction must reconcile
        // EXACTLY to (1,0) and continue correctly from there.
        var predictor = new LocalPlayerPredictor(new TileCoord(0, 0), Direction8.E, Cadence, OpenField, NewInterpolator(new TileCoord(0, 0)));
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);

        predictor.Tick(TimeSpan.Zero);                   // predict (1,0)
        predictor.Tick(TimeSpan.FromMilliseconds(150));   // predict (2,0) -- the disputed tile

        Assert.Equal(new TileCoord(2, 0), predictor.PredictedTile);

        // Player releases the key (so the server's confirmation is its FINAL answer, not a catch-up).
        predictor.SetIntent(false, Direction8.E, TimeSpan.FromMilliseconds(160));

        // Server's authoritative truth: it stopped at (1,0) (rejected the (2,0) step).
        var outcome = predictor.Reconcile(new TileCoord(1, 0), TimeSpan.FromMilliseconds(200));

        Assert.Equal(LocalPlayerPredictor.ReconcileOutcome.Corrected, outcome);
        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);   // EXACT reconciliation
    }

    [Fact]
    public void MidMoveSpeedChange_AdoptedImmediately_ContinuesCorrectly()
    {
        var predictor = new LocalPlayerPredictor(new TileCoord(0, 0), Direction8.E, Cadence, OpenField, NewInterpolator(new TileCoord(0, 0)));
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        predictor.Tick(TimeSpan.Zero);                   // (1,0) at cadence 150

        // Speed buff: cadence halves to 75ms mid-move. The next step should arrive 75ms after the prior
        // step, not 150ms.
        predictor.SetCadence(75);
        Assert.Equal(75d, predictor.CadenceMs);

        // Prior step fired at t=0 and scheduled the next at +150 (old cadence already armed); after that the
        // new 75ms cadence governs. Drive to t=300 and count steps: t=150 -> (2,0), then +75 each.
        predictor.Tick(TimeSpan.FromMilliseconds(150));   // (2,0)
        predictor.Tick(TimeSpan.FromMilliseconds(225));   // (3,0) at new cadence
        predictor.Tick(TimeSpan.FromMilliseconds(300));   // (4,0)

        Assert.Equal(new TileCoord(4, 0), predictor.PredictedTile);
    }

    [Fact]
    public void LargeDisagreement_SnapsTheInterpolator()
    {
        var interpolator = NewInterpolator(new TileCoord(0, 0));
        var predictor = new LocalPlayerPredictor(new TileCoord(0, 0), Direction8.E, Cadence, OpenField, interpolator);
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        predictor.Tick(TimeSpan.Zero);                   // (1,0)
        predictor.SetIntent(false, Direction8.E, TimeSpan.FromMilliseconds(10));

        // Server teleported the player far away (knockback / admin tp): a large correction snaps.
        var outcome = predictor.Reconcile(new TileCoord(40, 40), TimeSpan.FromMilliseconds(20));

        Assert.Equal(LocalPlayerPredictor.ReconcileOutcome.Snapped, outcome);
        Assert.Equal(new TileCoord(40, 40), predictor.PredictedTile);
        // The interpolator was reset to the truth (render position jumps, no multi-tile smear).
        Assert.Equal(40d, interpolator.RenderPosition.X);
        Assert.Equal(40d, interpolator.RenderPosition.Y);
    }

    [Fact]
    public void StartStopBoundary_ConvergesToServerStopTile()
    {
        // Predict three steps, stop, then the server's final confirmation lands one tile short of the
        // prediction (the last in-flight step had not been processed when the player released). Reconcile
        // must converge exactly to the server's stop tile with a small (non-snapping) correction.
        var predictor = new LocalPlayerPredictor(new TileCoord(0, 0), Direction8.E, Cadence, OpenField, NewInterpolator(new TileCoord(0, 0)));
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        predictor.Tick(TimeSpan.Zero);                   // (1,0)
        predictor.Tick(TimeSpan.FromMilliseconds(150));   // (2,0)
        predictor.Tick(TimeSpan.FromMilliseconds(300));   // (3,0)

        predictor.SetIntent(false, Direction8.E, TimeSpan.FromMilliseconds(310));

        var outcome = predictor.Reconcile(new TileCoord(2, 0), TimeSpan.FromMilliseconds(360));

        Assert.Equal(LocalPlayerPredictor.ReconcileOutcome.Corrected, outcome);
        Assert.Equal(new TileCoord(2, 0), predictor.PredictedTile);
    }
}
