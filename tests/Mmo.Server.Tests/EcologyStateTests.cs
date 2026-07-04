using System;
using Mmo.Server.Runtime;
using Xunit;

namespace Mmo.Server.Tests;

// ECOLOGY E1 (docs/ecology-v1-design.md §5.1): headless coverage for the EcologyState MATH ENGINE — logistic
// convergence, brink-recovery asymmetry, pressure decay, the five D5 state boundaries, D2 overgrowth gating, D3's
// no-extinction floor, and determinism (every test drives EcologyTick() in a plain tick-count loop; nothing here
// reads a clock). EcologyState is constructed off a bare EcologyRegistry — no GameServer/WorldState dependency —
// mirroring how TelegraphSchedulerTests exercises the telegraph engine headlessly.
public sealed class EcologyStateTests
{
    // A single-region, single-type registry for isolating the pure math (region "r", type "t", an arbitrary rect —
    // geometry is irrelevant to EcologyState's math; only EcologyRegistry.TryGetRegionAt cares about it).
    private static EcologyRegistry SingleTypeRegistry(double k, double rPerMinute, int maxLive = 10)
    {
        var json = $$"""
        {
          "regions": [
            { "id": "r", "displayName": "R", "minX": 0, "minY": 0, "maxX": 10, "maxY": 10,
              "types": [ { "typeId": "t", "k": {{k.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                           "rPerMinute": {{rPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                           "maxLive": {{maxLive}} } ] }
          ]
        }
        """;
        return EcologyRegistry.FromManifestJson(json);
    }

    private static double SMin(double k) => Math.Max(0.05d * k, 0.5d);

    // §5.1 #1a: "logistic math converges (S -> K from below without overshoot at authored rates)". Starting at the
    // D3 floor, tick until stock is within 0.1% of K: at every step stock must be MONOTONICALLY NON-DECREASING and
    // must NEVER exceed K (the standard-logistic branch hard-clamps at K by construction — this is the regression
    // guard for that clamp) — across all three §7 starter (K, r) pairs.
    [Theory]
    [InlineData(10.0, 1.0)]   // Slime Hollow (slime)
    [InlineData(8.0, 0.4)]    // Eastern Scrubland (gnoll)
    [InlineData(6.0, 0.25)]   // The Verge (either type)
    public void LogisticGrowth_FromTheFloor_ConvergesToK_MonotonicallyWithoutOvershoot(double k, double rPerMinute)
    {
        var state = new EcologyState(SingleTypeRegistry(k, rPerMinute));
        Assert.True(state.TrySetStock("r", "t", SMin(k)));

        var previous = state.StockOf("r", "t");
        var target = k * 0.999d;
        var reachedTarget = false;
        for (var tick = 0; tick < 100_000; tick++)
        {
            state.EcologyTick();
            var current = state.StockOf("r", "t");
            Assert.True(current >= previous - 1e-12, $"stock decreased ({previous} -> {current}) at ecology tick {tick}.");
            Assert.True(current <= k + 1e-9, $"stock overshot K ({current} > {k}) at ecology tick {tick}.");
            previous = current;
            if (current >= target)
            {
                reachedTarget = true;
                break;
            }
        }

        Assert.True(reachedTarget, $"stock never converged to within 0.1% of K={k} within the iteration budget.");
    }

    // §5.1 #1b: "brink recovery from S_min takes markedly longer than from K/2 (the wound is real)".
    //
    // FORK RESOLVED (orchestrator decision on the E1 briefing's flagged fork): the doc's original ">= 10x" figure
    // is unreachable under PURE logistic growth (recovery time scales with the LOG of the deficit — the ratio
    // caps ~2.5x at a non-degenerate milestone), so the MODEL gained depleted-band growth suppression
    // (Allee-style — see EcologyState.DepletedSuppression): below 0.25K every growth increment is multiplied by
    // S/(0.25K), making the DEPLETED band a slow crawl (the wound) while THIN and above recover at normal
    // logistic speed. With the suppression in place, the empirically-true ratios at the 80%-of-K milestone are
    // 4.889x (K=10 — the only pair whose Smin is the fractional 0.05K floor), 4.286x (K=8) and 3.441x (K=6): the
    // smaller-K pairs are weaker because the ABSOLUTE floor (0.5) exceeds 0.05K there (Smin sits at 0.0625K /
    // 0.0833K), so less of the suppressed band is traversed. Each pair pins its own bound just below its
    // empirical value (the same pin-just-below style as the pre-suppression 2.2x bound this replaces).
    [Theory]
    [InlineData(10.0, 1.0, 4.5)] // empirical 4.889x (44 ecology ticks from S_min vs 9 from K/2)
    [InlineData(8.0, 0.4, 4.0)]  // empirical 4.286x (90 vs 21)
    [InlineData(6.0, 0.25, 3.2)] // empirical 3.441x (117 vs 34)
    public void BrinkRecovery_FromSMin_IsMarkedlySlowerThanFromKHalf(double k, double rPerMinute, double minRatio)
    {
        var target = k * 0.8d;

        var fromMin = new EcologyState(SingleTypeRegistry(k, rPerMinute));
        Assert.True(fromMin.TrySetStock("r", "t", SMin(k)));
        var ticksFromMin = TicksToReach(fromMin, target);

        var fromHalf = new EcologyState(SingleTypeRegistry(k, rPerMinute));
        Assert.True(fromHalf.TrySetStock("r", "t", k / 2d));
        var ticksFromHalf = TicksToReach(fromHalf, target);

        Assert.True(ticksFromHalf > 0, "test setup: K/2 must take a nonzero number of ticks to reach the milestone.");
        var ratio = (double)ticksFromMin / ticksFromHalf;
        Assert.True(ratio >= minRatio, $"K={k}: recovery-from-brink ratio was only {ratio:0.###} " +
            $"({ticksFromMin} ticks from S_min vs {ticksFromHalf} from K/2) — expected >= {minRatio}x with depleted-band suppression.");
    }

    // The suppression multiplier pinned DIRECTLY (one exact tick, no simulation loop): at S = 0.125K (mid-band)
    // one tick's increment is rPerTick·S·(1−S/K) times the 0.5 suppression factor (S/(0.25K)); at S = 0.25K (the
    // band edge, NOT inside the strict "< 0.25K" band) the factor is exactly 1.0 — the plain logistic increment.
    // K=40 keeps the absolute 0.5 floor out of play (Smin = 0.05K = 2).
    [Fact]
    public void DepletedBandSuppression_ScalesTheIncrementLinearly_AndIsInertAtTheBandEdge()
    {
        const double k = 40d;
        const double rPerTick = 1.0d / 6.0d; // rPerMinute 1.0 across 6 ecology ticks/minute.

        var midBand = new EcologyState(SingleTypeRegistry(k, rPerMinute: 1.0));
        Assert.True(midBand.TrySetStock("r", "t", 5d)); // 0.125K -> suppression factor 5/10 = 0.5.
        midBand.EcologyTick();
        var expectedMidBand = 5d + rPerTick * 5d * (1d - 5d / k) * 0.5d;
        Assert.Equal(expectedMidBand, midBand.StockOf("r", "t"), 9);

        var bandEdge = new EcologyState(SingleTypeRegistry(k, rPerMinute: 1.0));
        Assert.True(bandEdge.TrySetStock("r", "t", 10d)); // exactly 0.25K -> factor 1.0 (band is a strict "<").
        bandEdge.EcologyTick();
        var expectedBandEdge = 10d + rPerTick * 10d * (1d - 10d / k);
        Assert.Equal(expectedBandEdge, bandEdge.StockOf("r", "t"), 9);
    }

    private static int TicksToReach(EcologyState state, double target)
    {
        for (var tick = 1; tick <= 200_000; tick++)
        {
            state.EcologyTick();
            if (state.StockOf("r", "t") >= target)
            {
                return tick;
            }
        }

        throw new InvalidOperationException("stock never reached the target milestone within the iteration budget.");
    }

    // §5.1 #1c: "pressure decays to idle (<0.5) in ~15 min of ecology ticks". A representative accumulated pressure
    // (3.0 — a handful of kills during a hunting session; D2 does not pin an exact accumulation scenario, so this
    // value is chosen to land in the documented ballpark) decays below the idle threshold within the ~15 min
    // window (90 ecology ticks @ 10s/tick), asserted with a generous +/-15% tolerance band (78-104 ticks) around
    // the exact 0.98^n crossing point.
    [Fact]
    public void PressureDecaysToIdle_WithinRoughlyFifteenMinutesOfEcologyTicks()
    {
        var state = new EcologyState(SingleTypeRegistry(k: 10, rPerMinute: 1.0));
        Assert.True(state.TrySetPressure("r", "t", 3.0d));

        var idleTick = -1;
        for (var tick = 1; tick <= 200; tick++)
        {
            state.EcologyTick();
            if (state.PressureOf("r", "t") < EcologyState.PressureIdleThreshold)
            {
                idleTick = tick;
                break;
            }
        }

        Assert.InRange(idleTick, 78, 104); // ~13-17.3 min at 10s/ecology-tick, centred on the ~15 min claim.
    }

    // The 0.98 decay constant's HALF-LIFE is exact + independent of any starting-pressure choice: pressure starting
    // at 1.0 must cross below 0.5 within a tight window around ln(0.5)/ln(0.98) ~= 34.3 ecology ticks (~5.7 min —
    // matching EcologyState.PressureDecayPerTick's own doc comment), a robust pin on the decay law itself.
    [Fact]
    public void PressureDecay_HalfLifeMatchesTheDocumentedConstant()
    {
        var state = new EcologyState(SingleTypeRegistry(k: 10, rPerMinute: 1.0));
        Assert.True(state.TrySetPressure("r", "t", 1.0d));

        var halfLifeTick = -1;
        for (var tick = 1; tick <= 100; tick++)
        {
            state.EcologyTick();
            if (state.PressureOf("r", "t") < 0.5d)
            {
                halfLifeTick = tick;
                break;
            }
        }

        Assert.InRange(halfLifeTick, 33, 36);
    }

    // §5.1 #1d: "state-enum boundaries exact at 0.25/0.6/1.0/1.25". K=40 gives clean integer stock values at every
    // boundary; "just below" probes confirm the category BELOW each boundary, and the boundary value itself lands
    // in the category ABOVE it (every threshold in D5 is a strict "<", so the boundary itself belongs to the next,
    // richer state).
    [Theory]
    [InlineData(9.999, EcologyState.PopulationState.Depleted)]
    [InlineData(10.0, EcologyState.PopulationState.Thin)]     // == 0.25K
    [InlineData(23.999, EcologyState.PopulationState.Thin)]
    [InlineData(24.0, EcologyState.PopulationState.Healthy)]  // == 0.6K
    [InlineData(39.999, EcologyState.PopulationState.Healthy)]
    [InlineData(40.0, EcologyState.PopulationState.Rich)]     // == 1.0K
    [InlineData(49.999, EcologyState.PopulationState.Rich)]
    [InlineData(50.0, EcologyState.PopulationState.Overgrown)] // == 1.25K
    [InlineData(60.0, EcologyState.PopulationState.Overgrown)] // == 1.5K (the overgrowth cap)
    public void StateBoundaries_AreExact(double stock, EcologyState.PopulationState expected)
    {
        var state = new EcologyState(SingleTypeRegistry(k: 40, rPerMinute: 1.0));
        Assert.True(state.TrySetStock("r", "t", stock));
        Assert.Equal(expected, state.StateOf("r", "t"));
    }

    // §5.1 #1e: "overgrowth only under idle pressure and capped at 1.5K". At S == K under ACTIVE pressure (re-forced
    // every tick so the natural per-tick decay can never let it drift idle), stock must stay pinned at K — growth
    // does not resume past K while the region is under pressure. Once pressure goes idle, growth resumes past K,
    // stays monotonically non-decreasing, and never exceeds the 1.5K cap.
    [Fact]
    public void Overgrowth_OnlyProceedsWhilePressureIsIdle_AndCapsAt1_5K()
    {
        const double k = 10d;
        var state = new EcologyState(SingleTypeRegistry(k, rPerMinute: 1.0));
        Assert.True(state.TrySetStock("r", "t", k));

        for (var tick = 0; tick < 200; tick++)
        {
            Assert.True(state.TrySetPressure("r", "t", 1.0d)); // re-force ACTIVE every tick (decay would otherwise idle it).
            state.EcologyTick();
            Assert.Equal(k, state.StockOf("r", "t"), 9); // pinned at K — no overgrowth while under pressure.
        }

        Assert.True(state.TrySetPressure("r", "t", 0d)); // go idle.
        var sMax = k * EcologyState.OvergrowthCapMultiplier;
        var previous = state.StockOf("r", "t");
        var reachedNearCap = false;
        for (var tick = 0; tick < 100_000; tick++)
        {
            state.EcologyTick();
            var current = state.StockOf("r", "t");
            Assert.True(current >= previous - 1e-12, "stock decreased during idle overgrowth.");
            Assert.True(current <= sMax + 1e-9, $"stock exceeded the 1.5K overgrowth cap ({current} > {sMax}).");
            previous = current;
            if (current >= sMax * 0.999d)
            {
                reachedNearCap = true;
                break;
            }
        }

        Assert.True(reachedNearCap, "stock never approached the 1.5K overgrowth cap within the iteration budget.");
    }

    // §5.1 #1f: "RecordKill clamps at the floor". Repeated kills on a small-K region drive stock down to Smin and
    // never below it, regardless of how many additional kills land after the floor is reached.
    [Fact]
    public void RecordKill_ClampsAtTheFloor_AndNeverGoesBelowIt()
    {
        const double k = 6d;
        var state = new EcologyState(SingleTypeRegistry(k, rPerMinute: 0.25));
        var sMin = SMin(k);

        for (var i = 0; i < 50; i++)
        {
            state.RecordKill("r", "t");
            Assert.True(state.StockOf("r", "t") >= sMin - 1e-9, $"stock fell below the floor after {i + 1} kills.");
        }

        Assert.Equal(sMin, state.StockOf("r", "t"), 9);
        // Pressure keeps accumulating (unbounded via the natural kill path — only a FORCED admin value is clamped).
        Assert.True(state.PressureOf("r", "t") >= 49d);
    }

    // RecordKill is a no-op for a region/type this state doesn't know about (E2's kill hook only calls it after a
    // successful TryGetRegionAt + type-membership check, but the method itself must not throw on a miss).
    [Fact]
    public void RecordKill_OnUnknownRegionOrType_IsANoOp()
    {
        var state = new EcologyState(SingleTypeRegistry(k: 10, rPerMinute: 1.0));
        state.RecordKill("no_such_region", "t"); // must not throw.
        state.RecordKill("r", "no_such_type");   // must not throw.
        Assert.Equal(10d, state.StockOf("r", "t"), 9); // the known cell is untouched.
    }

    // §5.1 #1g: "determinism (no wall-clock — everything driven by tick counts)". Two independently constructed
    // states off equal registries, driven through the IDENTICAL sequence of ticks/kills/forced values, must land on
    // BIT-FOR-BIT identical stock/pressure — proving nothing here reads a clock or any other hidden ambient state.
    [Fact]
    public void IdenticalTickSequences_ProduceBitForBitIdenticalResults()
    {
        EcologyState Build()
        {
            var s = new EcologyState(SingleTypeRegistry(k: 10, rPerMinute: 1.0));
            for (var tick = 0; tick < 500; tick++)
            {
                s.EcologyTick();
                if (tick % 37 == 0)
                {
                    s.RecordKill("r", "t");
                }

                if (tick == 200)
                {
                    s.TrySetPressure("r", "t", 2.0d);
                }
            }

            return s;
        }

        var a = Build();
        var b = Build();

        Assert.Equal(a.StockOf("r", "t"), b.StockOf("r", "t"));       // exact equality — no tolerance.
        Assert.Equal(a.PressureOf("r", "t"), b.PressureOf("r", "t")); // exact equality — no tolerance.
    }

    // EcologyState seeds every region×type's stock at K, pressure at 0 (D1: "S seeds at K") — the starting point
    // every other test in this file (and E2's spawner) can rely on without an explicit TrySetStock call.
    [Fact]
    public void ConstructionSeedsStockAtK_AndPressureAtZero()
    {
        var state = new EcologyState(SingleTypeRegistry(k: 10, rPerMinute: 1.0));
        Assert.Equal(10d, state.StockOf("r", "t"), 9);
        Assert.Equal(0d, state.PressureOf("r", "t"), 9);
        Assert.Equal(EcologyState.PopulationState.Rich, state.StateOf("r", "t")); // ratio == 1.0 -> Rich (D5's "< 1.25" band).
    }

    [Fact]
    public void TrySetStockAndTrySetPressure_ClampAndReportFailureForUnknownKeys()
    {
        var state = new EcologyState(SingleTypeRegistry(k: 10, rPerMinute: 1.0));

        Assert.False(state.TrySetStock("no_such_region", "t", 5d));
        Assert.False(state.TrySetPressure("r", "no_such_type", 5d));

        Assert.True(state.TrySetStock("r", "t", -100d)); // clamped up to Smin.
        Assert.Equal(SMin(10d), state.StockOf("r", "t"), 9);

        Assert.True(state.TrySetStock("r", "t", 100d)); // clamped down to 1.5K.
        Assert.Equal(15d, state.StockOf("r", "t"), 9);

        Assert.True(state.TrySetPressure("r", "t", -5d)); // clamped up to 0.
        Assert.Equal(0d, state.PressureOf("r", "t"), 9);
    }
}
