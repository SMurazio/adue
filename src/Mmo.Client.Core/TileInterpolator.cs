using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

public sealed class TileInterpolator
{
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
