using Mmo.Server.Configuration;

namespace Mmo.Server.Runtime;

// S60 live-tuning holder: a small MUTABLE box for the handful of server params an admin can retune at
// runtime via the AdminSetTuning message. ServerOptions stays immutable (it is the startup contract);
// this is seeded from it once and is what the game loop READS for those params instead of ServerOptions.
//
// Only fields that are genuinely safe to flip mid-run live here — they are read each tick fresh, so a
// changed value simply takes effect on the next read with no torn state. Plain fields (not properties)
// kept deliberately trivial: a single int / float read on the hot path, no allocation, no locking. The
// game loop and the AdminSetTuning handler both run on the main/tick thread, so no synchronization is
// needed; if that ever changes these become volatile/Interlocked. Nothing here persists — the panel is
// for FINDING values; the Orchestrator bakes winners into ServerOptions/env defaults afterwards.
public sealed class ServerTuning
{
    private readonly int _tickRate;

    public ServerTuning(ServerOptions options)
    {
        _tickRate = options.TickRate;
        StepCooldownMs = options.StepCooldownMs;
        InterestRadius = options.InterestRadius;
    }

    // Global base step cooldown in ms. PINNED (SPEED1): seeded once from ServerOptions and never changed at
    // runtime — the old move.stepCooldownMs live knob was removed so the base walk speed is a constant 150 ms
    // (3 ticks at 20 Hz). The step loop derives the per-entity effective cadence from StepCooldownTicks
    // (below); per-entity /speed (SpeedMultiplier) still scales off this constant base.
    public int StepCooldownMs { get; }

    // AOI interest radius in tiles. Read each AOI pass (snapshot selection + interact validation).
    public float InterestRadius { get; set; }

    // Base step cooldown in TICKS, derived exactly like ServerOptions.StepCooldownTicks so live changes
    // stay tick-quantised identically to the startup value (default value byte-for-byte unchanged).
    public uint StepCooldownTicks =>
        (uint)Math.Max(1, (int)Math.Ceiling(StepCooldownMs / (1000d / _tickRate)));
}
