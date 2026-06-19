using Mmo.Client.Core;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// S53 (redo) movement-prediction bar: the predictor mirrors the server's held-intent step loop for the local
// player, renders it at the predicted tile with its OWN present-time tween (no playout-buffer delay), and on
// an authoritative self-snapshot snaps/blends to the server's truth on divergence while NOT rubber-banding on
// the steady path. These tests drive the predictor against a tiny in-process model of the server's step rule
// (one tile per cadence in the held direction, validating IsWalkable) and assert tile-for-tile agreement, the
// present-time render position, and the reconcile outcomes.
public sealed class LocalPlayerPredictorTests
{
    private const double Cadence = 150d;

    // Open field: every tile walkable.
    private static bool OpenField(TileCoord _) => true;

    private static LocalPlayerPredictor NewPredictor(TileCoord start, Direction8 facing, Func<TileCoord, bool>? walkable = null)
        => new(start, facing, Cadence, walkable ?? OpenField);

    // ---- Predict: snappy first step + faithful stepping -------------------------------------------

    [Fact]
    public void FirstStepFiresImmediatelyOnKeydown_NoRoundTrip()
    {
        // Already facing E, so the first step is a MOVE with no round-trip wait. (Starting in a direction you
        // don't face turns in place first — covered by the turn-then-move tests below.)
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);

        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        var stepped = predictor.Tick(TimeSpan.Zero);

        Assert.True(stepped);
        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);
        Assert.Equal(Direction8.E, predictor.Facing);
    }

    [Fact]
    public void StepsOneTilePerCadence_NotPerFrame()
    {
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
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

    // ---- Present-time render tween (the snappy part; no playout delay) ----------------------------

    [Fact]
    public void RenderTweens_PresentTime_BetweenTileCenters_NoDelay()
    {
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);

        predictor.Tick(TimeSpan.Zero);                                // step -> (1,0), tween 0..150ms
        // The very instant the step is accepted the render still sits at the old center (no past-rendering
        // playout buffer would START the move yet); it then glides to the new center over the cadence.
        Assert.Equal(0d, predictor.Sample(TimeSpan.Zero).X, 3);
        Assert.Equal(0.5d, predictor.Sample(TimeSpan.FromMilliseconds(75)).X, 3);   // halfway across the tile
        Assert.Equal(1d, predictor.Sample(TimeSpan.FromMilliseconds(150)).X, 3);    // arrived at present time
    }

    [Fact]
    public void BlockedTarget_HoldsAtWall_DoesNotConsumeCooldown()
    {
        // Wall at x>=2: the avatar can reach (1,0) but not step onto (2,0).
        bool walkable(TileCoord t) => t.X < 2;
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E, walkable);
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
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
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
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
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
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
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
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
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
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
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
    public void LargeDisagreement_SnapsTheRenderInstantly()
    {
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        predictor.Tick(TimeSpan.Zero);                   // (1,0)
        predictor.SetIntent(false, Direction8.E, TimeSpan.FromMilliseconds(10));

        // Server teleported the player far away (knockback / admin tp): a large correction snaps.
        var outcome = predictor.Reconcile(new TileCoord(40, 40), TimeSpan.FromMilliseconds(20));

        Assert.Equal(LocalPlayerPredictor.ReconcileOutcome.Snapped, outcome);
        Assert.Equal(new TileCoord(40, 40), predictor.PredictedTile);
        // The render jumps to the truth at present time — no multi-tile smear.
        var render = predictor.Sample(TimeSpan.FromMilliseconds(20));
        Assert.Equal(40d, render.X, 3);
        Assert.Equal(40d, render.Y, 3);
    }

    // ---- S56: predictor mirrors the server cooldown EXACTLY (no early step on a direction flip) -----

    [Fact]
    public void DirectionFlipsWithinOneCadence_DoNotAddExtraSteps_MatchServerCount()
    {
        // The server steps ONCE per cadence regardless of how the held direction changes (TryStep gates on
        // _lastStepTick, which only a real step advances). The predictor must do the same: a flurry of
        // direction changes inside one cadence window produces exactly one step (the one due that window),
        // not one per flip.
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);

        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        Assert.True(predictor.Tick(TimeSpan.Zero));                       // first step fires -> (1,0)
        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);

        // Rapidly flip direction several times, all WITHIN the first cadence window. Each SetIntent updates
        // facing/direction but must NOT bring the next step earlier than _lastStep + cadence.
        predictor.SetIntent(true, Direction8.N, TimeSpan.FromMilliseconds(20));
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(20)));      // no extra step on the flip
        predictor.SetIntent(true, Direction8.W, TimeSpan.FromMilliseconds(40));
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(40)));
        predictor.SetIntent(true, Direction8.S, TimeSpan.FromMilliseconds(60));
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(60)));

        // Facing only changes on a STEP (turn-then-move), and no step has been due since the first, so facing
        // is still E even though the held direction is now S.
        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);
        Assert.Equal(Direction8.E, predictor.Facing);

        // The next step is due a full cadence after the first (t=150). Because the held direction (S) differs
        // from facing (E), that single step is a TURN in place — not a move.
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(150)));
        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);
        Assert.Equal(Direction8.S, predictor.Facing);

        // Now facing S, the following step (t=300) moves.
        Assert.True(predictor.Tick(TimeSpan.FromMilliseconds(300)));
        Assert.Equal(new TileCoord(1, 1), predictor.PredictedTile);       // stepped S from (1,0)
    }

    [Fact]
    public void DirectionChangeMidMove_StepsOnServerCadence_NotImmediately()
    {
        // A single mid-move redirect (E for a while, then N) must keep the cadence: the post-redirect step
        // lands at the next cadence boundary, never the instant the direction changed.
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        predictor.Tick(TimeSpan.Zero);                                    // (1,0) at t=0
        predictor.Tick(TimeSpan.FromMilliseconds(150));                   // (2,0) at t=150

        // Redirect to N at t=160 (mid-cadence). No step should fire until the next boundary (t=300).
        predictor.SetIntent(true, Direction8.N, TimeSpan.FromMilliseconds(160));
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(160)));
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(299)));
        Assert.Equal(new TileCoord(2, 0), predictor.PredictedTile);

        // At the next boundary (t=300) the redirect first TURNS to N (no move); the move N follows a cadence
        // later (t=450).
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(300)));     // turn to N
        Assert.Equal(new TileCoord(2, 0), predictor.PredictedTile);
        Assert.Equal(Direction8.N, predictor.Facing);

        Assert.True(predictor.Tick(TimeSpan.FromMilliseconds(450)));      // now facing N -> moves
        Assert.Equal(new TileCoord(2, -1), predictor.PredictedTile);
    }

    [Fact]
    public void FreshStartFromIdle_StepsPromptly_AfterIdlingPastCadence()
    {
        // Idle long past a cadence (the cooldown elapsed while standing still), then start: the first step is
        // prompt — matching the server, whose cooldown is also long elapsed.
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        predictor.Tick(TimeSpan.Zero);                                    // (1,0), last step at t=0
        predictor.SetIntent(false, Direction8.E, TimeSpan.FromMilliseconds(10)); // stop

        // Idle well past a cadence, then press again at t=1000.
        predictor.SetIntent(true, Direction8.E, TimeSpan.FromMilliseconds(1000));
        Assert.True(predictor.Tick(TimeSpan.FromMilliseconds(1000)));     // prompt: cadence long elapsed
        Assert.Equal(new TileCoord(2, 0), predictor.PredictedTile);
    }

    [Fact]
    public void QuickStopStart_WithinCadence_DoesNotDoubleStep()
    {
        // Step, stop, then immediately start again — all inside one cadence window. The re-press must NOT
        // fire a second step early; it respects the cadence since the last accepted step (mirrors the server
        // keeping _lastStepTick across the stop).
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        predictor.Tick(TimeSpan.Zero);                                    // (1,0), last step at t=0
        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);

        predictor.SetIntent(false, Direction8.E, TimeSpan.FromMilliseconds(30)); // stop early in the window
        predictor.SetIntent(true, Direction8.E, TimeSpan.FromMilliseconds(50));  // restart still in the window

        // No step before the cadence since the LAST step (t=0) elapses.
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(50)));
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(149)));
        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);

        Assert.True(predictor.Tick(TimeSpan.FromMilliseconds(150)));      // exactly one more step at the boundary
        Assert.Equal(new TileCoord(2, 0), predictor.PredictedTile);
    }

    [Fact]
    public void PredictorMatchesServerStepCount_OverRapidFlips()
    {
        // End-to-end parity: drive the predictor and a tiny in-process model of WorldEntity.TryStep with the
        // SAME held-intent timeline (a press, then rapid direction flips), and assert identical step counts.
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);

        // Server model with turn-then-move: a step is due one cadence after the last; a step in a new
        // direction TURNS (no tile move), only a step in the faced direction MOVES.
        TimeSpan? serverLastStep = null;
        var serverFacing = Direction8.E;
        var serverTile = new TileCoord(0, 0);
        var serverMoves = 0;
        var currentDir = Direction8.E;

        var predictedMoves = 0;
        var lastPredicted = predictor.PredictedTile;

        // A timeline of (timeMs, direction) intents: a press at 0, then flips at 25/55/85, holding to 600ms.
        var intents = new (double ms, Direction8 dir)[]
        {
            (0, Direction8.E), (25, Direction8.N), (55, Direction8.W), (85, Direction8.S),
        };
        var intentIndex = 0;

        for (var ms = 0; ms <= 600; ms += 5)
        {
            var now = TimeSpan.FromMilliseconds(ms);
            while (intentIndex < intents.Length && intents[intentIndex].ms <= ms)
            {
                predictor.SetIntent(true, intents[intentIndex].dir, now);
                currentDir = intents[intentIndex].dir;
                intentIndex++;
            }

            // Server tick model (turn-then-move): a step is due a full cadence after the last; it turns when
            // the held direction differs from facing, otherwise moves one tile.
            if (serverLastStep is null || now >= serverLastStep.Value + TimeSpan.FromMilliseconds(Cadence))
            {
                if (currentDir != serverFacing)
                {
                    serverFacing = currentDir; // turn in place
                }
                else
                {
                    var d = currentDir.Delta();
                    serverTile = serverTile.Offset(d.X, d.Y);
                    serverMoves++;
                }

                serverLastStep = serverLastStep is null
                    ? now
                    : serverLastStep.Value + TimeSpan.FromMilliseconds(Cadence);
            }

            predictor.Tick(now);
            if (predictor.PredictedTile != lastPredicted)
            {
                predictedMoves++;
                lastPredicted = predictor.PredictedTile;
            }
        }

        // Tile-for-tile, move-for-move, and facing parity between the predictor and the server rule.
        Assert.Equal(serverTile, predictor.PredictedTile);
        Assert.Equal(serverMoves, predictedMoves);
        Assert.Equal(serverFacing, predictor.Facing);
    }

    [Fact]
    public void StartStopBoundary_ConvergesToServerStopTile()
    {
        // Predict three steps, stop, then the server's final confirmation lands one tile short of the
        // prediction (the last in-flight step had not been processed when the player released). Reconcile
        // must converge exactly to the server's stop tile with a small (non-snapping) correction.
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
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
