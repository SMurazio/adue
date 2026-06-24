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
    private const int MinRoamRadius = 1;
    private const int MaxRoamRadius = 32;
    private const int MinPauseMs = 0;
    private const int MaxPauseMs = 60000;
    private const int MinAggroRadius = 1;
    private const int MaxAggroRadius = 64;
    private const int MinChaseLeash = 1;
    private const int MaxChaseLeash = 128;
    private const int MinAttackRange = 1;
    private const int MaxAttackRange = 4;
    private const int MinAttackDamage = 0;
    private const int MaxAttackDamage = 10000;
    private const int MinAttackCooldownMs = 100;
    private const int MaxAttackCooldownMs = 10000;
    // moveSpeed 0.1..5x — a near-crawl up to 5x the player base; the per-entity cadence is further clamped by the
    // server's EffectiveStepCooldown ms bounds, so an extreme value can never break the tick loop.
    private const double MinMoveSpeed = 0.1d;
    private const double MaxMoveSpeed = 5d;
    private const int MinMaxHealth = 1;
    private const int MaxMaxHealth = 100000;

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

    private readonly int _tickRate;
    private readonly Dictionary<string, MonsterType> _types = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MonsterType> _ordered = [];

    public MonsterTypeRegistry(int tickRate)
    {
        _tickRate = tickRate;
        // Seed the one type today. A new type is one Add() + its non-default values.
        Add(new MonsterType(DefaultTypeId, "Slime"));
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
    public MonsterRoamAi.Tunables BuildTunables(MonsterType type) => new(
        RoamRadius: type.RoamRadius,
        PauseMinTicks: MsToTicks(type.PauseMinMs),
        PauseMaxTicks: PauseMaxTicks(type),
        AggroRadius: type.AggroRadius,
        DeaggroRadius: DeaggroRadius(type),
        ChaseLeash: type.ChaseLeash,
        AttackRange: type.AttackRange,
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

    // Derived de-aggro range: 1.5× the acquire radius (ceil, always strictly beyond acquire) — the hysteresis margin.
    private static int DeaggroRadius(MonsterType type) =>
        Math.Max(type.AggroRadius + 1, (int)Math.Ceiling(type.AggroRadius * 1.5));

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
                type.RoamRadius = ClampInt(value, MinRoamRadius, MaxRoamRadius, out applied);
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
                type.AggroRadius = ClampInt(value, MinAggroRadius, MaxAggroRadius, out applied);
                return true;
            case ChaseLeashField:
                type.ChaseLeash = ClampInt(value, MinChaseLeash, MaxChaseLeash, out applied);
                return true;
            case AttackRangeField:
                type.AttackRange = ClampInt(value, MinAttackRange, MaxAttackRange, out applied);
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
            case MoveSpeedField:
            {
                var clamped = Math.Clamp(value, MinMoveSpeed, MaxMoveSpeed);
                type.MoveSpeedMultiplier = clamped;
                applied = clamped;
                return true;
            }
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

        return key[(dot + 1)..] is RoamRadiusField or PauseMinMsField or PauseMaxMsField or AggroRadiusField
            or ChaseLeashField or AttackRangeField or AttackDamageField or AttackCooldownMsField
            or MoveSpeedField or MaxHealthField;
    }

    // The current per-type tuning as the wire snapshot the server replicates (login + on change). Ints/doubles in
    // the SAME ms/tile units as the registry keys, so the client F1 tab shows + edits the authoritative numbers.
    public MonsterTuningSnapshot BuildSnapshot()
    {
        var entries = new MonsterTypeSnapshot[_ordered.Count];
        for (var i = 0; i < _ordered.Count; i++)
        {
            var t = _ordered[i];
            entries[i] = new MonsterTypeSnapshot(
                t.Id,
                t.DisplayName,
                t.MaxHealth,
                t.MoveSpeedMultiplier,
                t.RoamRadius,
                t.PauseMinMs,
                t.PauseMaxMs,
                t.AggroRadius,
                t.ChaseLeash,
                t.AttackRange,
                t.AttackDamage,
                t.AttackCooldownMs);
        }

        return new MonsterTuningSnapshot(entries);
    }

    private static int ClampInt(double value, int min, int max, out double applied)
    {
        var clamped = Math.Clamp((int)Math.Round(value), min, max);
        applied = clamped;
        return clamped;
    }
}
