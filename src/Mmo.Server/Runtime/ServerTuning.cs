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

    public double FreeAimHalfAngleRadians => FreeAimHalfAngleDegrees * System.Math.PI / 180d;

    // COMBAT-QOL: HEAVY HP regen rate for stationary enemy targets (dummies/NPCs), in HP PER SECOND. 50 HP/s heals a
    // full 100-HP dummy from empty in ~2 s, so a hit (number pops, bar drops) heals back fast and the dummy is a
    // permanent test target. Centralized here (not a magic number in the tick loop) and read each tick by
    // GameServer.RegenDummies; the per-tick amount is derived from the tick rate so the wall-clock heal speed is
    // independent of the configured tick rate. Damage events are NEVER emitted for regen — only real hits float a
    // number; the refilled HP rides the snapshot and the overhead bar fills on its own.
    public int EnemyRegenPerSecond { get; set; } = 50;

    // The per-tick regen amount derived from EnemyRegenPerSecond and the tick rate, rounded UP and floored at 1 so a
    // small per-second rate still heals at least 1 HP/tick (never a 0-HP no-op loop). At 50 HP/s and 20 Hz this is
    // 3 HP/tick (≈33 ticks / 1.65 s from empty — comfortably "heavy").
    public int EnemyRegenPerTick =>
        Math.Max(1, (int)Math.Ceiling(EnemyRegenPerSecond / (double)_tickRate));

    // Attack cooldown in TICKS, derived exactly like the old GameServer.AttackCooldownTicks (Ceiling, >= 1) so the
    // default value is unchanged and a live change stays tick-quantised the same way.
    public uint AttackCooldownTicks =>
        (uint)Math.Max(1, (int)Math.Ceiling(AttackCooldownMs / (1000d / _tickRate)));

    // Swing movement-root in TICKS, via the shared CombatTuning conversion off the LIVE AttackRootMs (so the server
    // and the client predictor compute the identical rootTicks from the same replicated rootMs — the parity point).
    public uint AttackRootTicks => CombatTuning.RootTicks(_tickRate, AttackRootMs);

    // The current combat knobs as the wire snapshot the server replicates to clients (login + on change).
    public CombatTuningSnapshot CombatSnapshot =>
        new(AttackCooldownMs, AttackRootMs, FreeAimHalfAngleDegrees, FreeAimRadiusTiles, AttackDamage);

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

    // LIVING-ENEMIES P1 (live): the leashed-roam feel-knobs for EntityKind.Monster, live-tunable via the
    // monster.* AdminSetTuning keys (clamped by ServerTuningRegistry) so an admin can dial roam range and
    // stroll cadence at runtime. Read fresh each AI pass by GameServer.StepMonsterAi / the MonsterRoamAi step.
    // These are SERVER-ONLY AI parameters (the client just interpolates the resulting tile-steps), so unlike
    // the combat.* knobs they have NO replicated client snapshot — there is nothing client-side to keep in sync.
    //
    //   RoamRadius   — Chebyshev tiles from a monster's home anchor it may wander within (the leash). Default 4.
    //   PauseMinMs   — minimum Idle pause between strolls (ms). Default 2000.
    //   PauseMaxMs   — maximum Idle pause between strolls (ms). Default 5000. (Registry keeps Max >= Min.)
    public int RoamRadius { get; set; } = 4;
    public int PauseMinMs { get; set; } = 2000;
    public int PauseMaxMs { get; set; } = 5000;

    // Roam pause bounds in TICKS, derived off the tick rate (Round, floored at 1) so the wall-clock pause is
    // independent of the configured tick rate and a small ms value still pauses at least one tick. The AI picks a
    // uniform random pause in [PauseMinTicks, PauseMaxTicks]; MonsterRoamAi re-floors Max>=Min defensively too.
    public uint PauseMinTicks =>
        (uint)Math.Max(1, (int)Math.Round(PauseMinMs / (1000d / _tickRate), MidpointRounding.AwayFromZero));

    public uint PauseMaxTicks =>
        (uint)Math.Max((int)PauseMinTicks, (int)Math.Round(PauseMaxMs / (1000d / _tickRate), MidpointRounding.AwayFromZero));
}
