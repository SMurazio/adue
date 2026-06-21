namespace Mmo.Client.Core;

// NET5b: the SINGLE, pure implementation of the ack-driven tail-recovery re-send rule. Both the shipped
// MmoClient.DriveAckDrivenResend wrapper AND the headless tests call this — so the decision logic exists exactly
// once, and a headless test exercises the real rule rather than a hand-rolled copy (the gap NET5b closes).
//
// The function is PURE: it takes the current inputs + the carried re-send state and returns a decision (whether to
// re-send a batch, whether to ForceResync) PLUS the updated state. It performs NO side effects — the wrapper owns
// SendStepCommitBatch / predictor.ForceResync / writing the state back. This keeps the rule trivially testable and
// keeps DriveAckDrivenResend a thin shell whose observable behaviour is unchanged.

// The carried re-send bookkeeping (mirrors the MmoClient._resend* fields). A value type so the helper returns the
// next state by value with no aliasing surprises.
internal struct AckResendState
{
    public uint LastConf;             // last conf seen (detect ack progress)
    public bool HasLastConf;
    public double ConfStalledSinceMs; // when conf last advanced (the stall clock), ms
    public double LastSentAtMs;       // last re-send/fresh-send wall time (the cadence bound), ms
    public bool HasLastSentAt;
    public int ResendsSinceConfAdvance; // re-sends since conf last moved (the K counter)
}

// The immutable tuning constants of the rule (the shipped K/T/grace). Passed in so the test and the wrapper share
// the same numbers from one place (MmoClient supplies its consts).
internal readonly record struct AckResendConfig(
    double StallGraceMs,    // ack overdue: conf stalled at least this long before any re-send
    int FallbackCount,      // K: re-sent this many times with conf still stuck => ForceResync
    double FallbackStuckMs, // T: conf stuck at least this long (with K reached) => ForceResync
    double CadenceMs);      // re-send bound: at most one batch per cadence

internal readonly record struct AckResendDecision(bool SendBatch, bool ForceResync, AckResendState State);

internal static class AckDrivenResendPolicy
{
    // The whole rule. `nowMs` is the current wall time (ms), `pred`/`conf` the predicted/last-reconciled step-seqs,
    // `emittedFreshThisPoll` whether a fresh commit batch already went out this poll. Returns whether to re-send,
    // whether to ForceResync, and the next state. Behaviour-preserving translation of DriveAckDrivenResend.
    public static AckResendDecision Decide(
        double nowMs, uint pred, uint conf, bool emittedFreshThisPoll,
        AckResendState state, AckResendConfig config)
    {
        var lead = pred > conf ? pred - conf : 0u;

        // Reset the stall clock + fallback counter whenever the ack makes ANY progress (or on the first observation).
        if (!state.HasLastConf || conf != state.LastConf)
        {
            state.LastConf = conf;
            state.HasLastConf = true;
            state.ConfStalledSinceMs = nowMs;
            state.ResendsSinceConfAdvance = 0;
        }

        if (lead == 0)
        {
            return new AckResendDecision(false, false, state); // fully acked — nothing to recover.
        }

        // A fresh batch this poll already covered the cadence; the re-send only adds the no-new-step recovery
        // packet, so skip when a fresh one just went out.
        if (emittedFreshThisPoll)
        {
            return new AckResendDecision(false, false, state);
        }

        // Only re-send when the ack is genuinely OVERDUE (conf stalled past the grace) — in clean play conf keeps
        // up within an RTT and this never trips, so no extra packet is sent.
        if (nowMs - state.ConfStalledSinceMs < config.StallGraceMs)
        {
            return new AckResendDecision(false, false, state);
        }

        // Bound to ~1 batch / cadence.
        if (state.HasLastSentAt && nowMs - state.LastSentAtMs < config.CadenceMs)
        {
            return new AckResendDecision(false, false, state);
        }

        // Re-send this poll.
        state.LastSentAtMs = nowMs;
        state.HasLastSentAt = true;
        state.ResendsSinceConfAdvance++;

        // Bounded ForceResync fallback: re-sent K times AND conf stuck >= T ms => the commit is genuinely
        // undeliverable (heavy/black loss); converge the prediction onto the server via the RESYNC1 primitive.
        var forceResync = state.ResendsSinceConfAdvance >= config.FallbackCount
            && nowMs - state.ConfStalledSinceMs >= config.FallbackStuckMs;
        if (forceResync)
        {
            state.ResendsSinceConfAdvance = 0;
            state.ConfStalledSinceMs = nowMs;
        }

        return new AckResendDecision(true, forceResync, state);
    }
}
