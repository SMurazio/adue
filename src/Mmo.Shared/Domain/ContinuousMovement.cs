namespace Mmo.Shared.Domain;

// CONTINUOUS MIGRATION (Phase 4): the SHARED per-input movement constants the server, the client predictor, AND the
// client send path must agree on. Lifted here so there is ONE source of truth instead of three drifting copies — the
// dt-alignment linchpin (docs/migration/phase-4-plan.md "dt alignment"): the predictor clamps its predicted dt to the
// SAME MaxInputDtSeconds the server sanity-clamps each received input to AND the SAME value the send path clamps the
// frame dt to, so the buffered dt == the sent dt == the server-integrated dt under normal play → replay reproduces
// the server path with no correction (R4 killed by construction).
public static class ContinuousMovement
{
    // Per-input SANITY clamp on the client-supplied dt (seconds). One frame's dt is tiny (~1/60s); 0.25s caps a single
    // input to ~5 server ticks of motion so a lone huge-dt packet can't teleport (and a legitimately laggy frame still
    // integrates fully). The server applies this on receive (GameServer.HandleMoveIntent), the predictor applies it
    // inside PredictAndBuffer (and BUFFERS the clamped dt so replay matches), and the send path applies it before
    // sending — so all three integrate the IDENTICAL dt. This is NOT the wall-clock dt BUDGET (the 0.4s burst
    // allowance), which only bites under sustained lag and is absorbed by the reconcile.
    public const double MaxInputDtSeconds = 0.25d;
}
