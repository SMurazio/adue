namespace Mmo.Server.Runtime;

// ECOLOGY E1 (docs/ecology-v1-design.md §2 D1-D5/D10, §3, §5.1): the MATH ENGINE only — per-region×type mutable
// {stock, pressure}, seeded from an EcologyRegistry at K, advanced by EcologyTick (called from GameServer.TickCore
// every 200 ticks = 10s @ 20Hz — see the "% 200" gate at the call site, mirroring how ResolveDue is a per-tick call
// but the SLAM windup/cooldown math is itself tick-quantised). NO spawning (E2), NO persistence (E3), NO wire (E4):
// this class owns nothing but the numbers + their history, and is headlessly constructible off a bare
// EcologyRegistry (no GameServer/WorldState dependency at all) — the SAME "inject nothing but the math" shape
// TelegraphScheduler proved out for the telegraph engine.
//
// D2 (logistic regrowth + decaying pressure): each ecology tick, S += r·S·(1−S/K) (rPerMinute converted to a
// per-ecology-tick rate — 6 ecology ticks/minute at the 10s cadence), and pressure *= 0.98 (≈5.7 min half-life).
// D2 overgrowth: once S reaches K, growth continues past it ONLY while pressure is idle (< PressureIdleThreshold),
// at r/3, capped at 1.5K (OvergrowthCapMultiplier) — a recently-hunted region parked at K does NOT overgrow; an
// undisturbed one does. D3 (no local extinction): S floors at max(0.05K, 0.5) every tick, and RecordKill's
// decrement clamps at the same floor. D5 (five legible states): StateOf derives DEPLETED/THIN/HEALTHY/RICH/
// OVERGROWN from S/K, exact at the 0.25/0.6/1.0/1.25 boundaries (see the acceptance tests).
//
// DEPLETED-BAND GROWTH SUPPRESSION (Allee-style; the orchestrator's resolution of the E1 brink-recovery fork):
// while S < 0.25K (the D5 DEPLETED band) every growth increment is additionally multiplied by S/(0.25K) — LINEAR
// suppression, factor 1.0 at the band edge and 0.2 at the 0.05K floor, never zero (so D3's no-extinction
// guarantee holds: growth is slowed, never stopped). WHY: pure logistic recovery time scales with the LOG of the
// deficit (the brink-vs-K/2 recovery-time ratio caps around ~2.5x), which fails the pillar-5 intent that hunting
// a region to the brink WOUNDS it for a session; the suppression makes the depleted band a slow crawl (the
// wound) while THIN and above recover at normal logistic speed. Applied to BOTH growth paths, normal and
// overgrowth (it only bites below 0.25K anyway, which the overgrowth path can never reach — uniform by design).
public sealed class EcologyState
{
    // D2: pressure is "idle" (overgrowth may proceed) below this; pressure decays toward it every ecology tick.
    public const double PressureIdleThreshold = 0.5d;

    // D2: pressure's per-ecology-tick multiplicative decay (≈5.7 min half-life at the 10s cadence).
    public const double PressureDecayPerTick = 0.98d;

    // D2: the overgrowth growth-rate divisor (r/3) and cap multiplier (1.5K = Smax).
    public const double OvergrowthRateDivisor = 3.0d;
    public const double OvergrowthCapMultiplier = 1.5d;

    // D3: the stock floor as a fraction of K, and its absolute minimum (so a tiny-K region still has a nonzero
    // floor). Smin = max(0.05*K, 0.5).
    private const double MinStockFractionOfK = 0.05d;
    private const double MinStockAbsolute = 0.5d;

    // The depleted-band suppression boundary as a fraction of K — deliberately the SAME 0.25 as the D5 DEPLETED
    // state threshold, so "the region reads DEPLETED" and "recovery is a slow crawl" are one and the same band.
    private const double DepletedBandFractionOfK = 0.25d;

    // How many ecology ticks (each 10s, per GameServer's "% 200 server ticks" gate) make up one real minute — the
    // conversion from the authored rPerMinute to the per-ecology-tick logistic rate this class actually applies.
    private const double EcologyTicksPerMinute = 6.0d;

    // A defensive ceiling on a FORCED pressure value (the /ecology pressure admin command) — kills accumulate
    // pressure unbounded in the natural path (bounded in practice by decay + the rate a player can actually land
    // kills), but an admin-typed value gets the same "can't be fat-fingered into nonsense" treatment every other
    // force-set command applies.
    private const double MaxForcedPressure = 1000d;

    private sealed class Cell
    {
        public double Stock;
        public double Pressure;
    }

    public enum PopulationState
    {
        Depleted,
        Thin,
        Healthy,
        Rich,
        Overgrown,
    }

    private readonly EcologyRegistry _registry;

    // regionId -> typeId -> its live cell. Both dictionaries are OrdinalIgnoreCase (mirrors the registry), built
    // ONCE at construction from every region×type the registry authors — EcologyTick/RecordKill/StateOf never grow
    // or shrink this set at runtime (E1 has no notion of a region/type appearing or disappearing live).
    private readonly Dictionary<string, Dictionary<string, Cell>> _cells = new(StringComparer.OrdinalIgnoreCase);

    // D1: "S seeds at K" — every region×type starts at full carrying capacity, zero pressure.
    public EcologyState(EcologyRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        foreach (var region in registry.Regions)
        {
            var byType = new Dictionary<string, Cell>(StringComparer.OrdinalIgnoreCase);
            foreach (var (typeId, config) in region.Types)
            {
                byType[typeId] = new Cell { Stock = config.K, Pressure = 0d };
            }

            _cells[region.Id] = byType;
        }
    }

    public EcologyRegistry Registry => _registry;

    // Advance every region×type by ONE ecology tick (10s of simulated time). Called from GameServer.TickCore
    // whenever `serverTick % 200 == 0` — this method itself is tick-count-agnostic (no wall-clock, no serverTick
    // read) so it is trivially callable in a loop from headless tests without a fake clock.
    public void EcologyTick()
    {
        foreach (var region in _registry.Regions)
        {
            var byType = _cells[region.Id];
            foreach (var (typeId, config) in region.Types)
            {
                GrowOne(byType[typeId], config);
            }
        }
    }

    private static void GrowOne(Cell cell, EcologyTypeConfig config)
    {
        var rPerTick = config.RPerMinute / EcologyTicksPerMinute;
        var k = config.K;

        if (cell.Stock < k)
        {
            // Standard logistic growth toward K, times the depleted-band suppression (1.0 at/above 0.25K). r is
            // small at the starter rates (<= 1.0/min => <= 0.167/tick), well inside the monotonic-convergence
            // region of the discrete logistic map (no oscillation/overshoot) — and suppression only shrinks the
            // increment further, so monotonicity is preserved.
            cell.Stock += rPerTick * cell.Stock * (1d - cell.Stock / k) * DepletedSuppression(cell.Stock, k);
            if (cell.Stock > k)
            {
                cell.Stock = k; // guard a hair of floating-point overshoot landing exactly on the K boundary.
            }
        }
        else if (cell.Pressure < PressureIdleThreshold)
        {
            // D2 overgrowth: S is already at/above K AND the region is idle (no recent kills) -> keep growing past
            // K at a THIRD the rate, toward a 1.5K ceiling, using the SAME logistic shape against the higher cap.
            // The suppression multiplier is applied here too for uniformity — S >= K on this branch, so it is
            // always 1.0 in practice.
            var sMax = k * OvergrowthCapMultiplier;
            if (cell.Stock < sMax)
            {
                var rOvergrowthPerTick = rPerTick / OvergrowthRateDivisor;
                cell.Stock += rOvergrowthPerTick * cell.Stock * (1d - cell.Stock / sMax) * DepletedSuppression(cell.Stock, k);
                if (cell.Stock > sMax)
                {
                    cell.Stock = sMax;
                }
            }
        }
        // else: at/above K under active pressure (pressure >= idle threshold) -> growth pauses at K. It does NOT
        // decay back down on its own; only RecordKill (and the floor below, defensively) reduce stock.

        var sMin = Math.Max(MinStockFractionOfK * k, MinStockAbsolute);
        if (cell.Stock < sMin)
        {
            cell.Stock = sMin;
        }

        cell.Pressure *= PressureDecayPerTick;
        if (cell.Pressure < 0d)
        {
            cell.Pressure = 0d; // defensive; decay of a non-negative value never goes negative, but stay honest.
        }
    }

    // DEPLETED-BAND GROWTH SUPPRESSION (see the class doc): the Allee-style multiplier on every growth increment.
    // Linear in S within the DEPLETED band — S/(0.25K) below 0.25K (0.2 at the 0.05K floor, approaching 1.0 at the
    // band edge), exactly 1.0 at/above it. Strictly positive for any S above zero (the D3 floor guarantees S > 0),
    // so growth is slowed to a crawl at the brink but NEVER stopped — no-extinction is preserved.
    private static double DepletedSuppression(double stock, double k)
    {
        var band = DepletedBandFractionOfK * k;
        return stock < band ? stock / band : 1d;
    }

    // D1/D3 kill hook (E2 wires the caller): a kill in `regionId` of `typeId` permanently decrements stock by 1
    // (clamped at the floor — D3, no local extinction) and adds 1 to pressure (D2's "recently hunted" memory). A
    // no-op for an unknown region/type (defensive; E2's kill hook only calls this after TryGetRegionAt + a type
    // membership check both succeed, so this path is not expected to miss in practice).
    public void RecordKill(string regionId, string typeId)
    {
        if (!TryGetCell(regionId, typeId, out var cell, out var config))
        {
            return;
        }

        var sMin = Math.Max(MinStockFractionOfK * config.K, MinStockAbsolute);
        cell.Stock = Math.Max(sMin, cell.Stock - 1d);
        cell.Pressure += 1d;
    }

    // D5: the five legible states, derived from S/K. Boundaries are EXACT (< on every threshold, per the design's
    // "DEPLETED (<0.25) / THIN (<0.6) / HEALTHY (<1.0) / RICH (<1.25) / OVERGROWN (>=1.25)"). Throws for an
    // unknown region/type — callers (the admin dump, E2/E4) only ever query a region×type they enumerated FROM
    // this same state's Regions/cells, so an unknown pair is a caller bug, not a runtime condition to swallow.
    public PopulationState StateOf(string regionId, string typeId)
    {
        var (stock, _, k) = Snapshot(regionId, typeId);
        var ratio = stock / k;
        if (ratio < 0.25d)
        {
            return PopulationState.Depleted;
        }

        if (ratio < 0.6d)
        {
            return PopulationState.Thin;
        }

        if (ratio < 1.0d)
        {
            return PopulationState.Healthy;
        }

        if (ratio < 1.25d)
        {
            return PopulationState.Rich;
        }

        return PopulationState.Overgrown;
    }

    public double StockOf(string regionId, string typeId) => Snapshot(regionId, typeId).Stock;

    public double PressureOf(string regionId, string typeId) => Snapshot(regionId, typeId).Pressure;

    // The K this region×type was authored with — exposed so callers (StateOf, the admin dump, E2's maxLive/D7
    // overgrown-modifier math) never need to round-trip back through the registry themselves.
    public double CapacityOf(string regionId, string typeId) => Snapshot(regionId, typeId).K;

    private (double Stock, double Pressure, double K) Snapshot(string regionId, string typeId)
    {
        if (!TryGetCell(regionId, typeId, out var cell, out var config))
        {
            throw new ArgumentException($"Unknown ecology region/type '{regionId}'/'{typeId}'.");
        }

        return (cell.Stock, cell.Pressure, config.K);
    }

    // Admin dev command support (/ecology set): force `regionId`×`typeId`'s stock to `value`, clamped to
    // [Smin, 1.5K] like the natural growth/kill paths never exceed. False for an unknown region/type.
    public bool TrySetStock(string regionId, string typeId, double value)
    {
        if (!TryGetCell(regionId, typeId, out var cell, out var config))
        {
            return false;
        }

        var sMin = Math.Max(MinStockFractionOfK * config.K, MinStockAbsolute);
        var sMax = config.K * OvergrowthCapMultiplier;
        cell.Stock = Math.Clamp(value, sMin, sMax);
        return true;
    }

    // Admin dev command support (/ecology pressure): force `regionId`×`typeId`'s pressure to `value`, clamped to
    // [0, MaxForcedPressure]. False for an unknown region/type.
    public bool TrySetPressure(string regionId, string typeId, double value)
    {
        if (!TryGetCell(regionId, typeId, out var cell, out _))
        {
            return false;
        }

        cell.Pressure = Math.Clamp(value, 0d, MaxForcedPressure);
        return true;
    }

    // Which authored region (if any) contains tile (x, y) — E2's kill hook seam ("did this dead monster's spawner
    // sit in a region?"). Delegates straight to the registry (pure geometry, no state needed).
    public bool TryGetRegionAt(int tileX, int tileY, out EcologyRegion region) =>
        _registry.TryGetRegionAt(tileX, tileY, out region);

    private bool TryGetCell(string regionId, string typeId, out Cell cell, out EcologyTypeConfig config)
    {
        cell = null!;
        config = default;
        if (!_registry.TryGet(regionId, out var region) || !region.Types.TryGetValue(typeId, out config))
        {
            return false;
        }

        return _cells.TryGetValue(regionId, out var byType) && byType.TryGetValue(typeId, out cell!);
    }
}
