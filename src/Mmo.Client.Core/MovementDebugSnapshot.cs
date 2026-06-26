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
    int LastLatencyMs,
    // SnapshotsPerSecond = the confirm-channel rate (`recv/s`): the server->client snapshot apply rate, the live
    // "is the confirm channel alive?" read-out. Defaults to 0 (Empty) so existing call sites are unaffected.
    // (CONTINUOUS MIGRATION Phase 4: the old tile-predictor recovery-chain fields — pred/conf/lead step-seqs and
    // the reconcile-outcome tallies — were removed here; the continuous predictor has no step-seq or tile reconcile.)
    double SnapshotsPerSecond = 0)
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
