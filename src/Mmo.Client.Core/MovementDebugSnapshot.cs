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
    // DIAG1 — local-player recovery-chain read-outs (measurement only). PredictedStepSeq = the predictor's
    // accepted-step count (`pred`); ConfirmedStepSeq = the last RecipientStepSeq the client learned the server
    // accepted (`conf`); Lead = pred - conf (the in-flight steps that must drain to recover). The three
    // ReconcileX counts are the reconcile outcomes since the last reset (link-3 health). SnapshotsPerSecond =
    // the confirm-channel rate (`recv/s`). All default to 0 (Empty) so existing call sites are unaffected.
    uint PredictedStepSeq = 0,
    uint ConfirmedStepSeq = 0,
    uint LeadSteps = 0,
    uint ReconcileMatched = 0,
    uint ReconcileCorrected = 0,
    uint ReconcileSnapped = 0,
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
