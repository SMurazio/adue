using System;
using System.Collections.Generic;
using Mmo.Client.Core.Continuous;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Mmo.Client.Core.Tests;

// CONTINUOUS MIGRATION — LOCAL-PLAYER PREDICTION REGRESSION (the measured "jump-back" rubberband).
//
// THE LIVE SYMPTOM (control-channel measured): the local player's prediction DIVERGES from the server's
// authoritative position by 1.5-1.7 tiles on plain movement across an empty map — a SOFT correction
// (motion.snapCount=0, so under the 4u snap threshold). Small (~0.7u) at clean steady state, SPIKING to
// 1.5-1.7u on frame hitches / uncapped (~145fps) movement.
//
// WHY THE EXISTING HARNESS MISSED IT (the review-independence lesson, verbatim from project.md): the Phase-4
// reconcile harness models the anti-speedhack dt budget as a CONSTANT FACTOR (Invariant5: ServerDtBudgetFactor
// = 0.6 — "server integrates 60% of each input"). That is the WRONG model: the real budget is a per-tick CREDIT
// (+1/TickRate s) CAPPED at the 0.4s burst allowance, DEBITED per input on the receive path. A constant factor
// can never reproduce the cap-discards-credit mechanic that bites under HONEST high-fps play. This test models
// the REAL budget arithmetic (a verbatim copy of ClientSession.CreditMoveDtBudget / ConsumeMoveDtBudget and the
// real PollEvents-then-Tick ordering) and drives the REAL ContinuousPredictor through it.
//
// THE MEASURED ROOT CAUSE (see the asserts): the budget is credited ONCE PER SERVER TICK by the FIXED tick
// interval (1/TickRate), NOT by the real wall-clock time that actually elapsed since the previous credit. Honest
// STEADY play (any fps) balances — summed sent-dt ≈ real elapsed ≈ credited, and a single input (≤ the 0.25s
// sanity clamp) is always under the 0.4s burst, so it is never clamped on its own. The bug needs a SERVER-TICK
// STALL (a GC gen2 pause / the live 571ms entities startup spike): the client keeps sending real-time dt the
// whole stall, but the fixed-tick credit refunds only 1/TickRate for a window of real time that was much longer,
// so the budget is left SHORT and the cap (0.4s) prevents it ever catching back up. The honest inputs that
// queued during the stall then get CLAMPED by ConsumeMoveDtBudget. The clamp is INVISIBLE to the predictor
// (which integrated the full dt), so the server integrates LESS than the client predicted -> the reconcile
// replays onto a lagging authoritative base -> the bounded SOFT divergence (~0.4s × speed ≈ 1.6u) the user sees.
// THE FIX: credit by REAL elapsed wall-clock time, so a stall window is fully refunded (anti-speedhack cap
// unchanged → integrated ≤ real elapsed + burst still holds).
public sealed class ContinuousDtBudgetRegressionTests
{
    private readonly ITestOutputHelper _out;

    public ContinuousDtBudgetRegressionTests(ITestOutputHelper output) => _out = output;

    private const int TickRate = 20;
    private const double ServerTickSeconds = 1.0d / TickRate; // 0.05s
    private const double Speed = 1000d / 250d;                // 4.0 u/s (250ms cooldown, multiplier 1.0)
    private const double Radius = CollisionDefaults.BodyRadius;

    // The two anti-speedhack constants, verbatim from GameServer.
    private const double MaxInputDtSeconds = ContinuousMovement.MaxInputDtSeconds; // 0.25
    private const double BurstAllowanceSeconds = 0.4d;

    // ---- the measurement: reproduce the divergence, pin the mechanism --------------------------------------

    [Theory]
    [InlineData(60d, false, "60fps steady")]
    [InlineData(145d, false, "145fps steady (uncapped)")]
    [InlineData(145d, true, "145fps + 143ms frame hitches")]
    [InlineData(60d, true, "60fps + 143ms frame hitches")]
    public void Measure_DivergenceUnderHonestPlay(double fps, bool injectHitches, string label)
    {
        var sim = new BudgetSim(fps, injectHitches);
        sim.RunHeldEast(seconds: 6d);

        _out.WriteLine(
            $"[{label}] maxPredictedVsAuthoritative={sim.MaxPredictedVsAuthoritativeUnits:F4}u " +
            $"clampedDtTotal={sim.TotalClampedDtSeconds:F4}s ({sim.TotalClampedDtUnits:F4}u of motion the server " +
            $"DROPPED) finalGap={sim.FinalPredictedVsAuthoritativeUnits:F4}u snapCount={sim.SnapCount} " +
            $"lowestBudget={sim.LowestBudgetSeconds:F4}s");
    }

    // THE DECISIVE MEASUREMENT — does the BUGGY fixed-tick-credit budget clamp HONEST play, and by how much?
    // This is the number that pins (or refutes) the dt-budget hypothesis: TotalClampedDtUnits is the motion the
    // server REFUSED to integrate even though the client's summed dt never exceeded real elapsed time (honest by
    // construction). MaxDivergence is the resulting predicted-vs-authoritative gap the reconcile then has to yank
    // back (the visible rubberband). Reported across the realistic streams; the buggy-vs-fix DELTA is the proof.
    [Theory]
    [InlineData(60d, false, "60fps steady")]
    [InlineData(145d, false, "145fps steady (uncapped)")]
    [InlineData(145d, true, "145fps + 143ms hitches")]
    [InlineData(60d, true, "60fps + 143ms hitches")]
    public void Repro_BuggyFixedTickCredit_HonestClampMeasured(double fps, bool injectHitches, string label)
    {
        var sim = new BudgetSim(fps, injectHitches) { UseRealElapsedCredit = false };
        sim.RunHeldEast(seconds: 6d);

        _out.WriteLine(
            $"[BUGGY {label}] maxDivergence={sim.MaxPredictedVsAuthoritativeUnits:F4}u " +
            $"HONEST-MOTION-DROPPED={sim.TotalClampedDtUnits:F4}u finalGap={sim.FinalPredictedVsAuthoritativeUnits:F4}u " +
            $"snapCount={sim.SnapCount} lowestBudget={sim.LowestBudgetSeconds:F4}s");

        // A SOFT correction throughout (matches the live motion.snapCount=0) — never a hard teleport snap.
        Assert.Equal(0, sim.SnapCount);
    }

    // THE REGRESSION REPRO — SERVER-TICK STALL (the live trigger: GC gen2 pauses + the 571ms entities spike).
    // With the BUGGY fixed-tick credit, a server stall refunds only ServerTickSeconds for a window of real time
    // that was much longer, so the budget is left short and clamps the honest inputs that queued during the stall.
    // This drives the predicted-vs-authoritative divergence into the live-measured 1.5u+ band — a SOFT rubberband
    // (snapCount=0), exactly the symptom. This is the failing-before-fix evidence with numbers.
    [Fact]
    public void Repro_ServerStall_BuggyCredit_Diverges_IntoLiveSymptomBand()
    {
        // 145fps client, 0.3s server stalls (a GC gen2 pause class) injected a few times across the run.
        var sim = new BudgetSim(fps: 145d, injectHitches: false)
        {
            UseRealElapsedCredit = false,
            ServerStallSeconds = 0.3d,
        };
        sim.RunHeldEast(seconds: 6d);

        _out.WriteLine(
            $"[BUGGY server-stall] maxDivergence={sim.MaxPredictedVsAuthoritativeUnits:F4}u " +
            $"maxCorrection={sim.MaxReconcileCorrectionUnits:F4}u maxRenderVsPredicted={sim.MaxRenderVsPredictedUnits:F4}u " +
            $"HONEST-MOTION-DROPPED={sim.TotalClampedDtUnits:F4}u finalGap={sim.FinalPredictedVsAuthoritativeUnits:F4}u " +
            $"snapCount={sim.SnapCount} lowestBudget={sim.LowestBudgetSeconds:F4}s");

        // The budget DROPPED real, honest motion (the server integrated less than the client predicted)...
        Assert.True(sim.TotalClampedDtUnits > 0.5d,
            $"expected the buggy budget to drop honest motion; dropped only {sim.TotalClampedDtUnits:F4}u");
        // ...which the reconcile YANKS back in one SOFT correction — the visible rubberband. The snap-back (max
        // reconcile correction) reproduces the live ~1.5u jump (1.22u for this 0.3s stall; a longer live stall — the
        // 571ms entities spike — yanks further), snapCount=0. NOTE maxDivergence (the POST-reconcile predicted-vs-
        // server gap) is tiny (~0.03u) precisely because the reconcile collapses it each tick — the CORRECTION
        // magnitude is the rubberband the user sees, not the residual gap (the metric the first cut wrongly asserted).
        // CAVEAT (independent review): the exact 1.22u is HARNESS-RELATIVE — model-specific to this synthetic
        // input/credit interleave. It is a DIRECTION-OF-FIX proof (buggy snap ≫ fixed, same sign + order as the live
        // ~1.5u), NOT a calibrated prediction of the live magnitude. The live confirmation is the human feel-test.
        Assert.True(sim.MaxReconcileCorrectionUnits >= 1.0d,
            $"expected to reproduce the >=1u snap-back; got correction {sim.MaxReconcileCorrectionUnits:F4}u");
        Assert.Equal(0, sim.SnapCount);
    }

    // THE FIX under the same SERVER-STALL repro: crediting REAL elapsed refunds the full stall window, so the
    // budget is never left short and the honest inputs that queued during the stall integrate in full. The dropped
    // motion goes to ZERO and the divergence collapses to the wire/quant band — the rubberband is gone.
    [Fact]
    public void Fix_ServerStall_RealElapsedCredit_NoClamp_DivergenceBounded()
    {
        var sim = new BudgetSim(fps: 145d, injectHitches: false)
        {
            UseRealElapsedCredit = true,
            ServerStallSeconds = 0.3d,
        };
        sim.RunHeldEast(seconds: 6d);

        _out.WriteLine(
            $"[FIX server-stall] maxCorrection={sim.MaxReconcileCorrectionUnits:F4}u " +
            $"maxDivergence={sim.MaxPredictedVsAuthoritativeUnits:F4}u " +
            $"droppedMotion={sim.TotalClampedDtUnits:F4}u finalGap={sim.FinalPredictedVsAuthoritativeUnits:F4}u");

        Assert.True(sim.TotalClampedDtUnits <= 1e-9d,
            $"the fix must not clamp honest play even across a server stall; dropped {sim.TotalClampedDtUnits:F6}u");
        // The snap-back the user sees is GONE: no dropped motion -> nothing for the reconcile to yank back.
        Assert.True(sim.MaxReconcileCorrectionUnits <= 0.2d,
            $"the fix must eliminate the snap-back; got correction {sim.MaxReconcileCorrectionUnits:F4}u");
        Assert.True(sim.MaxPredictedVsAuthoritativeUnits <= 0.2d,
            $"divergence not bounded after the fix: {sim.MaxPredictedVsAuthoritativeUnits:F4}u");
        Assert.Equal(0, sim.SnapCount);
    }

    // The FIX: credit the budget by the REAL elapsed wall-clock time since the last credit (not the fixed tick
    // interval), so an honest client that ran for real-elapsed seconds gets real-elapsed credit. The cap still
    // bounds a CHEATER to real-elapsed + burst (anti-speedhack intact), but it no longer clamps honest play.
    // After the fix the divergence collapses to the wire-quantization / latency-lead band — no rubberband.
    [Theory]
    [InlineData(60d, false, "60fps steady")]
    [InlineData(145d, false, "145fps steady (uncapped)")]
    [InlineData(145d, true, "145fps + 143ms frame hitches")]
    [InlineData(60d, true, "60fps + 143ms frame hitches")]
    public void Fix_RealElapsedCredit_DivergenceBounded(double fps, bool injectHitches, string label)
    {
        var sim = new BudgetSim(fps, injectHitches) { UseRealElapsedCredit = true };
        sim.RunHeldEast(seconds: 6d);

        _out.WriteLine(
            $"[FIX {label}] maxDivergence={sim.MaxPredictedVsAuthoritativeUnits:F4}u " +
            $"droppedMotion={sim.TotalClampedDtUnits:F4}u finalGap={sim.FinalPredictedVsAuthoritativeUnits:F4}u");

        // Honest play must NOT clamp at all once credit tracks real elapsed time.
        Assert.True(sim.TotalClampedDtUnits <= 1e-9d,
            $"the fix must not clamp honest play; dropped {sim.TotalClampedDtUnits:F6}u");
        // Divergence stays in the small (latency-lead + wire-quant) band — the rubberband is gone.
        Assert.True(sim.MaxPredictedVsAuthoritativeUnits <= 0.2d,
            $"divergence not bounded after the fix: {sim.MaxPredictedVsAuthoritativeUnits:F4}u");
        Assert.Equal(0, sim.SnapCount);
    }

    // The anti-speedhack guarantee is PRESERVED under the fix: a CHEATER that lies about dt (sends huge dt every
    // frame, far above the real time that elapsed) is still clamped to real-elapsed + the burst allowance. The
    // honest fix only stops penalizing honest dt; it does not let a liar out-integrate real time.
    [Fact]
    public void Fix_AntiSpeedhack_CheaterStillClampedToRealElapsedPlusBurst()
    {
        // A cheater running at 60fps real but stamping the MAX dt (0.25s) on EVERY input — claiming 0.25s of sim
        // per 1/60s real frame (a 15x speedhack). Over the run the server must integrate at most real-elapsed +
        // burst, no matter what the client claims.
        var sim = new BudgetSim(fps: 60d, injectHitches: false)
        {
            UseRealElapsedCredit = true,
            CheatClaimedDtSeconds = MaxInputDtSeconds, // lie: every frame claims the max
        };
        sim.RunHeldEast(seconds: 6d);

        // The server-integrated sim-time must not exceed real elapsed + the burst allowance (the anti-speedhack
        // invariant), so the cheater's authoritative distance is capped at real-time distance + a burst's worth.
        var realElapsed = sim.ElapsedSeconds;
        var maxHonestSimDistance = (realElapsed + BurstAllowanceSeconds) * Speed;
        _out.WriteLine(
            $"cheater: authoritativeDistance={sim.AuthoritativeDistanceUnits:F4}u " +
            $"cap(realElapsed+burst)={maxHonestSimDistance:F4}u realElapsed={realElapsed:F3}s");

        Assert.True(sim.AuthoritativeDistanceUnits <= maxHonestSimDistance + 1e-6d,
            $"speedhack escaped the budget: integrated {sim.AuthoritativeDistanceUnits:F4}u > cap {maxHonestSimDistance:F4}u");
    }

    // ---- the faithful budget+predict+integrate+reconcile sim -----------------------------------------------

    // Models the REAL local loop with the REAL ContinuousPredictor and a VERBATIM copy of the ClientSession dt
    // budget and the GameServer PollEvents-then-Tick ordering. Zero network latency (the live repro is on an
    // empty LOCAL map, where the rubberband still showed — so latency is NOT the cause; the budget clamp is).
    private sealed class BudgetSim
    {
        private readonly double _frameDt;
        private readonly bool _injectHitches;
        private readonly ContinuousPredictor _predictor;

        // The authoritative server position (open field, no collision — the live repro was an empty map).
        private double _serverX;
        private uint _serverLastInputSeq;

        // The VERBATIM dt budget state (ClientSession._dtBudgetSeconds, seeded at the burst allowance).
        private double _budgetSeconds = BurstAllowanceSeconds;

        // Time-ordered event streams. The server polls the transport CONTINUOUSLY (much faster than 20Hz) so an
        // input is integrated + DEBITED the instant it arrives (here: at its send time — zero local latency); a
        // CREDIT fires once per fixed 20Hz tick boundary. Modeling these as two timestamped streams and processing
        // them in TRUE chronological order reproduces the real interleave of debits and credits (NOT a per-tick
        // batch, which would invent or mask clamps). Each pending input carries its send-time so a debit lands at
        // the right moment relative to the surrounding credits.
        private readonly Queue<(double At, uint Seq, double WireDt)> _inputs = new();

        private double _clock;
        private double _nextCredit = ServerTickSeconds; // next 20Hz credit boundary
        private double _lastCreditClock;                // wall-clock of the previous credit (real-elapsed fix)
        private int _frame;
        private int _hitchesInjected;

        public BudgetSim(double fps, bool injectHitches)
        {
            _frameDt = 1.0d / fps;
            _injectHitches = injectHitches;
            _predictor = new ContinuousPredictor(Speed, 0d, 0d, blocked: null, radius: Radius);
        }

        // FALSE = buggy (credit by the FIXED tick interval). TRUE = fixed (credit by REAL elapsed wall-clock).
        public bool UseRealElapsedCredit { get; init; }

        // When > 0, inject SERVER-tick stalls (a GC gen2 pause / the live 571ms entities spike): a few times in the
        // run the server stalls for this many seconds — it stops polling + crediting while the client keeps sending
        // real-time dt. At stall end every queued input integrates at once. The BUGGY fixed-tick credit refunds only
        // ServerTickSeconds for the whole stall window, leaving the budget short of the real time that elapsed (the
        // precise regression); the cap (0.4s) then can't refund it, so subsequent honest inputs are clamped. The
        // real-elapsed FIX refunds the full stall, so honest play is never clamped.
        public double ServerStallSeconds { get; init; }

        // Real-time the server has stalled so far (a credit's REAL instant = its logical schedule time + this).
        private double _stallAccrued;
        private int _stallsInjected;

        // When > 0, the client LIES: it stamps this dt on every sent input regardless of the real frame dt
        // (a speedhack). The predictor still predicts the real frame dt; only the wire dt is the lie.
        public double CheatClaimedDtSeconds { get; init; }

        public double MaxPredictedVsAuthoritativeUnits { get; private set; }
        public double FinalPredictedVsAuthoritativeUnits { get; private set; }
        public double TotalClampedDtSeconds { get; private set; } // honest dt the budget refused to integrate
        public double TotalClampedDtUnits => TotalClampedDtSeconds * Speed;
        public int SnapCount { get; private set; }
        public double LowestBudgetSeconds { get; private set; } = BurstAllowanceSeconds;

        // The ACTUAL visible rubberband: the per-reconcile correction magnitude (how far the reconcile yanks the
        // render/predicted in one step) and the render-vs-predicted gap — NOT the post-reconcile predicted-vs-server
        // gap (which the reconcile has already collapsed). max(LastCorrectionUnits) is the snap-back the user SEES.
        public double MaxReconcileCorrectionUnits { get; private set; }
        public double MaxRenderVsPredictedUnits { get; private set; }
        public double AuthoritativeDistanceUnits => Math.Abs(_serverX);
        public double ElapsedSeconds => _clock;

        public void RunHeldEast(double seconds)
        {
            var end = _clock + seconds;
            while (_clock < end)
            {
                var frameDt = CurrentFrameDt();

                // PREDICT this frame with the REAL frame dt (the predictor clamps to MaxInputDtSeconds and
                // immediately advances the predicted present — exactly PredictAndSendMove). Then "send" the input,
                // timestamped at the SERVER-PERCEIVED time it will be polled at (real send time minus the server
                // stall accrued so far — a stall freezes the server, so inputs sent during it are polled at the
                // stall's end). The server's continuous poll/credit clock runs in this server-perceived time.
                var seq = _predictor.PredictAndBuffer(inputX: 1d, inputY: 0d, dtSeconds: frameDt);
                var wireDt = CheatClaimedDtSeconds > 0d
                    ? CheatClaimedDtSeconds
                    : Math.Clamp(frameDt, 0d, MaxInputDtSeconds);
                _inputs.Enqueue((_clock - _stallAccrued, seq, wireDt));

                // A server stall (GC gen2 / 571ms entities spike): the server is frozen for ServerStallSeconds of
                // REAL time — it neither polls nor credits. Inputs the client keeps sending queue up; at stall end
                // they all flush. We model the freeze by advancing the stall offset so the server-perceived clock
                // does NOT move during the stall (no credit fires across it), while real time does.
                MaybeInjectServerStall();

                // Advance the wall clock by this frame, processing every server event in TRUE server-perceived
                // chronological order up to the new server-perceived clock.
                var frameEnd = _clock + frameDt;
                AdvanceServerTo(frameEnd - _stallAccrued);

                // Cosmetic render decay once per frame (matches the live AdvanceRender cadence).
                _predictor.AdvanceRender(frameDt);

                _clock = frameEnd;
                _frame++;
            }

            // Drain any straggler input/credit at the final instant so the final gap is exact.
            AdvanceServerTo(_clock - _stallAccrued + ServerTickSeconds);
            FinalPredictedVsAuthoritativeUnits = Math.Abs(_predictor.PredictedX - _serverX);
        }

        // Inject a server stall a few times across the run (when ServerStallSeconds > 0). The stall freezes the
        // server-perceived clock for ServerStallSeconds of REAL time: _stallAccrued grows, so no credit fires
        // across the stall window, but the FIX's real-elapsed credit at the next boundary still spans it.
        private void MaybeInjectServerStall()
        {
            if (ServerStallSeconds <= 0d || _stallsInjected >= 4)
            {
                return;
            }

            if (_frame == 60 + _stallsInjected * 90)
            {
                _stallAccrued += ServerStallSeconds;
                _stallsInjected++;
            }
        }

        // Process every server event (continuous input poll/debit + 20Hz credit + the post-tick snapshot/reconcile)
        // whose timestamp is <= upTo, in TRUE chronological order.
        //
        // THE REGRESSION MECHANISM modeled here: the server credits the budget ONCE PER TICK by the FIXED tick
        // interval (1/TickRate), but a real server tick can run LONG (a GC gen2 pause / the live 571ms entities
        // spike). When a tick stalls, the credit boundary it represents lands LATE in real time, yet still adds only
        // the fixed 0.05s — while the client, running independently at 145fps, kept spending real-time dt the whole
        // stall. So the budget UNDER-credits real elapsed during a server hitch, the cap (0.4s) can't refund it
        // later, and subsequent honest inputs get clamped. We model a server-tick stall as the credit boundary's
        // REAL time being pushed out by the stall while the buggy credit still adds only ServerTickSeconds.
        private void AdvanceServerTo(double upTo)
        {
            while (true)
            {
                var nextInputAt = _inputs.Count > 0 ? _inputs.Peek().At : double.PositiveInfinity;
                var nextCreditAt = _nextCredit;

                // Whichever fires first (a tie resolves input-before-credit: PollEvents drains inputs BEFORE the
                // tick credits — the real GameServer ordering).
                if (nextInputAt <= nextCreditAt && nextInputAt <= upTo)
                {
                    var input = _inputs.Dequeue();
                    IntegrateInput(input.Seq, input.WireDt);
                }
                else if (nextCreditAt <= upTo)
                {
                    CreditTick(nextCreditAt);
                    _nextCredit += ServerTickSeconds;
                }
                else
                {
                    return;
                }
            }
        }

        // The receive-path integration (GameServer.HandleMoveIntent): sanity-clamp the dt, debit the budget, and
        // integrate the ALLOWED dt (open-field east — the live repro map). Advances LastInputSeq even on a clamp.
        private void IntegrateInput(uint seq, double wireDt)
        {
            _serverLastInputSeq = seq;
            var sanitized = Math.Clamp(wireDt, 0d, MaxInputDtSeconds);
            var allowed = ConsumeMoveDtBudget(sanitized);

            // The HONEST dt the budget refused (a cheater's clamp is the guard working as intended, not lost honest
            // motion — only counted when not cheating).
            if (CheatClaimedDtSeconds <= 0d)
            {
                TotalClampedDtSeconds += sanitized - allowed;
            }

            if (allowed > 0d)
            {
                _serverX += Speed * allowed;
            }
        }

        // A 20Hz tick boundary: credit the budget (BUGGY = fixed interval; FIX = real elapsed since last credit),
        // then emit the Q12.4-quantized snapshot and reconcile the real predictor (the wire + ReconcileLocalPredictor
        // path). The reconcile cadence is exactly the server's 20Hz snapshot cadence.
        private void CreditTick(double serverPerceivedTime)
        {
            // The credit's REAL-time instant = its server-perceived schedule time + the stall accrued so far. The
            // FIX credits REAL elapsed since the last credit (so a stall window is fully refunded); the BUGGY path
            // credits the fixed tick interval regardless of how much real time the stall added.
            var realInstant = serverPerceivedTime + _stallAccrued;
            var creditSeconds = UseRealElapsedCredit
                ? Math.Max(0d, realInstant - _lastCreditClock)
                : ServerTickSeconds;
            CreditMoveDtBudget(creditSeconds);
            _lastCreditClock = realInstant;
            if (_budgetSeconds < LowestBudgetSeconds)
            {
                LowestBudgetSeconds = _budgetSeconds;
            }

            var (qx, _) = PositionEncoding.Encode(new WorldVector(_serverX, 0d));
            var quantized = PositionEncoding.Decode(qx, 0);
            _predictor.Reconcile(new WorldVector(quantized.X, 0d), _serverLastInputSeq);

            if (_predictor.LastCorrectionUnits > 4.0d || _predictor.RenderVsPredictedUnits > 4.0d)
            {
                SnapCount++;
            }

            if (_predictor.LastCorrectionUnits > MaxReconcileCorrectionUnits)
            {
                MaxReconcileCorrectionUnits = _predictor.LastCorrectionUnits;
            }

            if (_predictor.RenderVsPredictedUnits > MaxRenderVsPredictedUnits)
            {
                MaxRenderVsPredictedUnits = _predictor.RenderVsPredictedUnits;
            }

            var gap = Math.Abs(_predictor.PredictedX - _serverX);
            if (gap > MaxPredictedVsAuthoritativeUnits)
            {
                MaxPredictedVsAuthoritativeUnits = gap;
            }
        }

        private double CurrentFrameDt()
        {
            // Inject a 143ms hitch frame periodically (the live frameMsMax=143ms spike): every ~1.5s a frame takes
            // 143ms of real time, so that frame's dt is 0.143s — the client predicts a big step while the server's
            // budget, credited only ~0.05s/tick and CAPPED at 0.4s, cannot have banked enough to cover the catch-up.
            if (_injectHitches && _hitchesInjected < 4 && _frame == 60 + _hitchesInjected * 90)
            {
                _hitchesInjected++;
                return 0.143d;
            }

            return _frameDt;
        }

        // --- VERBATIM from ClientSession (the budget arithmetic under test) ---

        private void CreditMoveDtBudget(double realElapsedSeconds)
        {
            if (realElapsedSeconds > 0d)
            {
                _budgetSeconds += realElapsedSeconds;
            }

            if (_budgetSeconds > BurstAllowanceSeconds)
            {
                _budgetSeconds = BurstAllowanceSeconds;
            }
        }

        private double ConsumeMoveDtBudget(double requestedDt)
        {
            if (requestedDt <= 0d)
            {
                return 0d;
            }

            var allowed = Math.Min(requestedDt, _budgetSeconds);
            if (allowed <= 0d)
            {
                return 0d;
            }

            _budgetSeconds -= allowed;
            return allowed;
        }
    }
}
