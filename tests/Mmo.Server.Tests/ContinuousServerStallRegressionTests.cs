using Mmo.Client.Core.Continuous;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Mmo.Server.Tests;

// CONTINUOUS MIGRATION (server-stall regression): MEASURES the soft "authoritative position stalls then snaps the
// character back" rubberband on PLAIN movement across an empty map (snapCount=0). This is a SECOND, independent
// regression from the dt-budget one already fixed in 676f1fa — it reproduces with ZERO packet loss and ZERO server
// stalls, on perfectly healthy steady motion, so the dt-budget refund cannot mask it.
//
// THE MEASURED DIFFERENCE FROM THE EXPERIMENT (exp/continuous-movement):
//   * EXPERIMENT server (Mmo.Tools.ContinuousServer): broadcasts state.Mover.X/Z (the live continuous float
//     authoritative position) in EVERY tick's ContinuousState, UNCONDITIONALLY. Every snapshot carries the fresh
//     authoritative position, so the client's reconcile base (ContinuousPredictor._baseX/Y) tracks the server
//     smoothly and the replay lands on the live prediction → NO correction.
//   * MIGRATION server (GameServer): the local player's own entity is re-sent only while its StateRevision differs
//     from the client-acked revision (GameServer.HasAckedCurrentRevision / SendSnapshotPackets). But
//     WorldEntity.ApplyResolvedMove bumps StateRevision ONLY on a rounded-TILE crossing (R1: "do NOT bump every
//     sub-tile tick"). So while the player moves SUB-TILE (the overwhelming majority of frames — a tile takes many
//     ticks to cross), the player is DELTA'D OUT of the snapshot: its advancing continuous position is NOT re-sent.
//     The client then reconciles its predictor against the STALE position frozen at the last tile crossing, while
//     the live prediction has advanced a full sub-tile beyond it → the reconcile correction == the sub-tile distance
//     travelled since the last tile cross. That is the soft snap-back the user sees ("the server position stalls,
//     then snaps my character back to it").
//
// THIS HARNESS IS THE REAL PATH, not a model:
//   * SERVER INTEGRATE: the real WorldEntity.IntegrateMovement (open-field; empty map → no walls).
//   * SERVER DELTA SELECTION: the real ClientSession.HasAckedCurrentRevision (StateRevision-gated re-send), with the
//     ack advanced exactly as AcknowledgeSnapshot would on the client's ack of the carried revision.
//   * WIRE: the real Q12.4 PositionEncoding round-trip (so the test includes the ≤0.0625u quantization, proving the
//     snap is the STALL, not the quantizer).
//   * CLIENT RECONCILE: the real ContinuousPredictor.Reconcile/PredictAndBuffer (open field, radius 0).
//
// The assertion is the user-visible symptom: the maximum reconcile CORRECTION magnitude (the snap-back) — NOT the
// post-reconcile residual gap (which the reconcile collapses every tick). The bug produces a ~0.5-1.0-tile snap;
// re-sending on sub-tile motion drives it to the quantization floor.
public sealed class ContinuousServerStallRegressionTests
{
    private readonly ITestOutputHelper _output;

    public ContinuousServerStallRegressionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const double Speed = 4.0d;          // tiles/sec (representative of the live base move speed)
    private const double FrameDt = 1.0 / 60.0;  // 60fps client frame
    private const double TickDt = 1.0 / 20.0;   // 20Hz server tick (a snapshot every ~3 client frames)

    // The bug: the server re-sends the local player ONLY when its StateRevision changes (tile cross). Walk straight
    // east across several tiles and measure the worst reconcile correction the client applies.
    [Fact]
    public void TileQuantizedResend_StrandsTheAuthoritativePosition_AndSnapsTheRenderBack()
    {
        var worst = RunStraightWalk(resendOnSubTileMotion: false);
        _output.WriteLine($"[tile-quantized re-send] worst reconcile correction = {worst:0.0000} u");

        // With the live tile-quantized re-send the client's reconcile base stalls at the last tile crossing while the
        // prediction runs a sub-tile ahead, so the correction grows to ~half a tile or more. Assert it is a clearly
        // visible snap (≫ the 0.0625u quantization floor).
        Assert.True(
            worst > 0.25d,
            $"Expected a visible snap-back from the stalled authoritative position; got {worst:0.0000} u.");
    }

    // The FIX: re-send the local player whenever its continuous position moved (sub-tile included), so every snapshot
    // carries the fresh authoritative position — exactly what the experiment server did. The correction collapses to
    // the quantization floor.
    [Fact]
    public void SubTileResend_KeepsTheAuthoritativePositionFresh_NoSnapBack()
    {
        var worst = RunStraightWalk(resendOnSubTileMotion: true);
        _output.WriteLine($"[sub-tile re-send (fix)] worst reconcile correction = {worst:0.0000} u");

        // Every snapshot carries the live continuous position → the reconcile base tracks the server, replay lands on
        // the prediction, and the only residual is the Q12.4 quantization (≤0.0625u/axis, ≤~0.09u magnitude).
        Assert.True(
            worst < 0.15d,
            $"Expected no visible snap-back once every snapshot carries the live position; got {worst:0.0000} u.");
    }

    // Drives the full server-integrate → delta-select → wire-quantize → client-reconcile loop for a straight eastward
    // walk and returns the worst (largest) reconcile correction the client applied. The single toggle
    // `resendOnSubTileMotion` switches between the live tile-quantized re-send trigger (false) and the proposed
    // sub-tile re-send (true) — everything else is identical, so the delta in the result IS the regression.
    private static double RunStraightWalk(bool resendOnSubTileMotion)
    {
        var entity = CreateEntity(new TileCoord(0, 0), Speed);

        var predictor = new ContinuousPredictor(
            speed: Speed,
            startX: entity.Position.X,
            startY: entity.Position.Y,
            blocked: null,   // empty map: open field, no walls
            radius: 0d);

        // The "client-acked" revision the server's HasAckedCurrentRevision diffs against. Seeded to the entity's
        // current revision (the spawn snapshot is acked), advanced whenever a snapshot carrying the player is acked.
        var ackedRevision = entity.StateRevision;
        // For the sub-tile re-send variant we track the last continuous position the server SENT, so we can decide
        // "did the position move since we last sent it?" without touching StateRevision (the minimal fix surface).
        var lastSentPosition = entity.Position;
        var firstSend = true;

        var worstCorrection = 0d;

        // Tick the server at 20Hz; between server ticks the client predicts ~3 frames at 60fps. Walk east ~3 tiles.
        const int serverTicks = 40; // 2 seconds → ~8 tiles of motion at speed 4 → many tile crossings
        var inputSeqAtTick = 0u;
        var framesPerTick = (int)Math.Round(TickDt / FrameDt);

        for (var tick = 0; tick < serverTicks; tick++)
        {
            // ---- CLIENT: predict + send N input frames this tick ----
            for (var f = 0; f < framesPerTick; f++)
            {
                inputSeqAtTick = predictor.PredictAndBuffer(inputX: 1d, inputY: 0d, dtSeconds: FrameDt);

                // ---- SERVER: integrate this fresh input by its own dt (HandleMoveIntent's per-input integrate). ----
                entity.IntegrateMovement(new WorldVector(1d, 0d).Normalized(), FrameDt);
            }

            // ---- SERVER: snapshot delta selection (SendSnapshotPackets). Decide whether the local player rides this
            // snapshot's payload. Live behaviour: ride iff StateRevision differs from acked (tile-cross only). ----
            bool carryPlayer;
            if (resendOnSubTileMotion)
            {
                // FIX: ride whenever the continuous position moved since last sent (sub-tile included).
                carryPlayer = firstSend || entity.Position != lastSentPosition;
            }
            else
            {
                // LIVE BUG: ride only when the tile-quantized StateRevision changed.
                carryPlayer = !HasAckedCurrentRevision(ackedRevision, entity.StateRevision);
            }

            if (carryPlayer)
            {
                // WIRE: quantize the authoritative position through the real Q12.4 codec, exactly as
                // ToEntityStateSnapshot → the snapshot encoder does, then decode it client-side.
                var (qx, qy) = PositionEncoding.Encode(entity.Position);
                var wirePosition = PositionEncoding.Decode(qx, qy);

                // CLIENT: reconcile against the carried position + the server's LastInputSeq (== the highest input the
                // server has integrated, which here is every input it received: inputSeqAtTick).
                predictor.Reconcile(wirePosition, inputSeqAtTick);
                worstCorrection = Math.Max(worstCorrection, predictor.LastCorrectionUnits);

                // The client acks this snapshot → the server's acked baseline advances to the carried revision.
                ackedRevision = entity.StateRevision;
                lastSentPosition = entity.Position;
                firstSend = false;
            }
            else
            {
                // DELTA'D OUT: the player is absent from the payload. The client still reconciles on the header
                // (MmoClient's delta'd-out branch: ReconcileLocalPredictor with the entity's LAST-KNOWN position) —
                // which is the STALE position frozen at the last carried snapshot. THIS is where the base stalls.
                var (qx, qy) = PositionEncoding.Encode(lastSentPosition);
                var stalePosition = PositionEncoding.Decode(qx, qy);
                predictor.Reconcile(stalePosition, inputSeqAtTick);
                worstCorrection = Math.Max(worstCorrection, predictor.LastCorrectionUnits);
            }
        }

        return worstCorrection;
    }

    // Mirror of ClientSession.HasAckedCurrentRevision for the local player's own entity: in sync (omit) iff the acked
    // revision equals the entity's current StateRevision.
    private static bool HasAckedCurrentRevision(uint ackedRevision, uint currentRevision) =>
        ackedRevision == currentRevision;

    private static WorldEntity CreateEntity(TileCoord tile, double speed)
    {
        var entity = new WorldEntity(
            id: 1,
            networkId: 1,
            EntityKind.Player,
            tile,
            Direction8.S,
            "Player1",
            Guid.NewGuid(),
            ownerSession: null,
            isDurable: true);
        entity.SetSpeedUnitsPerSecond(speed);
        return entity;
    }
}
