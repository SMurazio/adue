using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// TELEGRAPH T2 (docs/ability-telegraph-sync-design.md): the client-side view of one active ground telegraph, built
// for the render layer by MmoClient.CopyTelegraphDecalsTo. Kind/Origin/Radius are the LOCKED wire shape (HONEST
// TELEGRAPH — user decision 2026-07-03: the drawn circle IS the hit rule, so the renderer must draw EXACTLY this
// radius, no padding/shrink/edge bias; membership is deliberately CENTER-POINT — a body clipping the rim is NOT hit —
// so blurring the true edge would lie in both directions). Progress is the deadline-form fill fraction in [0,1]
// (1 == the resolve instant arrived); Resolved flips true AT estimated T and stays true through the brief flash
// window, after which the entry is pruned and the decal despawns.
public readonly record struct TelegraphDecalState(
    ulong TelegraphId,
    TelegraphShapeKind Kind,
    WorldVector Origin,
    double Radius,
    double Progress,
    bool Resolved);

// The deadline-form fill arithmetic, kept as a pure function so the headless suite pins it without a client. All in
// SERVER TICKS: `estimatedServerTick` is the cosmetic clock's fractional "now"; start/resolve are the two absolute
// ticks off the wire.
public static class TelegraphFill
{
    // progress = (now − start) / (T − start), clamped to [0,1]: 0 before the windup started (a snapshot-starved
    // estimate can sit slightly behind start on the arrival tick), 1 at/after the deadline. A degenerate window
    // (resolve <= start — the codec/scheduler never produce it, but a hostile packet could) is treated as already
    // full: the honest reading of "resolves no later than its own start". uint ticks promote exactly into double
    // (< 2^53), so the subtraction is safe against the uint wrap a raw `now - start` would suffer.
    public static double Progress(double estimatedServerTick, uint startTick, uint resolveTick)
    {
        if (resolveTick <= startTick)
        {
            return 1d;
        }

        var progress = (estimatedServerTick - startTick) / ((double)resolveTick - startTick);
        return Math.Clamp(progress, 0d, 1d);
    }
}
