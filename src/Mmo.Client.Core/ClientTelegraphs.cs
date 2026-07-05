using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// TELEGRAPH T2 (docs/ability-telegraph-sync-design.md): the client-side view of one active ground telegraph, built
// for the render layer by MmoClient.CopyTelegraphDecalsTo. The shape fields are the LOCKED wire shape (HONEST
// TELEGRAPH — user decision 2026-07-03: the drawn shape IS the hit rule, so the renderer must draw EXACTLY this shape,
// no padding/shrink/edge bias; membership is deliberately CENTER-POINT — a body clipping the edge is NOT hit — so
// blurring the true edge would lie in both directions). Progress is the deadline-form fill fraction in [0,1] (1 == the
// resolve instant arrived); Resolved flips true AT estimated T and stays true through the brief flash window, after
// which the entry is pruned and the decal despawns.
//
// WEDGE+LINE (S-telegraph-shapes-wedge-line): the shape params ride here so the Godot decal pass draws the right mesh
// PER KIND from the wire fields ALONE — Radius is the circle radius / wedge reach / line length; AimRadians is the
// wedge/line bearing; HalfAngleRadians is the wedge half-angle; HalfWidth is the line half-width. Circle ignores the
// three trailing params (they are 0), so its decal is unchanged.
public readonly record struct TelegraphDecalState(
    ulong TelegraphId,
    TelegraphShapeKind Kind,
    WorldVector Origin,
    double Radius,
    double Progress,
    bool Resolved,
    double AimRadians = 0d,
    double HalfAngleRadians = 0d,
    double HalfWidth = 0d);

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
