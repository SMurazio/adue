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
    // S81 tick grid: the predictor now gates on the server's integer tick grid. These behaviour tests use a
    // fine 10 ms tick so Cadence (150 = 15 ticks) and TurnDelay (80 = 8 ticks) are exact whole ticks and every
    // ms timestamp below lands on a boundary — the timings are identical to the pre-S81 ms model. The
    // against-real-WorldEntity parity tests use the real 50 ms server tick (they drive `tick * 50`).
    private const double TickMs = 10d;

    // Open field: every tile walkable.
    private static bool OpenField(TileCoord _) => true;

    private static LocalPlayerPredictor NewPredictor(TileCoord start, Direction8 facing, Func<TileCoord, bool>? walkable = null)
        => new(start, facing, Cadence, walkable ?? OpenField, TurnDelay, TickMs);

    // ---- Predict: snappy first step + faithful stepping -------------------------------------------

    [Fact]
    public void FirstStepFiresWithinOneTickOnKeydown_NoRoundTrip()
    {
        // S81: the predictor mirrors the server's tick grid, which can only act on a tick boundary. A fresh
        // press OFF the grid therefore takes its first step at the NEXT tick boundary (≤ one tick, ~25 ms avg),
        // not the same instant — the bargain that buys exact tile/seq parity through turns. Already facing E so
        // the first action is a MOVE (no turn-then-move). Press at t=3 ms (off the 10 ms grid): the next
        // boundary is t=10, and nothing fires before it.
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);

        predictor.SetIntent(true, Direction8.E, TimeSpan.FromMilliseconds(3));
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(3)));            // not yet — still off-grid
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(9)));            // still before the boundary
        Assert.Equal(new TileCoord(0, 0), predictor.PredictedTile);

        var stepped = predictor.Tick(TimeSpan.FromMilliseconds(10));          // next tick boundary -> step
        Assert.True(stepped);
        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);
        Assert.Equal(Direction8.E, predictor.Facing);
    }

    [Fact]
    public void FirstStepOnGridBoundary_FiresThatTick_NoRoundTrip()
    {
        // A press exactly ON a tick boundary (t=0) acts that same tick — ceil(0) == 0. (This is the legacy
        // grid-aligned case the old immediate-first-step contract covered; it still fires promptly.)
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
            // realistic steady-state lag. The confirm's step-seq trails the prediction by one too (the predictor
            // is at seq step+1, the server confirms seq step). The history tile at that seq matches, so it must
            // not yank the prediction back.
            var outcome = predictor.Reconcile(serverTile, (uint)step, now);
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
            var outcome = predictor.Reconcile(predictor.PredictedTile, predictor.PredictedStepSeq, now);
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

        predictor.Tick(TimeSpan.Zero);                   // predict (1,0) at seq 1
        predictor.Tick(TimeSpan.FromMilliseconds(150));   // predict (2,0) at seq 2 -- the disputed tile

        Assert.Equal(new TileCoord(2, 0), predictor.PredictedTile);
        Assert.Equal(2u, predictor.PredictedStepSeq);

        // Player releases the key (so the server's confirmation is its FINAL answer, not a catch-up).
        predictor.SetIntent(false, Direction8.E, TimeSpan.FromMilliseconds(160));

        // S77: the server's authoritative truth at the SAME step the prediction reached (seq 2) is (1,0), not
        // the predicted (2,0) — a genuine divergence at that step (the server's step-2 move went elsewhere /
        // was held). The history tile at seq 2 (2,0) mismatches the confirm, so reconcile corrects EXACTLY to
        // the server's truth.
        var outcome = predictor.Reconcile(new TileCoord(1, 0), 2u, TimeSpan.FromMilliseconds(200));

        Assert.Equal(LocalPlayerPredictor.ReconcileOutcome.Corrected, outcome);
        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);   // EXACT reconciliation
        Assert.Equal(2u, predictor.PredictedStepSeq);                 // seq re-anchored on the confirm
    }

    [Fact]
    public void MidMoveSpeedChange_AdoptedImmediately_ContinuesCorrectly()
    {
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        predictor.Tick(TimeSpan.Zero);                   // (1,0) at cadence 150 (15 ticks), next step armed t=150

        // Speed buff: cadence drops to 80 ms (8 ticks, tick-aligned on the 10 ms grid) mid-move. The
        // already-armed next step keeps its t=150 boundary; subsequent steps use the new 80 ms cadence.
        predictor.SetCadence(80);
        Assert.Equal(80d, predictor.CadenceMs);

        // Prior step fired at t=0 and armed the next at +150 (old cadence already on the gate); from there the
        // new 80 ms cadence governs: t=150 -> (2,0), t=230 -> (3,0), t=310 -> (4,0).
        predictor.Tick(TimeSpan.FromMilliseconds(150));   // (2,0)
        predictor.Tick(TimeSpan.FromMilliseconds(230));   // (3,0) at new cadence
        predictor.Tick(TimeSpan.FromMilliseconds(310));   // (4,0)

        Assert.Equal(new TileCoord(4, 0), predictor.PredictedTile);
    }

    [Fact]
    public void LargeDisagreement_SnapsTheRenderInstantly()
    {
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        predictor.Tick(TimeSpan.Zero);                   // (1,0)
        predictor.SetIntent(false, Direction8.E, TimeSpan.FromMilliseconds(10));

        // Server teleported the player far away (knockback / admin tp): a large correction snaps. The confirm's
        // step-seq (1) matches the predicted step's seq but its tile (40,40) is wildly off the history tile
        // (1,0) — a mismatch, and the Chebyshev jump exceeds the snap threshold, so it snaps rather than blends.
        var outcome = predictor.Reconcile(new TileCoord(40, 40), 1u, TimeSpan.FromMilliseconds(20));

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
            t => grid.IsWalkable(t), turnDelayMs, tickMs);

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
            // S77: the predictor's step-seq mirrors the server's StepSequence by the SAME construction as the
            // tile parity — both bump only on an accepted tile move (never a turn/block), so they agree every
            // tick. This is what lets Reconcile match a confirm to the exact predicted step.
            Assert.Equal(entity.StepSequence, predictor.PredictedStepSeq);
        }
    }

    [Fact]
    public void OffGridTurnParity_AgainstRealWorldEntity_Sweep_TileFacingSeqMatchEachTick()
    {
        // S81 — THE PROOF. The pre-S81 predictor scheduled on continuous wall-clock ms while the server acts only
        // on its integer tick grid; on a turn they sampled the rapidly-changing held direction at DIFFERENT
        // instants and diverged a whole tile + a step-seq (the spam-left-right gap). This sweep drives the REAL
        // WorldEntity.TryStep and the tick-grid predictor with intents arriving BETWEEN ticks — rapid E/W
        // alternation — over many press-phase x tick-phase offsets, and asserts tile + facing + step-seq agree at
        // EVERY tick boundary in EVERY phase. It FAILS on the old ms model (every phase diverged in S80) and
        // PASSES on the tick-grid mirror. The predictor polls every 1 ms (frame << tick), so it samples the held
        // direction at the same boundary the server does.
        const int tickRate = 20;                 // 50 ms/tick
        const double tickMs = 1000d / tickRate;  // 50 ms
        const uint stepCooldownTicks = 3;        // 150 ms
        const uint turnDelayTicks = 2;           // 100 ms
        var stepCadenceMs = MovementCadence.EffectiveStepCadenceMs(150, tickRate);
        var turnDelayMs = MovementCadence.EffectiveTurnDelayMs(100, tickRate);

        var mismatches = 0;
        var phasesChecked = 0;

        // press-phase: where in the tick the player presses (ms past a boundary). tick-phase: the flip cadence
        // offset. Sweep 50 press-phases x 8 flip cadences = 400 phases (S80's sweep size).
        for (var pressPhaseMs = 0; pressPhaseMs < 50; pressPhaseMs++)
        {
            for (var flipEveryMs = 13; flipEveryMs < 13 + 8; flipEveryMs++)
            {
                phasesChecked++;
                var grid = new TileGrid(128, 128, []);
                var start = new TileCoord(40, 40);
                var entity = new WorldEntity(1, 1, EntityKind.Player, start, Direction8.E,
                    "Local", System.Guid.NewGuid(), ownerSession: null, isDurable: true);
                var predictor = new LocalPlayerPredictor(start, Direction8.E, stepCadenceMs,
                    t => grid.IsWalkable(t), turnDelayMs, tickMs);

                // The held direction over wall time: press E at pressPhaseMs, then flip E<->W every flipEveryMs.
                Direction8 HeldAt(double ms)
                {
                    if (ms < pressPhaseMs)
                    {
                        return Direction8.E; // not pressed yet (predictor isn't moving before the press anyway)
                    }

                    var flips = (int)((ms - pressPhaseMs) / flipEveryMs);
                    return (flips % 2 == 0) ? Direction8.E : Direction8.W;
                }

                predictor.SetIntent(true, Direction8.E, TimeSpan.FromMilliseconds(pressPhaseMs));
                var lastHeld = Direction8.E;
                var lastServerHeld = Direction8.E;

                // Drive ~24 ticks of wall time at 1 ms granularity. Feed every held-direction change into the
                // predictor as it happens (off-grid), tick the predictor each ms, and step the REAL entity once
                // per tick boundary sampling the held direction AT that boundary (what the server does).
                var totalMs = (int)(24 * tickMs);
                uint nextTick = 0;
                for (var ms = pressPhaseMs; ms <= totalMs; ms++)
                {
                    var held = HeldAt(ms);
                    if (held != lastHeld)
                    {
                        predictor.SetIntent(true, held, TimeSpan.FromMilliseconds(ms));
                        lastHeld = held;
                    }

                    predictor.Tick(TimeSpan.FromMilliseconds(ms));

                    // Step the real entity at each tick boundary that has now elapsed, sampling the held
                    // direction at the boundary instant (the server's MoveIntentDirection at that tick). The
                    // server has NO held intent before the press, so it only acts at ticks at/after the press —
                    // exactly when the predictor's first quantised action is armed.
                    while (nextTick * tickMs <= ms)
                    {
                        if (nextTick * tickMs >= pressPhaseMs)
                        {
                            var serverHeld = HeldAt(nextTick * tickMs);
                            entity.TryStep(serverHeld, nextTick, stepCooldownTicks, turnDelayTicks, grid);
                            lastServerHeld = serverHeld;

                            // Parity at this tick boundary: tile, facing, AND accepted-step count.
                            if (entity.Tile != predictor.PredictedTile
                                || entity.Facing != predictor.Facing
                                || entity.StepSequence != predictor.PredictedStepSeq)
                            {
                                mismatches++;
                            }
                        }

                        nextTick++;
                    }
                }

                _ = lastServerHeld;
            }
        }

        Assert.Equal(400, phasesChecked);
        // The structural accumulation must be ZERO — exact tile/facing/seq parity through off-grid turn spam.
        Assert.Equal(0, mismatches);
    }

    [Fact]
    public void SpamLeftRight_NoDrift_PredictedTileTracksServer()
    {
        // The human's repro: stand still, spam left-right rapidly off the tick grid, then settle and run. The
        // predicted tile must NOT accumulate divergence from the server's authoritative tile. Drive the REAL
        // WorldEntity and the predictor with E/W flips every 17 ms (off the 50 ms grid) for ~2 s, then hold E,
        // and assert the predicted tile and step-seq equal the server's at the end (zero drift).
        const int tickRate = 20;
        const double tickMs = 1000d / tickRate;
        const uint stepCooldownTicks = 3;
        const uint turnDelayTicks = 2;
        var stepCadenceMs = MovementCadence.EffectiveStepCadenceMs(150, tickRate);
        var turnDelayMs = MovementCadence.EffectiveTurnDelayMs(100, tickRate);

        var grid = new TileGrid(256, 256, []);
        var start = new TileCoord(100, 100);
        var entity = new WorldEntity(1, 1, EntityKind.Player, start, Direction8.E,
            "Local", System.Guid.NewGuid(), ownerSession: null, isDurable: true);
        var predictor = new LocalPlayerPredictor(start, Direction8.E, stepCadenceMs,
            t => grid.IsWalkable(t), turnDelayMs, tickMs);

        const int spamUntilMs = 2000;
        const int totalMs = 3000;
        const int flipEveryMs = 17;

        Direction8 HeldAt(double ms)
        {
            if (ms >= spamUntilMs)
            {
                return Direction8.E; // settle on E and run
            }

            var flips = (int)(ms / flipEveryMs);
            return (flips % 2 == 0) ? Direction8.E : Direction8.W;
        }

        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        var lastHeld = Direction8.E;
        uint nextTick = 0;
        for (var ms = 0; ms <= totalMs; ms++)
        {
            var held = HeldAt(ms);
            if (held != lastHeld)
            {
                predictor.SetIntent(true, held, TimeSpan.FromMilliseconds(ms));
                lastHeld = held;
            }

            predictor.Tick(TimeSpan.FromMilliseconds(ms));

            while (nextTick * tickMs <= ms)
            {
                var serverHeld = HeldAt(nextTick * tickMs);
                entity.TryStep(serverHeld, nextTick, stepCooldownTicks, turnDelayTicks, grid);
                Assert.Equal(entity.Tile, predictor.PredictedTile);
                Assert.Equal(entity.Facing, predictor.Facing);
                Assert.Equal(entity.StepSequence, predictor.PredictedStepSeq);
                nextTick++;
            }
        }

        // After settling and running E, the avatar has actually travelled (no permanent stall), and stayed in
        // exact lockstep with the server the whole way.
        Assert.Equal(entity.Tile, predictor.PredictedTile);
        Assert.True(predictor.PredictedTile.X > start.X, "predicted avatar should have advanced east after settling");
    }

    [Fact]
    public void Calibration_SmoothsJitter_NoBackwardTileNoSpuriousStep()
    {
        // S81 calibration risk (S80's #1 flagged real-client risk): the NTP-free wall clock + jittery snapshot
        // arrival must not jump the tick the gate runs on. Press E and run straight while feeding calibration
        // snapshots whose advertised serverTick JITTERS around the truth by +/-1. The clamped, monotonic
        // calibration must keep the predicted tile advancing monotonically east — never rewinding, never
        // double-stepping past the straight-line cadence.
        const double tickMs = 50d;
        const double cadence = 150d;            // 3 ticks
        var predictor = new LocalPlayerPredictor(new TileCoord(0, 0), Direction8.E, cadence, OpenField, TurnDelay, tickMs);

        // Seed the frame at server tick 1000 @ wall 0 (a realistic large server tick).
        predictor.CalibrateToServerTick(1000, TimeSpan.Zero);
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);

        var lastX = predictor.PredictedTile.X;
        var rng = new System.Random(12345);
        for (var ms = 0; ms <= 2000; ms += 5)
        {
            // Every ~50 ms a snapshot lands with a jittered tick (+/-1 around the true tick = 1000 + ms/50).
            if (ms % 50 == 0)
            {
                var trueTick = 1000 + ms / 50;
                var jitter = rng.Next(-1, 2); // -1, 0, or +1
                predictor.CalibrateToServerTick(trueTick + jitter, TimeSpan.FromMilliseconds(ms));
            }

            predictor.Tick(TimeSpan.FromMilliseconds(ms));

            // Monotonic non-decreasing X (never rewinds), and never jumps more than one tile per frame.
            Assert.True(predictor.PredictedTile.X >= lastX, $"predicted tile rewound at t={ms}");
            Assert.True(predictor.PredictedTile.X - lastX <= 1, $"predicted tile jumped >1 tile at t={ms}");
            lastX = predictor.PredictedTile.X;
        }

        // Over 2 s at a 150 ms cadence the avatar should have walked ~13 tiles east (jitter shifts the exact
        // count by at most a tile or two), proving steps actually fired and weren't stalled.
        Assert.InRange(predictor.PredictedTile.X, 11, 14);
        Assert.Equal(0, predictor.PredictedTile.Y);
    }

    [Fact]
    public void SpamThenRest_AgainstRealWorldEntity_LeadStaysBounded_AndReconvergesAtRest()
    {
        // S82 — THE STUCK-STATE LATCH. The full real-client loop, not the pure-stepping parity sweep: the
        // predictor Ticks every frame (~16 ms) but Reconciles only per snapshot (~50 ms). During rapid down/left
        // (S/W) spam the predictor accepts several un-reconciled steps between reconciles and the per-turn
        // misprediction COMPOUNDS — pre-fix the lead (PredictedStepSeq - server StepSequence) ran away to ~6 and,
        // because every while-moving confirm benignly MATCHES the step it confirms, the idle/stop clause never
        // fired while the key stayed held, so the gap NEVER recovered (the latch the human reproduced by spamming
        // down/left). This test FAILS before the lead bound (the lead assertion trips at ~6) and PASSES after
        // (capped at MaxPredictedLeadSteps), and then — once the key is released and steady stopped snapshots
        // arrive — asserts the predicted tile re-converges EXACTLY to the server tile within a few snapshots.
        const int tickRate = 20;
        const double tickMs = 1000d / tickRate;   // 50 ms server tick
        const uint stepCooldownTicks = 3;         // 150 ms
        const uint turnDelayTicks = 2;            // 100 ms
        var stepCadenceMs = MovementCadence.EffectiveStepCadenceMs(150, tickRate);
        var turnDelayMs = MovementCadence.EffectiveTurnDelayMs(100, tickRate);
        const uint leadBound = 2; // MaxPredictedLeadSteps

        var grid = new TileGrid(256, 256, []);
        var start = new TileCoord(100, 100);
        var entity = new WorldEntity(1, 1, EntityKind.Player, start, Direction8.S,
            "Local", System.Guid.NewGuid(), ownerSession: null, isDurable: true);
        var predictor = new LocalPlayerPredictor(start, Direction8.S, stepCadenceMs,
            t => grid.IsWalkable(t), turnDelayMs, tickMs);

        // Worst-case timing from the S82 sweep: a ~30 fps frame (33 ms) is SLOWER than the 50 ms tick is fast,
        // so several tick boundaries elapse per Tick call between the ~50 ms reconciles — that is exactly what
        // lets the per-turn misprediction compound. Pre-fix this config drove the lead to ~13.
        const double frameMs = 33d;     // ~30 fps client poll
        const double netDelayMs = 10d;  // one-way delay for intents up and snapshots down
        const int spamUntilMs = 3000;   // rapid S/W flips through here
        const int releaseAtMs = 3400;   // settle S a moment, then release the key
        const int totalMs = 6000;       // run on with steady stopped snapshots so it can re-converge
        const int flipEveryMs = 30;     // off the 50 ms grid

        // What the client's held intent is at wall time ms: spam S<->W, then settle S, then release.
        (bool moving, Direction8 dir) ClientIntentAt(double ms)
        {
            if (ms >= releaseAtMs) return (false, Direction8.S);
            if (ms >= spamUntilMs) return (true, Direction8.S);
            var flips = (int)(ms / flipEveryMs);
            return (true, (flips % 2 == 0) ? Direction8.S : Direction8.W);
        }

        // Intents in flight to the server (delivered after netDelayMs); snapshots in flight to the client.
        var pendingIntents = new List<(double arriveMs, bool moving, Direction8 dir)>();
        var pendingSnaps = new List<(double arriveMs, long serverTick, TileCoord tile, Direction8 facing, uint stepSeq)>();
        var serverMoving = true;
        var serverHeld = Direction8.S;

        // Press at t=0 (both the predictor and the wire).
        predictor.SetIntent(true, Direction8.S, TimeSpan.Zero);
        pendingIntents.Add((netDelayMs, true, Direction8.S));
        var clientMoving = true;
        var clientHeld = Direction8.S;

        uint nextServerTick = 0;
        double nextFrame = 0;
        var maxLeadDuringSpam = 0;

        for (double ms = 0; ms <= totalMs; ms += 1)
        {
            // ---- client samples its input each ms; sends on change (keydown/keyup/direction change) ----
            var (wantMoving, wantDir) = ClientIntentAt(ms);
            if (wantMoving != clientMoving || (wantMoving && wantDir != clientHeld))
            {
                predictor.SetIntent(wantMoving, wantDir, TimeSpan.FromMilliseconds(ms));
                pendingIntents.Add((ms + netDelayMs, wantMoving, wantDir));
                clientMoving = wantMoving;
                clientHeld = wantDir;
            }

            // ---- intents arrive at the server ----
            for (var i = pendingIntents.Count - 1; i >= 0; i--)
            {
                if (pendingIntents[i].arriveMs <= ms)
                {
                    serverMoving = pendingIntents[i].moving;
                    if (pendingIntents[i].moving)
                    {
                        serverHeld = pendingIntents[i].dir;
                    }

                    pendingIntents.RemoveAt(i);
                }
            }

            // ---- server steps on its tick grid + emits a snapshot each tick ----
            while (nextServerTick * tickMs <= ms)
            {
                if (serverMoving)
                {
                    entity.TryStep(serverHeld, nextServerTick, stepCooldownTicks, turnDelayTicks, grid);
                }

                pendingSnaps.Add((nextServerTick * tickMs + netDelayMs, nextServerTick + 1000, entity.Tile, entity.Facing, entity.StepSequence));
                nextServerTick++;
            }

            // ---- client frame: PollEvents (calibrate+reconcile each delivered snapshot) THEN Tick ----
            if (ms >= nextFrame)
            {
                for (var i = 0; i < pendingSnaps.Count; i++)
                {
                    if (pendingSnaps[i].arriveMs <= ms)
                    {
                        var snap = pendingSnaps[i];
                        predictor.CalibrateToServerTick(snap.serverTick, TimeSpan.FromMilliseconds(ms));
                        predictor.Reconcile(snap.tile, snap.stepSeq, TimeSpan.FromMilliseconds(ms));
                        predictor.ConfirmFacing(snap.facing);
                        pendingSnaps.RemoveAt(i);
                        i--;
                    }
                }

                predictor.Tick(TimeSpan.FromMilliseconds(ms));
                nextFrame += frameMs;

                // THE BUG SIGNAL: during the spam the prediction's lead over the server-confirmed step-seq must
                // stay bounded. Pre-fix it climbed to ~6 here (and never recovered while the key was held).
                if (ms < spamUntilMs)
                {
                    var lead = (int)((long)predictor.PredictedStepSeq - entity.StepSequence);
                    if (lead > maxLeadDuringSpam)
                    {
                        maxLeadDuringSpam = lead;
                    }
                }
            }
        }

        // (1) The runaway is capped: the prediction never led the server-confirmed step-seq by more than the
        // bound during the spam. This is the assertion that FAILS pre-fix (the lead reached ~6).
        Assert.True(maxLeadDuringSpam <= (int)leadBound,
            $"prediction lead ran away during spam: max {maxLeadDuringSpam} > bound {leadBound}");

        // (2) After the key is released and steady stopped snapshots have arrived, the prediction has fully
        // re-converged to the server's authoritative tile and step-seq — no permanent latch.
        Assert.False(predictor.IsMoving);
        Assert.Equal(entity.Tile, predictor.PredictedTile);
        Assert.Equal(entity.StepSequence, predictor.PredictedStepSeq);
    }

    [Fact]
    public void TurnPathParity_AgainstRealWorldEntity_DiagonalCornerCutRejectedIdentically()
    {
        // S75 corner-cut parity: drive the REAL server WorldEntity.TryStep and the predictor on the SAME
        // held-intent timeline, but now over a TileGrid with a blocked corner. The entity faces NE and holds it,
        // trying to slip diagonally from (10,10) to (11,9) — but the side tile (11,10) is blocked, so BOTH the
        // server and the predictor must reject the diagonal and hold every tick. A divergence here is exactly
        // the "client thinks it can cut the corner, server says no" desync this rule must prevent.
        const int tickRate = 20;                 // 50 ms/tick
        const double tickMs = 1000d / tickRate;  // 50 ms
        const uint stepCooldownTicks = 3;        // 150 ms
        const uint turnDelayTicks = 2;           // 100 ms
        var stepCadenceMs = MovementCadence.EffectiveStepCadenceMs(150, tickRate);     // 150 ms
        var turnDelayMs = MovementCadence.EffectiveTurnDelayMs(100, tickRate);          // 100 ms

        // (11,10) is the side tile E of the start; (11,9) (the NE destination) is OPEN, so a target-only check
        // would WRONGLY let the diagonal through. The corner rule must block it on both sides.
        var grid = new TileGrid(64, 64, [new TileCoord(11, 10)]);
        var entity = new WorldEntity(1, 1, EntityKind.Player, new TileCoord(10, 10), Direction8.NE,
            "Local", System.Guid.NewGuid(), ownerSession: null, isDurable: true);
        var predictor = new LocalPlayerPredictor(new TileCoord(10, 10), Direction8.NE, stepCadenceMs,
            t => grid.IsWalkable(t), turnDelayMs, tickMs);

        // Held NE the whole time (already facing NE, so each eligible tick is a MOVE attempt into the corner).
        var held = Direction8.NE;
        predictor.SetIntent(true, held, TimeSpan.Zero);

        for (uint tick = 0; tick <= 30; tick++)
        {
            entity.TryStep(held, tick, stepCooldownTicks, turnDelayTicks, grid);
            predictor.Tick(TimeSpan.FromMilliseconds(tick * tickMs));

            // Both must HOLD at the start tile (the diagonal is corner-cut-rejected) — tile AND facing parity
            // every tick, never slipping to (11,9).
            Assert.Equal(new TileCoord(10, 10), entity.Tile);
            Assert.Equal(entity.Tile, predictor.PredictedTile);
            Assert.Equal(entity.Facing, predictor.Facing);
        }
    }

    [Fact]
    public void StartStopBoundary_ServerSettledBehind_ConvergesDownToStopTile()
    {
        // The REAL stop-boundary over-prediction: predict three steps E to (3,0) (seq 3), then STOP. The
        // server only ever took TWO steps — the 3rd step's intent arrived AFTER the player released — so its
        // authoritative truth settles at (2,0) at seq 2 and it will NEVER emit a seq-3 confirm (it is stopped).
        // The benign-match path would see MatchesHistory(2,(2,0)) -> Matched and leave us stranded at (3,0)
        // forever. The idle/stop clause fires (!_moving && serverStepSeq 2 < predictedStepSeq 3) and converges
        // DOWN to the server's stop tile (2,0) with a small (non-snapping) blend.
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        predictor.Tick(TimeSpan.Zero);                   // (1,0) seq 1
        predictor.Tick(TimeSpan.FromMilliseconds(150));   // (2,0) seq 2
        predictor.Tick(TimeSpan.FromMilliseconds(300));   // (3,0) seq 3
        Assert.Equal(new TileCoord(3, 0), predictor.PredictedTile);
        Assert.Equal(3u, predictor.PredictedStepSeq);

        predictor.SetIntent(false, Direction8.E, TimeSpan.FromMilliseconds(310));

        // Server settled at (2,0) at seq 2 — one step short of the prediction. (2,0)@seq2 is exactly what
        // history holds, so the OLD code matched and left us at (3,0); the idle clause converges down to it.
        var outcome = predictor.Reconcile(new TileCoord(2, 0), 2u, TimeSpan.FromMilliseconds(360));

        Assert.Equal(LocalPlayerPredictor.ReconcileOutcome.Corrected, outcome);
        Assert.Equal(new TileCoord(2, 0), predictor.PredictedTile);
        Assert.Equal(2u, predictor.PredictedStepSeq);
    }

    [Fact]
    public void StartStopBoundary_CleanStop_ServerTookAllSteps_MatchedNoRenderMove()
    {
        // The clean stop: predict three steps E to (3,0) (seq 3), then STOP, and the server DID take all three
        // — it confirms (3,0)@seq3. The idle clause must NOT fire (serverStepSeq 3 is not < predictedStepSeq 3):
        // the prediction already agrees with the truth, so this is a benign Matched with no render move.
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        predictor.Tick(TimeSpan.Zero);                   // (1,0) seq 1
        predictor.Tick(TimeSpan.FromMilliseconds(150));   // (2,0) seq 2
        predictor.Tick(TimeSpan.FromMilliseconds(300));   // (3,0) seq 3
        Assert.Equal(new TileCoord(3, 0), predictor.PredictedTile);

        predictor.SetIntent(false, Direction8.E, TimeSpan.FromMilliseconds(310));
        var renderBefore = predictor.Sample(TimeSpan.FromMilliseconds(360));

        var outcome = predictor.Reconcile(new TileCoord(3, 0), 3u, TimeSpan.FromMilliseconds(360));

        Assert.Equal(LocalPlayerPredictor.ReconcileOutcome.Matched, outcome);
        Assert.Equal(new TileCoord(3, 0), predictor.PredictedTile);
        Assert.Equal(3u, predictor.PredictedStepSeq);
        var renderAfter = predictor.Sample(TimeSpan.FromMilliseconds(360));
        Assert.Equal(renderBefore.X, renderAfter.X, 6);
        Assert.Equal(renderBefore.Y, renderAfter.Y, 6);
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

        // A confirm at the step the prediction reached (seq 2) reports an OFF-LINE tile (0,1) — the history
        // tile at seq 2 is (-2,0), so this is a genuine mismatch that re-anchors (the live reversal case where
        // the server's authoritative step-2 tile diverged from the prediction). It falls through to Corrected.
        var outcome = predictor.Reconcile(new TileCoord(0, 1), 2u, TimeSpan.FromMilliseconds(160));
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

    // ---- S77: a benign trailing/old-direction confirm MATCHES its step — no backward re-anchor ---------

    [Fact]
    public void BenignTrailingConfirm_MatchedByStepSeq_NoRenderMove()
    {
        // The S72 "rubberband", now resolved by step-seq (replaces StaleOldDirectionConfirm_OnRecentPath). Drive
        // E for three tiles (seq 3, (3,0)). A stale in-flight EAST confirm from seq 1 lands — the server simply
        // hasn't processed the steps that carried us forward. The history tile at seq 1 IS (1,0), so the confirm
        // matches the exact step we predicted: Matched, prediction untouched, no backward render move.
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        predictor.Tick(TimeSpan.Zero);                                    // (1,0) seq 1
        predictor.Tick(TimeSpan.FromMilliseconds(150));                   // (2,0) seq 2
        predictor.Tick(TimeSpan.FromMilliseconds(300));                   // (3,0) seq 3
        Assert.Equal(new TileCoord(3, 0), predictor.PredictedTile);
        Assert.Equal(3u, predictor.PredictedStepSeq);

        var renderBefore = predictor.Sample(TimeSpan.FromMilliseconds(320));

        var outcome = predictor.Reconcile(new TileCoord(1, 0), 1u, TimeSpan.FromMilliseconds(320));

        Assert.Equal(LocalPlayerPredictor.ReconcileOutcome.Matched, outcome);
        Assert.Equal(new TileCoord(3, 0), predictor.PredictedTile);       // NOT re-anchored backward
        Assert.Equal(3u, predictor.PredictedStepSeq);                     // seq untouched
        // The render did not get yanked backward toward (1,0): it stayed exactly where it was showing.
        var renderAfter = predictor.Sample(TimeSpan.FromMilliseconds(320));
        Assert.Equal(renderBefore.X, renderAfter.X, 6);
        Assert.Equal(renderBefore.Y, renderAfter.Y, 6);
    }

    [Fact]
    public void StaleConfirm_OlderThanHistory_IsBenign_NoRenderMove()
    {
        // A confirm for a step OLDER than anything we still remember (we have moved well past it) is benign by
        // construction — there is nothing to compare it against and it can only be a lagging trailing confirm.
        // Matched, prediction + render untouched.
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        for (var step = 0; step < 5; step++)
        {
            predictor.Tick(TimeSpan.FromMilliseconds(step * 150));
        }

        Assert.Equal(new TileCoord(5, 0), predictor.PredictedTile);
        var renderBefore = predictor.Sample(TimeSpan.FromMilliseconds(620));

        // seq 0 — the start anchor, never recorded in history (history holds seq 1..5). It is older than the
        // oldest retained entry, so it is treated as a benign already-passed match.
        var outcome = predictor.Reconcile(new TileCoord(0, 0), 0u, TimeSpan.FromMilliseconds(620));

        Assert.Equal(LocalPlayerPredictor.ReconcileOutcome.Matched, outcome);
        Assert.Equal(new TileCoord(5, 0), predictor.PredictedTile);
        var renderAfter = predictor.Sample(TimeSpan.FromMilliseconds(620));
        Assert.Equal(renderBefore.X, renderAfter.X, 6);
        Assert.Equal(renderBefore.Y, renderAfter.Y, 6);
    }

    // ---- S77: a genuine misprediction re-anchors and REPLAYS the in-flight steps from the truth -------

    [Fact]
    public void GenuineMisprediction_ReplaysInFlightSteps_FromCorrectedAnchor()
    {
        // The server diverges at an EARLIER step than the prediction's head, so the in-flight steps after it
        // must be replayed from the corrected anchor. Predict E to (3,0) seq 3. The server reports that at seq 2
        // the tile was (2,1) (diverged one tile north of the predicted (2,0)). Reconcile re-anchors seq 2 to
        // (2,1) and REPLAYS the recorded seq-3 step (still E) from there, recomputing the present tile as (3,1)
        // — not a backward yank to (2,1), and not the stale (3,0).
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E);
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);
        predictor.Tick(TimeSpan.Zero);                                    // (1,0) seq 1
        predictor.Tick(TimeSpan.FromMilliseconds(150));                   // (2,0) seq 2
        predictor.Tick(TimeSpan.FromMilliseconds(300));                   // (3,0) seq 3
        Assert.Equal(new TileCoord(3, 0), predictor.PredictedTile);
        Assert.Equal(3u, predictor.PredictedStepSeq);

        var outcome = predictor.Reconcile(new TileCoord(2, 1), 2u, TimeSpan.FromMilliseconds(310));

        Assert.Equal(LocalPlayerPredictor.ReconcileOutcome.Corrected, outcome);
        // Re-anchored to the seq-2 truth (2,1) and replayed the seq-3 E step forward => present tile (3,1).
        Assert.Equal(new TileCoord(3, 1), predictor.PredictedTile);
        Assert.Equal(3u, predictor.PredictedStepSeq);                     // seq head preserved across replay
    }

    [Fact]
    public void BlockedHoldThenTurnAlongWall_NoBackwardRenderMove()
    {
        // A wall at x>=2. Hold E into it: step to (1,0) (seq 1), then the (2,0) step is blocked and the avatar
        // holds at the wall — the seq does NOT advance on a blocked step (mirrors the server). The server
        // confirms the held tile (1,0) at the unchanged seq 1 => Matched. Then turn S and slide along the wall.
        // Assert: the held confirm is benign (no render move), and across the turn-then-slide the render only
        // advances DOWN the wall, never backward, while the seq bumps only on the accepted S moves.
        bool walkable(TileCoord t) => t.X < 2;
        var predictor = NewPredictor(new TileCoord(0, 0), Direction8.E, walkable);
        predictor.SetIntent(true, Direction8.E, TimeSpan.Zero);

        predictor.Tick(TimeSpan.Zero);                                    // (1,0) seq 1
        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);
        Assert.Equal(1u, predictor.PredictedStepSeq);

        // Hold into the wall: target (2,0) blocked, no step, seq stays 1.
        Assert.False(predictor.Tick(TimeSpan.FromMilliseconds(150)));
        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);
        Assert.Equal(1u, predictor.PredictedStepSeq);

        // Server confirms the held tile at the unchanged seq 1 — benign match, no render move.
        var renderBeforeConfirm = predictor.Sample(TimeSpan.FromMilliseconds(155));
        var held = predictor.Reconcile(new TileCoord(1, 0), 1u, TimeSpan.FromMilliseconds(155));
        Assert.Equal(LocalPlayerPredictor.ReconcileOutcome.Matched, held);
        Assert.Equal(new TileCoord(1, 0), predictor.PredictedTile);
        var renderAfterConfirm = predictor.Sample(TimeSpan.FromMilliseconds(155));
        Assert.Equal(renderBeforeConfirm.X, renderAfterConfirm.X, 6);
        Assert.Equal(renderBeforeConfirm.Y, renderAfterConfirm.Y, 6);

        // Turn S and slide along the wall, ticking frame-by-frame. The render Y must be monotonic non-decreasing
        // (only ever slides DOWN the wall, never snaps backward), and the X never slides off the wall column.
        predictor.SetIntent(true, Direction8.S, TimeSpan.FromMilliseconds(160));
        var lastY = renderAfterConfirm.Y;
        for (var ms = 160; ms <= 600; ms += 10)
        {
            predictor.Tick(TimeSpan.FromMilliseconds(ms));
            var r = predictor.Sample(TimeSpan.FromMilliseconds(ms));
            Assert.True(r.Y >= lastY - 1e-6, $"render moved backward at t={ms}: {r.Y} < {lastY}");
            Assert.Equal(1d, r.X, 6);                                     // stays on the wall column x=1
            lastY = r.Y;
        }

        // Every accepted move was a single S tile, so seq == 1 (the E step) + the S moves taken; the tile is
        // straight down the wall column and the seq mirrors exactly that many accepted moves.
        Assert.Equal(1, predictor.PredictedTile.X);
        Assert.Equal(1u + (uint)predictor.PredictedTile.Y, predictor.PredictedStepSeq);

        // A final confirm of the present tile at the present seq is a clean match.
        var slid = predictor.Reconcile(predictor.PredictedTile, predictor.PredictedStepSeq, TimeSpan.FromMilliseconds(610));
        Assert.Equal(LocalPlayerPredictor.ReconcileOutcome.Matched, slid);
    }
}
