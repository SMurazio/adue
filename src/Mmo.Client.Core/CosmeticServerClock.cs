namespace Mmo.Client.Core;

// TELEGRAPH T2 (docs/ability-telegraph-sync-design.md): the client's COSMETIC server-clock estimate — "what server
// tick is it roughly NOW?", derived from the serverTick that already rides every WorldSnapshot header, EMA-smoothed
// over arrivals so per-snapshot network jitter doesn't wobble the estimate.
//
// PRESENTATION ONLY — THIS MUST NEVER FEED SIMULATION OR PREDICTION. The continuous predictor deliberately has no
// server-tick estimator (the B2 lesson: an EstimateServerTick in the sim only zeroed an invisible temporal lead and
// added a desync surface — attacks/actions send AuthoredTick=0 and the server anchors at receipt). This class exists
// for exactly one job: driving the telegraph decal fill (now − start)/(T − start) so every viewer's fill completes at
// the shared deadline T. Its estimate is systematically LATE by roughly the one-way snapshot latency (the observed
// serverTick was stamped a trip ago) — which is fine for the deadline form: the fill completes when the resolve's
// OBSERVABLE effects (damage number, HP drop) arrive on the same-latency wire, so the visual and the consequence stay
// in step on every client, near or far.
//
// Model: each applied snapshot observes offsetTicks = serverTick − localSeconds × tickRate (how far the server's tick
// counter is ahead of the local clock, in ticks). The first observation snaps; later ones EMA-blend, so the estimate
// converges to the MEAN offset under jittered arrivals instead of chasing each burst. A sample far outside the
// smoothed value (> SnapThresholdSeconds) re-snaps — a reconnect / long pause / debugger stall is a clock STEP, not
// jitter to be averaged through. EstimateServerTick then extrapolates between arrivals off the local clock, so the
// estimate advances smoothly at tick rate even while snapshots are in flight.
public sealed class CosmeticServerClock
{
    // EMA weight per observation. Snapshots arrive at up to the server tick rate (20 Hz), so 0.1 averages over the
    // last ~10 arrivals (~0.5 s) — enough smoothing to flatten per-packet jitter, fresh enough to track genuine drift.
    private const double SmoothingAlpha = 0.1d;

    // A sample this many SECONDS away from the smoothed offset is a step (reconnect/pause), not jitter: snap to it.
    private const double SnapThresholdSeconds = 2d;

    private double _offsetTicks;
    private int _tickRate;
    private bool _hasEstimate;

    public bool HasEstimate => _hasEstimate;

    // Observe one applied snapshot: its header serverTick and the local clock at arrival. `tickRate` is the server's
    // replicated tick rate (ServerHello); a tick-rate change (should never happen mid-session) re-snaps because the
    // old offset is measured in incompatible units.
    public void ObserveSnapshot(uint serverTick, TimeSpan localNow, int tickRate)
    {
        if (tickRate <= 0)
        {
            return; // defensive — a nonsense rate would poison the offset; keep the last good estimate.
        }

        var sample = serverTick - (localNow.TotalSeconds * tickRate);
        if (!_hasEstimate || tickRate != _tickRate || Math.Abs(sample - _offsetTicks) > SnapThresholdSeconds * tickRate)
        {
            _offsetTicks = sample;
            _tickRate = tickRate;
            _hasEstimate = true;
            return;
        }

        _offsetTicks += SmoothingAlpha * (sample - _offsetTicks);
    }

    // The estimated CURRENT server tick (fractional — a fill fraction wants sub-tick smoothness at render rate), or
    // null before the first snapshot lands. Extrapolates from the smoothed offset off the local clock, so it advances
    // every render frame, not once per arrival. NOT guaranteed monotonic across arrivals: an EMA update can nudge the
    // offset down by a fraction of a tick — invisible in a fill bar, and cosmetics-only means nothing downstream may
    // rely on monotonicity anyway.
    public double? EstimateServerTick(TimeSpan localNow)
    {
        if (!_hasEstimate)
        {
            return null;
        }

        return (localNow.TotalSeconds * _tickRate) + _offsetTicks;
    }

    // TELEGRAPH T2 REVIEW FOLLOWUP (ghost-decal-after-reconnect, latent): forget the estimate entirely — the next
    // ObserveSnapshot after a reconnect then SNAPS (the !_hasEstimate branch) instead of treating the new server's
    // tick counter as jitter around the old (now-meaningless) offset. Without this, reconnecting to a RESTARTED
    // server (tick ~0) leaves the old high offset in place; a sample that far away IS already a snap by
    // SnapThresholdSeconds, so in practice a bare reconnect self-heals within one snapshot — but Reset makes the
    // "no stale estimate survives a disconnect" invariant explicit rather than relying on the snap-threshold's
    // side effect, and it's what MmoClient.Disconnect calls alongside clearing _activeTelegraphs.
    public void Reset()
    {
        _offsetTicks = 0d;
        _tickRate = 0;
        _hasEstimate = false;
    }
}
