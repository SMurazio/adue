namespace Mmo.Server.Runtime;

// LIVING-ENEMIES P2-POLISH: a named monster TEMPLATE — its own stats + AI tuning, owned server-side. A spawned
// monster (EntityKind.Monster) remembers its type id; the AI reads its Tunables and its SpeedMultiplier FROM the
// type each tick, instead of a single global monster.* block. This is the seam that lets the world hold more than
// one kind of monster (slime now; more later) each with its own feel, all live-tunable.
//
// LIVE-TUNABLE: the per-type values are MUTABLE so an admin can retune one type at runtime via the AdminSetTuning
// path on PER-TYPE keys (e.g. "slime.roamRadius", "slime.aggroRadius"). The MonsterTypeRegistry owns the table of
// types + the apply/clamp logic (mirroring ServerTuningRegistry), and the per-type values are REPLICATED to clients
// (MonsterTuningSnapshot) so the F1 Monster tab can show + edit the authoritative numbers. They are read fresh each
// AI pass, so a live change takes effect on the next tick with no torn state (single-threaded tick loop).
//
// Defaults migrate the former global monster.* block verbatim (roam 4 / pause 2000-5000 / aggro 6 / leash 12 /
// attackRange 1 / attackDamage 10 / attackCooldown 1000), EXCEPT MoveSpeedMultiplier, which is the NEW slower-than-
// player default (0.8 → the slime steps ~312 ms vs the player's 250 ms base, so the dumb ones are outrunnable), and
// MaxHealth (100, the CharacterStats default, now an explicit per-type knob so a tankier type is one number away).
public sealed class MonsterType
{
    public MonsterType(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    // The wire/registry id (lowercase, stable) the AdminSetTuning per-type keys are built from ("slime" → "slime.*").
    public string Id { get; }

    // The human-facing name shown in the F1 Monster-tab dropdown ("Slime").
    public string DisplayName { get; }

    // Per-type max HP — the monster spawns at full of this. Default 100 (the CharacterStats default).
    public int MaxHealth { get; set; } = 100;

    // Per-type movement speed as a multiplier of the player's base cadence (SpeedMultiplier on the spawned entity).
    // < 1 = slower than the player (outrunnable), 1 = same, > 1 = faster. Default 0.8 (slower).
    public double MoveSpeedMultiplier { get; set; } = 0.8;

    // P1 roam knobs (migrated from the global monster.* defaults).
    public int RoamRadius { get; set; } = 4;
    public int PauseMinMs { get; set; } = 2000;
    public int PauseMaxMs { get; set; } = 5000;

    // P2 aggro/chase/attack knobs (migrated from the global monster.* defaults).
    public int AggroRadius { get; set; } = 6;
    public int ChaseLeash { get; set; } = 12;
    public int AttackRange { get; set; } = 1;
    public int AttackDamage { get; set; } = 10;
    public int AttackCooldownMs { get; set; } = 1000;

    // LIVING-ENEMIES P3: how long after a monster of this type DIES its spawner waits before spawning a fresh
    // full-HP one at the spawner tile. Default 5000 ms (~5 s). Live-tunable via the "<typeId>.respawnMs" key on the
    // F1 Monster tab; read live by the spawner at death time.
    public int RespawnMs { get; set; } = 5000;

    // LOOT P4a: the LootTableRegistry id this type rolls on death. Empty string = no loot (the explicit
    // "this type drops nothing" sentinel — distinct from an unknown id). STATIC seed data for now: it is
    // NOT live-tunable or replicated, unlike the AI knobs above, because it is content authored once, not a
    // feel dial an admin retunes mid-session. (If later loot wants live retuning — e.g. an event drop boost
    // — promote it to a per-type key like the others; flagged in the P4a review-request.)
    public string LootTableId { get; set; } = string.Empty;
}
