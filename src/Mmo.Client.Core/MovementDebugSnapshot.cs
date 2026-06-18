using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

public readonly record struct MovementDebugSnapshot(
    uint LastSentSequence,
    Direction8? LastSentDirection,
    DateTimeOffset? LastSentAtUtc,
    uint LastConfirmedNetworkId,
    TileCoord? LastConfirmedTile,
    uint LastConfirmedSnapshotSequence,
    DateTimeOffset? LastConfirmedAtUtc,
    int QueueDepth,
    double EffectiveCadenceMs,
    RenderPosition RenderPosition,
    int LastLatencyMs)
{
    public static MovementDebugSnapshot Empty { get; } = new(
        0,
        null,
        null,
        0,
        null,
        0,
        null,
        0,
        0,
        new RenderPosition(0, 0),
        0);
}
