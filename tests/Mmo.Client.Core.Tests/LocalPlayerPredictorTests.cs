using Mmo.Client.Core;
using Mmo.Server.Runtime;
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
    // S63 turn delay: a turn costs this (not a full cadence). 80 ms is the ServerOptions default; the tests
    // pass it explicitly so the turn-vs-step timing is unambiguous.
    private const double TurnDelay = 80d;

    // Open field: every tile walkable.
    private static bool OpenField(TileCoord _) => true;

    private static LocalPlayerPredictor NewPredictor(TileCoord start, Direction8 facing, Func<TileCoord, bool>? walkable = null)
        => new(start, facing, Cadence, walkable ?? OpenField, TurnDelay);

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

        // The next action is due a full cadence after the first (t=150). Because the held direction (S)
        // differs from facing (E), that single action is a TURN in place — not a move.
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(150)));
        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);
        Assert.Equal(Direction8.S, predictor.Facing);

        // S63: the turn freed the next action after the TURN DELAY (80 ms), not a full cadence — so the move S
        // lands at t=150+80=230, not t=300. (Before t=230 nothing fires.)
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(229)));
        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);
        Assert.True(predictor.Tick(TimeSpan.FromMilliseconds(230)));
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

        // At the next boundary (t=300) the redirect first TURNS to N (no move). S63: the move N follows a
        // TURN DELAY (80 ms) later — t=380 — not a full cadence (t=450).
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(300)));     // turn to N
        Assert.Equal(new TileCoord(2, 0), predictor.PredictedTile);
        Assert.Equal(Direction8.N, predictor.Facing);

        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(379)));     // turn delay not yet elapsed
        Assert.Equal(new TileCoord(2, 0), predictor.PredictedTile);
        Assert.True(predictor.Tick(TimeSpan.FromMilliseconds(380)));      // now facing N -> moves
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

        // Server model with turn-then-move + S63 turn delay: the next action is due at serverNextEligible; a
        // step in a new direction TURNS (no tile move) and frees the next action after the TURN DELAY, only a
        // step in the faced direction MOVES and frees the next after a full cadence.
        TimeSpan? serverNextEligible = null;
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

            // Server tick model (turn-then-move + turn delay): the next action is due at serverNextEligible. A
            // turn frees the next after TurnDelay; a move frees it after a full Cadence.
            if (serverNextEligible is null || now >= serverNextEligible.Value)
            {
                var actionAt = serverNextEligible ?? now;
                if (currentDir != serverFacing)
                {
                    serverFacing = currentDir; // turn in place
                    serverNextEligible = actionAt + TimeSpan.FromMilliseconds(TurnDelay);
                }
                else
                {
                    var d = currentDir.Delta();
                    serverTile = serverTile.Offset(d.X, d.Y);
                    serverMoves++;
                    serverNextEligible = actionAt + TimeSpan.FromMilliseconds(Cadence);
                }
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
    public void TurnPathParity_AgainstRealWorldEntity_TileFacingMatchEachTick()
    {
        // S63 turn-path parity: drive the REAL server WorldEntity.TryStep and the predictor on the SAME
        // held-intent timeline with tick-aligned cooldown/turn-delay, and assert tile + facing agree every
        // tick. A drift here is exactly the S56 rapid-direction-change snap the turn delay must avoid.
        const int tickRate = 20;                 // 50 ms/tick
        const double tickMs = 1000d / tickRate;  // 50 ms
        const uint stepCooldownTicks = 3;        // 150 ms
        const uint turnDelayTicks = 2;           // 100 ms
        var stepCadenceMs = MovementCadence.EffectiveStepCadenceMs(150, tickRate);     // 150 ms
        var turnDelayMs = MovementCadence.EffectiveTurnDelayMs(100, tickRate);          // 100 ms

        var grid = new TileGrid(64, 64, []);
        var entity = new WorldEntity(1, 1, EntityKind.Player, new TileCoord(10, 10), Direction8.E,
            "Local", System.Guid.NewGuid(), ownerSession: null, isDurable: true);
        var predictor = new LocalPlayerPredictor(new TileCoord(10, 10), Direction8.E, stepCadenceMs,
            t => grid.IsWalkable(t), turnDelayMs);

        // Held-direction timeline by tick: press E, then whip through N/W/S, then settle on S.
        var dirByTick = new System.Collections.Generic.Dictionary<uint, Direction8>
        {
            [0] = Direction8.E, [4] = Direction8.N, [5] = Direction8.W, [6] = Direction8.S,
        };
        var held = Direction8.E;
        predictor.SetIntent(true, held, TimeSpan.Zero);

        for (uint tick = 0; tick <= 30; tick++)
        {
            if (dirByTick.TryGetValue(tick, out var d))
            {
                held = d;
                predictor.SetIntent(true, held, TimeSpan.FromMilliseconds(tick * tickMs));
            }

            entity.TryStep(held, tick, stepCooldownTicks, turnDelayTicks, grid);
            predictor.Tick(TimeSpan.FromMilliseconds(tick * tickMs));

            Assert.Equal(entity.Tile, predictor.PredictedTile);
            Assert.Equal(entity.Facing, predictor.Facing);
        }
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

    // ---- S71: a stale old-direction confirm after a reversal must NOT freeze a full cadence ----------

    [Fact]
    public void ReversalThenStaleConfirm_DoesNotStallAFullCadence_KeepsStepping()
    {
        // The S71 bug: held E for a while, then flip to W. The server's already-in-flight EAST confirmations
        // keep arriving AFTER the flip. Such a confirm misses IsBehindOnPredictedLine (which only knows the new
        // direction W), so Reconcile takes the small-Corrected branch. The OLD code re-armed _nextStepAt to
        // now + cadence on every Corrected reconcile, freezing predicted stepping for a whole cadence while the
        // server kept stepping — the ~3-tile lag-then-jump. Option B leaves the schedule on its existing cadence
        // for a moving Corrected, so the predictor resumes stepping at the next boundary and tracks the server
        // through the reversal. This test pins that: after the stale confirm the predicted tile keeps advancing
        // WEST on the normal cadence, it does not stall a full cadence past the correction.

        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.W);
        // Already facing W so steps are moves (no turn-then-move delay clouding the schedule under test).
        predictor.SetIntent(true, Direction8.W, TimeSpan.Zero);
        predictor.Tick(TimeSpan.Zero);                                    // (-1,0) at t=0, next step armed t=150
        predictor.Tick(TimeSpan.FromMilliseconds(150));                   // (-2,0) at t=150, next step armed t=300
        Assert.Equal(new TileCoord(-2, 0), predictor.PredictedTile);

        // A stale confirm from BEFORE the flip lands at t=160. It is a small (Chebyshev 2) but OFF-LINE
        // disagreement: the W back-walk from (-2,0) only visits y=0 tiles (-1,0),(0,0),(1,0), so a tile at
        // y=1 can never match — it falls through to the Corrected branch, exactly the live reversal case where
        // a stale old-direction confirm sits off the new predicted line.
        var outcome = predictor.Reconcile(new TileCoord(0, 1), TimeSpan.FromMilliseconds(160));
        Assert.Equal(LocalPlayerPredictor.ReconcileOutcome.Corrected, outcome);
        Assert.Equal(new TileCoord(0, 1), predictor.PredictedTile);       // re-anchored on truth (expected)

        // The schedule was NOT frozen to t=160+150=310: the next step is still due at the ORIGINAL boundary
        // t=300 (the pre-correction cadence). With the OLD freeze it would not step until t=310.
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(299)));
        Assert.Equal(new TileCoord(0, 1), predictor.PredictedTile);
        Assert.True(predictor.Tick(TimeSpan.FromMilliseconds(300)));      // resumes stepping ON the existing cadence
        Assert.Equal(new TileCoord(-1, 1), predictor.PredictedTile);      // stepped W from (0,1)

        // And it keeps marching west at cadence — no multi-tile trail, no full-cadence stall.
        Assert.True(predictor.Tick(TimeSpan.FromMilliseconds(450)));
        Assert.Equal(new TileCoord(-2, 1), predictor.PredictedTile);
    }

    // ---- S72: a stale OLD-direction confirm on the recent path is benign — no backward re-anchor -----

    [Fact]
    public void StaleOldDirectionConfirm_OnRecentPath_IsBenign_NoBackwardReanchor()
    {
        // The S72 "rubberband": drive E for a few tiles, then flip to W. The server's already-in-flight EAST
        // confirmations keep arriving AFTER the flip. The OLD straight-line back-walk only knew the NEW
        // direction (W), so a stale EAST confirm missed it and Reconcile re-anchored _predictedTile BACKWARD
        // onto the lagging confirm and blended the render back — the residual backward pull. With the recent-
        // path ring, that EAST tile is one we actually occupied, so Reconcile recognises it as a benign
        // trailing confirm: Matched, prediction untouched, no backward render move.
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        predictor.Tick(TimeSpan.Zero);                                    // (1,0)
        predictor.Tick(TimeSpan.FromMilliseconds(150));                   // (2,0)
        predictor.Tick(TimeSpan.FromMilliseconds(300));                   // (3,0)
        Assert.Equal(new TileCoord(3, 0), predictor.PredictedTile);

        // Flip to W mid-cadence. The predictor is still at (3,0) (the turn/move hasn't fired yet) but is now
        // predicting the WEST line; the recent path still holds the EAST tiles (3,0),(2,0),(1,0),(0,0).
        predictor.SetIntent(true, Direction8.W, TimeSpan.FromMilliseconds(310));

        var renderBefore = predictor.Sample(TimeSpan.FromMilliseconds(320));

        // A stale EAST in-flight confirm from BEFORE the flip lands: the server is still at (1,0), a tile we
        // already occupied. The OLD code re-anchored backward to (1,0) and blended the render back. Now it is
        // recognised as a benign trailing confirm.
        var outcome = predictor.Reconcile(new TileCoord(1, 0), TimeSpan.FromMilliseconds(320));

        Assert.Equal(LocalPlayerPredictor.ReconcileOutcome.Matched, outcome);
        Assert.Equal(new TileCoord(3, 0), predictor.PredictedTile);       // NOT re-anchored backward
        // The render did not get yanked backward toward (1,0): it stayed where it was showing.
        var renderAfter = predictor.Sample(TimeSpan.FromMilliseconds(320));
        Assert.Equal(renderBefore.X, renderAfter.X, 3);
        Assert.Equal(renderBefore.Y, renderAfter.Y, 3);

        // A genuine OFF-path confirm (a tile we never occupied — e.g. the server held us off-line) still
        // corrects: the recent-path latitude does not swallow a real divergence.
        var offPath = predictor.Reconcile(new TileCoord(3, 5), TimeSpan.FromMilliseconds(330));
        Assert.True(offPath is LocalPlayerPredictor.ReconcileOutcome.Corrected
            or LocalPlayerPredictor.ReconcileOutcome.Snapped);
        Assert.Equal(new TileCoord(3, 5), predictor.PredictedTile);
    }

    [Fact]
    public void OffPathConfirm_WhileMoving_StillCorrects_RecentPathDoesNotSwallowDivergence()
    {
        // Parity guard for the S72 latitude: while MOVING, a confirm that is NOT on the recent path must still
        // correct. The predictor walks E onto (1,0),(2,0),(3,0); the server reports a tile that is genuinely
        // off that line (a divergence / blocked step that held us off-path). It must reconcile, not be absorbed
        // as a benign trailing confirm.
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        predictor.Tick(TimeSpan.Zero);                                    // (1,0)
        predictor.Tick(TimeSpan.FromMilliseconds(150));                   // (2,0)
        predictor.Tick(TimeSpan.FromMilliseconds(300));                   // (3,0)

        var outcome = predictor.Reconcile(new TileCoord(2, 1), TimeSpan.FromMilliseconds(310));

        Assert.Equal(LocalPlayerPredictor.ReconcileOutcome.Corrected, outcome);
        Assert.Equal(new TileCoord(2, 1), predictor.PredictedTile);
    }
}
