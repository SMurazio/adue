using System.Globalization;

namespace Mmo.Server.Runtime;

// S60 tuning registry: the table of which keys an admin may set live and how. Each entry knows how to
// clamp/validate an incoming double and apply it to the ServerTuning holder, returning the value actually
// stored (post-clamp) so the caller can log/echo the authoritative result. Adding a new live knob is one
// entry here + one field on ServerTuning + (optionally) one client field — that is the whole extension
// surface. Unknown keys are rejected here (TryApply returns false) and ignored+logged by the handler.
//
// SPEED1 (2026-06-21): the global base step cooldown is now PINNED — it is no longer a live knob (the
// move.stepCooldownMs key was removed) so an admin can't retune everyone's base walk speed mid-run. The
// base is a constant 150 ms (3 ticks at 20 Hz); per-entity /speed (SpeedMultiplier) still scales off it.
//
// Bounds mirror the startup ServerOptions.Validate() bounds so live values can never reach a state the
// server would have refused to boot with: interest radius (0, MaxInterestRadius]. No persistence — see ServerTuning.
public static class ServerTuningRegistry
{
    public const string InterestRadiusKey = "aoi.interestRadius";

    // COMBAT-TUNING (live): the free-aim combat feel-knobs an admin may retune live (replicated to clients via
    // CombatTuningSnapshot). Adding each was one entry here + one field on ServerTuning + one client mirror — the
    // documented extension surface. Clamps keep a typo from breaking resolution (e.g. a 0 radius that hits nothing,
    // or a multi-second cooldown that looks like the server stalled).
    public const string AttackCooldownMsKey = "combat.attackCooldownMs";
    public const string AttackRootMsKey = "combat.rootMs";
    public const string FreeAimHalfAngleDegKey = "combat.halfAngleDeg";
    public const string FreeAimRadiusTilesKey = "combat.radiusTiles";
    public const string AttackDamageKey = "combat.damage";

    // LIVING-ENEMIES P2-POLISH: the former global monster.* tuning keys (P1 roam + P2 aggro/chase/attack) were
    // REPLACED by PER-TYPE keys ("<typeId>.<field>", e.g. slime.roamRadius) owned by MonsterTypeRegistry, which also
    // REPLICATES them to clients (MonsterTuningSnapshot) for the F1 Monster tab. The per-type keys are applied via a
    // separate registry route in HandleAdminSetTuning, so they are NOT listed here.

    // Sane upper bound for a live AOI radius. The startup options only require > 0; here a live max guards
    // against an admin typo turning every AOI query into a near-world scan and stalling the tick loop.
    private const float MinInterestRadius = 1f;
    private const float MaxInterestRadius = 512f;

    // COMBAT-TUNING clamps. Wide enough to sweep feel, tight enough that a typo can't break resolution:
    //  attack cooldown 50..5000 ms (a watchable cadence; never sub-tick-trivial, never a multi-second "freeze");
    //  swing root      0..2000 ms (0 = no movement root; CombatTuning floors the derived ticks to >= 1 regardless);
    //  half-angle      1..180 deg (1° = a near-ray, 180° = a full-circle 360° arc);
    //  radius          0.25..16 tiles (point-blank-ish up to a long reach, still bounded by the AOI gather box);
    //  damage          0..10000 HP (0 = a harmless "tickle" test up to an instakill).
    private const int MinAttackCooldownMs = 50;
    private const int MaxAttackCooldownMs = 5000;
    private const int MinAttackRootMs = 0;
    private const int MaxAttackRootMs = 2000;
    private const double MinHalfAngleDeg = 1d;
    private const double MaxHalfAngleDeg = 180d;
    private const double MinRadiusTiles = 0.25d;
    private const double MaxRadiusTiles = 16d;
    private const int MinAttackDamage = 0;
    private const int MaxAttackDamage = 10000;

    // Applies a tuning key to the holder, clamping/validating first. Returns false for an unknown key (the
    // caller ignores + logs). On success, `applied` is the post-clamp value actually stored.
    public static bool TryApply(ServerTuning tuning, string key, double value, out double applied)
    {
        applied = 0d;
        if (!double.IsFinite(value))
        {
            return false;
        }

        switch (key)
        {
            case InterestRadiusKey:
            {
                var clamped = Math.Clamp((float)value, MinInterestRadius, MaxInterestRadius);
                tuning.InterestRadius = clamped;
                applied = clamped;
                return true;
            }
            case AttackCooldownMsKey:
            {
                var clamped = Math.Clamp((int)Math.Round(value), MinAttackCooldownMs, MaxAttackCooldownMs);
                tuning.AttackCooldownMs = clamped;
                applied = clamped;
                return true;
            }
            case AttackRootMsKey:
            {
                var clamped = Math.Clamp((int)Math.Round(value), MinAttackRootMs, MaxAttackRootMs);
                tuning.AttackRootMs = clamped;
                applied = clamped;
                return true;
            }
            case FreeAimHalfAngleDegKey:
            {
                var clamped = Math.Clamp(value, MinHalfAngleDeg, MaxHalfAngleDeg);
                tuning.FreeAimHalfAngleDegrees = clamped;
                applied = clamped;
                return true;
            }
            case FreeAimRadiusTilesKey:
            {
                var clamped = Math.Clamp(value, MinRadiusTiles, MaxRadiusTiles);
                tuning.FreeAimRadiusTiles = clamped;
                applied = clamped;
                return true;
            }
            case AttackDamageKey:
            {
                var clamped = Math.Clamp((int)Math.Round(value), MinAttackDamage, MaxAttackDamage);
                tuning.AttackDamage = clamped;
                applied = clamped;
                return true;
            }
            default:
                return false;
        }
    }

    public static bool IsKnownKey(string key) =>
        key is InterestRadiusKey || IsCombatKey(key);

    // COMBAT-TUNING: whether a key is one of the combat.* knobs. The GameServer broadcasts the replicated
    // CombatTuningSnapshot to all clients when (and only when) one of these changes, so the wedge/predictor/viz stay
    // in sync — an interest-radius change does not need a combat re-broadcast.
    public static bool IsCombatKey(string key) =>
        key is AttackCooldownMsKey or AttackRootMsKey or FreeAimHalfAngleDegKey or FreeAimRadiusTilesKey or AttackDamageKey;

    public static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
