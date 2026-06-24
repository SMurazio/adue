using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

public sealed class TileInterpolator
{
    // Catch-up cap (remote-interp-tighten Part B): a permanent runaway guard. If the render falls more than
    // CatchUpQueueCap confirmed tiles behind the NEWEST confirmed tile (the queue backs up — cadence mismatch,
    // a GC hitch, a tab-out, a live speed change before the steady fix landed), drop the backlog and fast-forward
    // toward the newest confirmed tile so the render can NEVER trail more than ~this many tiles. Chosen small (2):
    // one tile of normal lookahead buffer plus one slack tile, so a single late/early arrival never trips it, but
    // any genuine pile-up is collapsed within ~2 tiles. The fast-forward is NOT a hard teleport — it keeps a short
    // final glide (the kept tail) so the catch-up still tweens into place over one step instead of snapping.
    private const int CatchUpQueueCap = 2;

    private readonly Queue<ConfirmedTile> _confirmedTiles = new();
    private double _stepDurationMs;
    private double _interpolationDelayMs;
    private RenderPosition _renderPosition;
    private ActiveStep? _activeStep;
    private TileCoord _lastConfirmedTile;

    public TileInterpolator(TileCoord initialTile, double stepDurationMs, double interpolationDelayMs)
    {
        _renderPosition = RenderPosition.FromTile(initialTile);
        _lastConfirmedTile = initialTile;
        _stepDurationMs = Math.Max(1, stepDurationMs);
        _interpolationDelayMs = Math.Max(0, interpolationDelayMs);
    }

    public RenderPosition RenderPosition => _renderPosition;

    public int QueueDepth => _confirmedTiles.Count + (_activeStep is null ? 0 : 1);

    public double StepDurationMs => _stepDurationMs;

    public double InterpolationDelayMs => _interpolationDelayMs;

    public void UpdateCadence(double stepDurationMs, double interpolationDelayMs)
    {
        _stepDurationMs = Math.Max(1, stepDurationMs);
        _interpolationDelayMs = Math.Max(0, interpolationDelayMs);
    }

    public void Reset(TileCoord tile)
    {
        _confirmedTiles.Clear();
        _activeStep = null;
        _lastConfirmedTile = tile;
        _renderPosition = RenderPosition.FromTile(tile);
    }

    public void Confirm(TileCoord tile, TimeSpan receivedAt)
    {
        if (tile == _lastConfirmedTile)
        {
            return;
        }

        if (_confirmedTiles.TryPeek(out var lastQueued) && lastQueued.Tile == tile)
        {
            return;
        }

        _lastConfirmedTile = tile;
        _confirmedTiles.Enqueue(new ConfirmedTile(tile, receivedAt));
        FastForwardIfBackedUp(receivedAt);
    }

    // Catch-up cap (Part B): collapse a backed-up queue so the render never trails the newest confirmed tile by
    // more than CatchUpQueueCap tiles. Counts the in-flight active step toward the backlog (it is one tile already
    // dequeued but not yet finished gliding). While the total depth exceeds the cap, drop the OLDEST queued tile —
    // those are stale waypoints the render would otherwise have to crawl through one by one. We keep the cap'th-from-
    // newest tiles so a short final glide remains (no hard teleport): the active step keeps gliding from the current
    // render position toward its target, then the remaining (<= cap) queued tiles play out normally. receivedAt of the
    // newest kept tile is back-dated so the trimmed tiles don't each re-impose the full interpolation buffer delay.
    private void FastForwardIfBackedUp(TimeSpan receivedAt)
    {
        // Depth = queued tiles + the active step (if any). The render is "behind" by this many tiles.
        var depth = _confirmedTiles.Count + (_activeStep is null ? 0 : 1);
        if (depth <= CatchUpQueueCap)
        {
            return;
        }

        // Drop the oldest queued tiles until the backlog is back within the cap. We never drop the active step
        // (it owns the live glide) nor the cap'th-from-newest tiles (they are the short tail we glide through).
        while (_confirmedTiles.Count + (_activeStep is null ? 0 : 1) > CatchUpQueueCap && _confirmedTiles.Count > 0)
        {
            _confirmedTiles.Dequeue();
        }

        // Back-date the oldest REMAINING queued tile to receivedAt so it is immediately eligible (the buffer delay
        // is measured from arrival; without this the kept tile would still wait out _interpolationDelayMs from its
        // own older timestamp, re-stalling the catch-up we just performed). Newer kept tiles keep their own arrival.
        if (_confirmedTiles.Count > 0)
        {
            var rest = _confirmedTiles.ToArray();
            _confirmedTiles.Clear();
            _confirmedTiles.Enqueue(new ConfirmedTile(rest[0].Tile, receivedAt));
            for (var i = 1; i < rest.Length; i++)
            {
                _confirmedTiles.Enqueue(rest[i]);
            }
        }
    }

    public RenderPosition Sample(TimeSpan now)
    {
        var carryOverMs = 0d;
        TimeSpan? earliestStart = null;

        for (var i = 0; i < 8; i++)
        {
            if (_activeStep is null && !TryStartNextStep(now, earliestStart))
            {
                return _renderPosition;
            }

            var step = _activeStep!;
            var elapsedMs = Math.Max(0, (now - step.StartedAt).TotalMilliseconds);
            var alpha = elapsedMs / _stepDurationMs;
            _renderPosition = RenderPosition.Lerp(step.From, step.To, alpha);

            if (alpha < 1d)
            {
                return _renderPosition;
            }

            carryOverMs = Math.Max(0, elapsedMs - _stepDurationMs);
            earliestStart = now - TimeSpan.FromMilliseconds(carryOverMs);
            _renderPosition = step.To;
            _activeStep = null;

            if (carryOverMs <= 0)
            {
                TryStartNextStep(now, now);
                return _renderPosition;
            }
        }

        return _renderPosition;
    }

    private bool TryStartNextStep(TimeSpan now, TimeSpan? earliestStart)
    {
        if (!_confirmedTiles.TryPeek(out var next))
        {
            return false;
        }

        var eligibleAt = next.ReceivedAt + TimeSpan.FromMilliseconds(_interpolationDelayMs);
        if (now < eligibleAt)
        {
            return false;
        }

        _confirmedTiles.Dequeue();
        var startedAt = earliestStart.HasValue && earliestStart.Value > eligibleAt
            ? earliestStart.Value
            : eligibleAt;
        _activeStep = new ActiveStep(_renderPosition, RenderPosition.FromTile(next.Tile), startedAt);
        return true;
    }

    private sealed record ConfirmedTile(TileCoord Tile, TimeSpan ReceivedAt);

    private sealed record ActiveStep(RenderPosition From, RenderPosition To, TimeSpan StartedAt);
}
