using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// LIVING-ENEMIES P2-POLISH: the table of monster TYPES (named templates) + the live-tuning apply/clamp logic for
// them. Mirrors ServerTuningRegistry, but the "holder" here is the per-type MonsterType objects (one per named
// template) rather than a single ServerTuning. Seeds ONE type today (id "slime", display "Slime"); the design is
// kept extensible — a new type is one Add() here + (optionally) its own non-default values.
//
// PER-TYPE LIVE KEYS: an admin retunes a type via AdminSetTuning on keys of the form "<typeId>.<field>", e.g.
// "slime.roamRadius", "slime.aggroRadius", "slime.moveSpeed", "slime.maxHealth". TryApply parses the typeId prefix,
// resolves the type, clamps the field, and applies it — returning false (caller ignores+logs) for an unknown type
// or field. The per-type tuning REPLACES the former global monster.* block (those keys were removed).
//
// TICK QUANTISATION: the AI consumes a MonsterRoamAi.Tunables in TICKS; this registry owns the tick rate and derives
// the tick-quantised values (pause bounds, attack cooldown, aggro-scan cadence) + the derived de-aggro hysteresis,
// exactly as the old ServerTuning did, so the migrated defaults are byte-for-byte unchanged.
//
// REPLICATION: BuildSnapshot() produces the wire MonsterTuningSnapshot (the per-type MS/tile values the client's F1
// Monster tab shows + edits). Replicated on login + whenever any per-type key changes (like CombatTuningSnapshot).
public sealed class MonsterTypeRegistry
{
    public const string DefaultTypeId = "slime";

    // Per-type clamps — wide enough to tune feel, tight enough a typo can't break the AI (mirrors the old global
    // monster.* clamps in ServerTuningRegistry, plus the new moveSpeed + maxHealth bounds).
    // CONTINUOUS: roam/aggro/leash/attack are world-unit RANGES (fractional doubles), not integer tiles.
    private const double MinRoamRadius = 0.5d;
    private const double MaxRoamRadius = 32d;
    private const int MinPauseMs = 0;
    private const int MaxPauseMs = 60000;
    private const double MinAggroRadius = 0.5d;
    private const double MaxAggroRadius = 64d;
    private const double MinChaseLeash = 0.5d;
    private const double MaxChaseLeash = 128d;
    private const double MinAttackRangeUnits = 0.5d;
    private const double MaxAttackRangeUnits = 8d;
    private const int MinAttackDamage = 0;
    private const int MaxAttackDamage = 10000;
    private const int MinAttackCooldownMs = 100;
    private const int MaxAttackCooldownMs = 10000;
    // LIVING-ENEMIES P3 respawn delay bounds: 0 (instant) up to 5 min, wide enough to tune feel.
    private const int MinRespawnMs = 0;
    private const int MaxRespawnMs = 300000;
    // moveSpeed 0.1..5x — a near-crawl up to 5x the player base; the per-entity cadence is further clamped by the
    // server's EffectiveStepCooldown ms bounds, so an extreme value can never break the tick loop.
    private const double MinMoveSpeed = 0.1d;
    private const double MaxMoveSpeed = 5d;
    private const int MinMaxHealth = 1;
    private const int MaxMaxHealth = 100000;
    // DATA-DRIVEN hop knobs (exposed on the F1 Monster tab). Distance/height are tile-unit doubles; airborne is ms.
    // Modest bounds: distance up to 8 tiles, height up to 4 tiles, airborne 50 ms..2 s (kept < a typical cadence).
    private const double MinHopDistance = 0.25d;
    private const double MaxHopDistance = 8d;
    private const double MinHopHeight = 0d;
    private const double MaxHopHeight = 4d;
    private const int MinHopAirborneMs = 50;
    private const int MaxHopAirborneMs = 2000;

    // Per-type field suffixes (the part after "<typeId>."). Public so the client F1 tab + tests name the SAME keys.
    public const string RoamRadiusField = "roamRadius";
    public const string PauseMinMsField = "pauseMinMs";
    public const string PauseMaxMsField = "pauseMaxMs";
    public const string AggroRadiusField = "aggroRadius";
    public const string ChaseLeashField = "chaseLeash";
    public const string AttackRangeField = "attackRange";
    public const string AttackDamageField = "attackDamage";
    public const string AttackCooldownMsField = "attackCooldownMs";
    public const string MoveSpeedField = "moveSpeed";
    public const string MaxHealthField = "maxHealth";
    public const string RespawnMsField = "respawnMs";
    public const string HopDistanceField = "hopDistance";
    public const string HopHeightField = "hopHeight";
    public const string HopAirborneMsField = "hopAirborneMs";

    // DATA-DRIVEN tuning (v40): the SINGLE source of the per-type tunable knobs. Each descriptor names a field's wire
    // Key (the "<typeId>." suffix), its human Label (the F1 caption), a Getter that reads the CURRENT value off a
    // MonsterType, its clamp Min/Max (shown as a hint; TryApply clamps authoritatively), and whether it is an integer.
    // BuildSnapshot iterates this to ship the generic field list, and IsMonsterTypeKey recognizes exactly these keys —
    // so adding a knob is ONE descriptor entry here + one TryApply case + the MonsterType field. Order = F1 row order.
    private readonly record struct TunableDescriptor(
        string Key,
        string Label,
        Func<MonsterType, double> Getter,
        double Min,
        double Max,
        bool IsInteger);

    private static readonly TunableDescriptor[] Descriptors =
    {
        new(MaxHealthField, "hp (max)", t => t.MaxHealth, MinMaxHealth, MaxMaxHealth, true),
        new(MoveSpeedField, "move speed (x)", t => t.MoveSpeedMultiplier, MinMoveSpeed, MaxMoveSpeed, false),
        new(RoamRadiusField, "roam range", t => t.RoamRadius, MinRoamRadius, MaxRoamRadius, false),
        new(AggroRadiusField, "aggro range", t => t.AggroRadius, MinAggroRadius, MaxAggroRadius, false),
        new(ChaseLeashField, "chase leash", t => t.ChaseLeash, MinChaseLeash, MaxChaseLeash, false),
        new(AttackRangeField, "attack range", t => t.AttackRangeUnits, MinAttackRangeUnits, MaxAttackRangeUnits, false),
        new(AttackDamageField, "attack damage", t => t.AttackDamage, MinAttackDamage, MaxAttackDamage, true),
        new(AttackCooldownMsField, "attack cooldown (ms)", t => t.AttackCooldownMs, MinAttackCooldownMs, MaxAttackCooldownMs, true),
        new(PauseMinMsField, "pause min (ms)", t => t.PauseMinMs, MinPauseMs, MaxPauseMs, true),
        new(PauseMaxMsField, "pause max (ms)", t => t.PauseMaxMs, MinPauseMs, MaxPauseMs, true),
        new(RespawnMsField, "respawn (ms)", t => t.RespawnMs, MinRespawnMs, MaxRespawnMs, true),
        new(HopDistanceField, "hop distance", t => t.HopDistanceUnits, MinHopDistance, MaxHopDistance, false),
        new(HopHeightField, "hop height", t => t.HopHeightUnits, MinHopHeight, MaxHopHeight, false),
        new(HopAirborneMsField, "hop airborne (ms)", t => t.HopAirborneMs, MinHopAirborneMs, MaxHopAirborneMs, true),
    };

    // The recognized per-type field suffixes, derived ONCE from the descriptor list so IsMonsterTypeKey never drifts
    // from BuildSnapshot. Ordinal (case-sensitive) to match the TryApply switch + the exact wire keys.
    private static readonly HashSet<string> FieldKeys =
        new(Descriptors.Select(d => d.Key), StringComparer.Ordinal);

    private readonly int _tickRate;
    private readonly Dictionary<string, MonsterType> _types = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MonsterType> _ordered = [];

    public MonsterTypeRegistry(int tickRate)
    {
        _tickRate = tickRate;
        // Seed the one type today. A new type is one Add() + its non-default values.
        // LOOT P4a: the slime rolls the "slime_loot" table on death (gel floor + the shared rare tail +
        // its signature core). Static content; the LootTableRegistry owns the table definition.
        Add(new MonsterType(DefaultTypeId, "Slime") { LootTableId = "slime_loot" });
    }

    private void Add(MonsterType type)
    {
        _types[type.Id] = type;
        _ordered.Add(type);
    }

    // All types in registration order (the F1 dropdown + the replicated snapshot iterate this).
    public IReadOnlyList<MonsterType> Types => _ordered;

    // Resolves a type by id (case-insensitive). False for an unknown name.
    public bool TryGet(string id, out MonsterType type) => _types.TryGetValue(id, out type!);

    // The default type (slime) — used when /monster is given no name.
    public MonsterType Default => _types[DefaultTypeId];

    // Builds the AI tunables for a type, tick-quantised exactly like the old ServerTuning did (so the migrated
    // defaults are unchanged). De-aggro range is derived (×1.5 aggro, hysteresis); the aggro-scan cadence is derived
    // (~0.5 s) and is tick-rate-only (not per-type). Read fresh each AI pass so a live retune takes effect next tick.
    // CONTINUOUS: the navigation ranges are EUCLIDEAN world-unit FLOATS — the per-type knobs (RoamRadius, AggroRadius,
    // ChaseLeash, AttackRangeUnits) are now fractional doubles read DIRECTLY (no integer-tile authoring, no 1:1
    // conversion). De-aggro keeps the ×1.5-aggro (min +1) hysteresis as a continuous range.
    public MonsterRoamAi.Tunables BuildTunables(MonsterType type) => new(
        RoamRadius: type.RoamRadius,
        PauseMinTicks: MsToTicks(type.PauseMinMs),
        PauseMaxTicks: PauseMaxTicks(type),
        AggroRadius: type.AggroRadius,
        DeaggroRadius: DeaggroRadius(type),
        ChaseLeash: type.ChaseLeash,
        AttackRangeUnits: type.AttackRangeUnits,
        AttackDamage: type.AttackDamage,
        AttackCooldownTicks: CooldownMsToTicks(type.AttackCooldownMs),
        AggroScanIntervalTicks: AggroScanIntervalTicks);

    // ~0.5 s aggro-scan cadence in ticks (floored at 1) — tick-rate-only, throttling the spatial scan.
    public uint AggroScanIntervalTicks =>
        (uint)Math.Max(1, (int)Math.Round(_tickRate * 0.5d, MidpointRounding.AwayFromZero));

    // Pause bounds in ticks (Round, floored at 1) so the wall-clock pause is tick-rate-independent; Max>=Min.
    private uint MsToTicks(int ms) =>
        (uint)Math.Max(1, (int)Math.Round(ms / (1000d / _tickRate), MidpointRounding.AwayFromZero));

    private uint PauseMaxTicks(MonsterType type) =>
        (uint)Math.Max((int)MsToTicks(type.PauseMinMs), (int)MsToTicks(type.PauseMaxMs));

    // Attack cooldown in ticks (Ceiling, >= 1) so a small ms value still gates at least one tick.
    private uint CooldownMsToTicks(int ms) =>
        (uint)Math.Max(1, (int)Math.Ceiling(ms / (1000d / _tickRate)));

    // Derived de-aggro RANGE (continuous world-units): 1.5× the acquire radius, at least +1 beyond it — the
    // hysteresis margin so a target must get meaningfully closer to re-aggro than it did to drop. Fractional now.
    private static double DeaggroRadius(MonsterType type) =>
        Math.Max(type.AggroRadius + 1d, type.AggroRadius * 1.5d);

    // Applies a per-type tuning key ("<typeId>.<field>") to its MonsterType, clamping first. Returns false for an
    // unknown type or field (caller ignores + logs); on success `applied` is the post-clamp value actually stored.
    public bool TryApply(string key, double value, out double applied)
    {
        applied = 0d;
        if (!double.IsFinite(value))
        {
            return false;
        }

        var dot = key.IndexOf('.');
        if (dot <= 0 || dot >= key.Length - 1)
        {
            return false;
        }

        var typeId = key[..dot];
        var field = key[(dot + 1)..];
        if (!_types.TryGetValue(typeId, out var type))
        {
            return false;
        }

        switch (field)
        {
            case RoamRadiusField:
                type.RoamRadius = ClampDouble(value, MinRoamRadius, MaxRoamRadius, out applied);
                return true;
            case PauseMinMsField:
                type.PauseMinMs = ClampInt(value, MinPauseMs, MaxPauseMs, out applied);
                if (type.PauseMaxMs < type.PauseMinMs)
                {
                    type.PauseMaxMs = type.PauseMinMs;
                }

                return true;
            case PauseMaxMsField:
                type.PauseMaxMs = ClampInt(value, MinPauseMs, MaxPauseMs, out applied);
                if (type.PauseMinMs > type.PauseMaxMs)
                {
                    type.PauseMinMs = type.PauseMaxMs;
                }

                return true;
            case AggroRadiusField:
                type.AggroRadius = ClampDouble(value, MinAggroRadius, MaxAggroRadius, out applied);
                return true;
            case ChaseLeashField:
                type.ChaseLeash = ClampDouble(value, MinChaseLeash, MaxChaseLeash, out applied);
                return true;
            case AttackRangeField:
                // CONTINUOUS: "attack range" edits the world-unit AttackRangeUnits the AI actually reads (the former
                // integer-tile AttackRange knob, which the AI never read, is retired).
                type.AttackRangeUnits = ClampDouble(value, MinAttackRangeUnits, MaxAttackRangeUnits, out applied);
                return true;
            case AttackDamageField:
                type.AttackDamage = ClampInt(value, MinAttackDamage, MaxAttackDamage, out applied);
                return true;
            case AttackCooldownMsField:
                type.AttackCooldownMs = ClampInt(value, MinAttackCooldownMs, MaxAttackCooldownMs, out applied);
                return true;
            case MaxHealthField:
                type.MaxHealth = ClampInt(value, MinMaxHealth, MaxMaxHealth, out applied);
                return true;
            case RespawnMsField:
                type.RespawnMs = ClampInt(value, MinRespawnMs, MaxRespawnMs, out applied);
                return true;
            case MoveSpeedField:
                type.MoveSpeedMultiplier = ClampDouble(value, MinMoveSpeed, MaxMoveSpeed, out applied);
                return true;
            case HopDistanceField:
                type.HopDistanceUnits = ClampDouble(value, MinHopDistance, MaxHopDistance, out applied);
                return true;
            case HopHeightField:
                type.HopHeightUnits = ClampDouble(value, MinHopHeight, MaxHopHeight, out applied);
                return true;
            case HopAirborneMsField:
                type.HopAirborneMs = ClampInt(value, MinHopAirborneMs, MaxHopAirborneMs, out applied);
                return true;
            default:
                return false;
        }
    }

    // True iff the key parses as a known "<typeId>.<field>" — the GameServer broadcasts the replicated
    // MonsterTuningSnapshot when (and only when) one of these changes, like IsCombatKey for combat.*.
    public bool IsMonsterTypeKey(string key)
    {
        var dot = key.IndexOf('.');
        if (dot <= 0 || dot >= key.Length - 1)
        {
            return false;
        }

        if (!_types.ContainsKey(key[..dot]))
        {
            return false;
        }

        // Recognized fields are derived from the descriptor list (the single source) so this can never drift from the
        // replicated snapshot — a new descriptor is auto-recognized; only its TryApply case must be added by hand.
        return FieldKeys.Contains(key[(dot + 1)..]);
    }

    // LIVING-ENEMIES P3: this type's respawn delay in TICKS (Round, floored at 0 — instant respawn is allowed). Read
    // by the spawner at death time so a live retune applies to the NEXT death.
    public uint RespawnTicks(MonsterType type) =>
        (uint)Math.Max(0, (int)Math.Round(type.RespawnMs / (1000d / _tickRate), MidpointRounding.AwayFromZero));

    // DATA-DRIVEN tuning (the "hops too often" fix): this type's per-hop AIRBORNE span in TICKS, derived from its live
    // HopAirborneMs (Round, floored at 1 tick — same MsToTicks the pause bounds use). GameServer.BeginMonsterHop feeds
    // this as the ballistic Jump's DurationTicks so the hop is a SHORT arc and the slime rests the rest of the cadence.
    public uint HopAirborneTicks(MonsterType type) => MsToTicks(type.HopAirborneMs);

    // The current per-type tuning as the wire snapshot the server replicates (login + on change). DATA-DRIVEN: each
    // type ships the GENERIC field list built from the descriptor table (current value via the getter, bounds from the
    // descriptor), so the F1 tab renders + edits the authoritative numbers without per-field code.
    public MonsterTuningSnapshot BuildSnapshot()
    {
        var entries = new MonsterTypeSnapshot[_ordered.Count];
        for (var i = 0; i < _ordered.Count; i++)
        {
            var t = _ordered[i];
            var fields = new MonsterTuningField[Descriptors.Length];
            for (var f = 0; f < Descriptors.Length; f++)
            {
                var d = Descriptors[f];
                fields[f] = new MonsterTuningField(d.Key, d.Label, d.Getter(t), d.Min, d.Max, d.IsInteger);
            }

            entries[i] = new MonsterTypeSnapshot(t.Id, t.DisplayName, fields);
        }

        return new MonsterTuningSnapshot(entries);
    }

    private static int ClampInt(double value, int min, int max, out double applied)
    {
        var clamped = Math.Clamp((int)Math.Round(value), min, max);
        applied = clamped;
        return clamped;
    }

    private static double ClampDouble(double value, double min, double max, out double applied)
    {
        var clamped = Math.Clamp(value, min, max);
        applied = clamped;
        return clamped;
    }
}
