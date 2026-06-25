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
        // Phase 0 (continuous migration): the DORMANT base move speed in tiles/sec, derived to reproduce today's
        // tile cadence (1 tile per StepCooldownMs ⇒ 1000/StepCooldownMs tiles/sec). Read by NOTHING in Phase 0 —
        // the cooldown path (StepCooldownTicks / SpeedMultiplier) still drives movement. Phase 1's integrator
        // switches the entity's SpeedUnitsPerSecond (= this × SpeedMultiplier) into the live mover.
        BaseMoveSpeedUnitsPerSecond = 1000d / StepCooldownMs;
    }

    // Phase 0 dormant: see the ctor. Settable so it can be exposed as a live knob (continuous.baseMoveSpeed),
    // but read by nothing until Phase 1. The base walk speed in tiles/sec.
    public double BaseMoveSpeedUnitsPerSecond { get; set; }

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

    // LIVING-ENEMIES P3: how long after the PLAYER's HP hits 0 the server waits before teleporting it back to spawn at
    // full HP (the brief "downed" window during which it can't act / take hits / die again). A single GLOBAL knob
    // (~2 s), live-tunable via the "player.respawnMs" key. Read at death time.
    public int PlayerRespawnMs { get; set; } = 2000;

    // Player respawn delay in TICKS (Round, floored at 0). Read by GameServer when scheduling the respawn so a live
    // retune applies to the next death.
    public uint PlayerRespawnTicks =>
        (uint)Math.Max(0, (int)Math.Round(PlayerRespawnMs / (1000d / _tickRate), MidpointRounding.AwayFromZero));

    // LOOT P4b: how long a dropped CORPSE lingers before it decays + despawns even if unlooted (UO-style ~minutes).
    // A single GLOBAL knob, live-tunable via the "loot.corpseDecayMs" key. Default ~3 min. Read at corpse-spawn time
    // (the death tick + this many ticks becomes the corpse's decay deadline), so a live retune applies to the NEXT
    // corpse — an already-spawned corpse keeps the deadline it was stamped with.
    public int CorpseDecayMs { get; set; } = 180000;

    // Corpse decay duration in TICKS (Round, floored at 1 so a corpse always lasts at least one tick). Stamped onto a
    // corpse at spawn as (serverTick + this).
    public uint CorpseDecayTicks =>
        (uint)Math.Max(1, (int)Math.Round(CorpseDecayMs / (1000d / _tickRate), MidpointRounding.AwayFromZero));

    // Base step cooldown in TICKS, derived exactly like ServerOptions.StepCooldownTicks so live changes
    // stay tick-quantised identically to the startup value (default value byte-for-byte unchanged).
    public uint StepCooldownTicks =>
        (uint)Math.Max(1, (int)Math.Ceiling(StepCooldownMs / (1000d / _tickRate)));

    // LIVING-ENEMIES P2-POLISH: the monster AI tuning (P1 roam + P2 aggro/chase/attack) moved OUT of here into the
    // per-TYPE MonsterTypeRegistry (a named template per monster kind — slime now), which owns the live-tunable +
    // REPLICATED per-type values and their tick-quantisation. ServerTuning no longer holds any monster.* knob.
}
