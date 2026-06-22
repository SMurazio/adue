using Mmo.Server.Configuration;
using Mmo.Shared.Domain;

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

    // COMBAT-TUNING (live): the free-aim combat feel-knobs, now LIVE-tunable (combat.* registry keys) and replicated
    // to clients (CombatTuningSnapshot) so the server's resolution and the client's wedge/predictor/cooldown-viz can
    // never silently drift. Seeded to the former hard-coded constants so default behaviour is byte-for-byte
    // unchanged; the registry clamps any live change. Read fresh each attack in HandleAttack + FreeAimSectorResolver.
    //
    //   AttackCooldownMs — per-entity attack cadence gate (was the GameServer.AttackCooldownMs const, 600).
    //   AttackRootMs     — how long an accepted swing roots the attacker's MOVEMENT (was CombatTuning.MovementRootMs,
    //                      200). The client predictor mirrors this via the replicated snapshot for swing-root parity.
    //   FreeAimHalfAngleDegrees / FreeAimRadiusTiles — the sector geometry (were the GameServer consts 45 / 1.6).
    //   AttackDamage     — HP per enemy hit (was GameServer.MeleeConeDamage, 20).
    public int AttackCooldownMs { get; set; } = 600;
    public int AttackRootMs { get; set; } = CombatTuning.MovementRootMs;
    public double FreeAimHalfAngleDegrees { get; set; } = 45d;
    public double FreeAimRadiusTiles { get; set; } = 1.6d;
    public int AttackDamage { get; set; } = 20;

    // SWING-SLOW (live, combat.swingMoveFactor): how HARD movement is slowed DURING the swing window (AttackRootMs
    // long). [0,1]: 0 = full stop (the old hard root), 1 = no slow, 0.4 (the default) = move at 40% speed. Seeded
    // to the SHARED CombatTuning.DefaultSwingMoveFactor so the server and the client predictor (which falls back to
    // the same constant before its first snapshot) default identically. Read fresh per attack in HandleAttack and
    // replicated in CombatSnapshot; the predictor mirrors it off the snapshot for swing-slow parity.
    public double SwingMoveFactor { get; set; } = CombatTuning.DefaultSwingMoveFactor;

    public double FreeAimHalfAngleRadians => FreeAimHalfAngleDegrees * System.Math.PI / 180d;

    // Attack cooldown in TICKS, derived exactly like the old GameServer.AttackCooldownTicks (Ceiling, >= 1) so the
    // default value is unchanged and a live change stays tick-quantised the same way.
    public uint AttackCooldownTicks =>
        (uint)Math.Max(1, (int)Math.Ceiling(AttackCooldownMs / (1000d / _tickRate)));

    // Swing movement-root in TICKS, via the shared CombatTuning conversion off the LIVE AttackRootMs (so the server
    // and the client predictor compute the identical rootTicks from the same replicated rootMs — the parity point).
    public uint AttackRootTicks => CombatTuning.RootTicks(_tickRate, AttackRootMs);

    // The current combat knobs as the wire snapshot the server replicates to clients (login + on change).
    public CombatTuningSnapshot CombatSnapshot =>
        new(AttackCooldownMs, AttackRootMs, FreeAimHalfAngleDegrees, FreeAimRadiusTiles, AttackDamage, SwingMoveFactor);

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
