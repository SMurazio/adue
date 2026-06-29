using System.Text.Json;
using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// LIVING-ENEMIES P2-POLISH: the table of monster TYPES (named templates) + the live-tuning apply/clamp logic for
// them. Mirrors ServerTuningRegistry, but the "holder" here is the per-type MonsterType objects (one per named
// template) rather than a single ServerTuning. Seeds ONE type today (id "slime", display "Slime"); the design is
// kept extensible — a new type is one Add() here + (optionally) its own non-default values.
//
// PER-TYPE LIVE KEYS: an admin retunes a type via AdminSetTuning on keys of the form "<typeId>.<field>", e.g.
// "slime.roamRadius", "slime.aggroRadius", "slime.hopDelayMs", "slime.maxHealth". TryApply parses the typeId prefix,
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
    // monster.* clamps in ServerTuningRegistry, plus the maxHealth + hop-knob bounds).
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
    private const int MinMaxHealth = 1;
    private const int MaxMaxHealth = 100000;
    // DATA-DRIVEN hop knobs (exposed on the F1 Monster tab). Distance/height are tile-unit doubles; airborne/delay are ms.
    // Modest bounds: distance up to 8 tiles, height up to 4 tiles, airborne 50 ms..2 s, delay 0..5 s (0 = no rest).
    private const double MinHopDistance = 0.25d;
    private const double MaxHopDistance = 8d;
    private const double MinHopHeight = 0d;
    private const double MaxHopHeight = 4d;
    private const int MinHopAirborneMs = 50;
    private const int MaxHopAirborneMs = 2000;
    private const int MinHopDelayMs = 0;
    private const int MaxHopDelayMs = 5000;

    // Per-type field suffixes (the part after "<typeId>."). Public so the client F1 tab + tests name the SAME keys.
    public const string RoamRadiusField = "roamRadius";
    public const string PauseMinMsField = "pauseMinMs";
    public const string PauseMaxMsField = "pauseMaxMs";
    public const string AggroRadiusField = "aggroRadius";
    public const string ChaseLeashField = "chaseLeash";
    public const string AttackRangeField = "attackRange";
    public const string AttackDamageField = "attackDamage";
    public const string AttackCooldownMsField = "attackCooldownMs";
    public const string MaxHealthField = "maxHealth";
    public const string RespawnMsField = "respawnMs";
    public const string HopDistanceField = "hopDistance";
    public const string HopHeightField = "hopHeight";
    public const string HopAirborneMsField = "hopAirborneMs";
    public const string HopDelayMsField = "hopDelayMs";

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
        // SLIME-FEEL-POLISH: the intuitive slime hop feel-knobs lead (RANGE / HEIGHT / AIRBORNE / DELAY) — the order
        // here IS the F1 row order. The opaque "move speed (x)" knob is retired (interp-cadence-only now, not shown).
        new(MaxHealthField, "hp (max)", t => t.MaxHealth, MinMaxHealth, MaxMaxHealth, true),
        new(HopDistanceField, "hop distance", t => t.HopDistanceUnits, MinHopDistance, MaxHopDistance, false),
        new(HopHeightField, "hop height", t => t.HopHeightUnits, MinHopHeight, MaxHopHeight, false),
        new(HopAirborneMsField, "hop airborne (ms)", t => t.HopAirborneMs, MinHopAirborneMs, MaxHopAirborneMs, true),
        new(HopDelayMsField, "hop delay (ms)", t => t.HopDelayMs, MinHopDelayMs, MaxHopDelayMs, true),
        new(RoamRadiusField, "roam range", t => t.RoamRadius, MinRoamRadius, MaxRoamRadius, false),
        new(AggroRadiusField, "aggro range", t => t.AggroRadius, MinAggroRadius, MaxAggroRadius, false),
        new(ChaseLeashField, "chase leash", t => t.ChaseLeash, MinChaseLeash, MaxChaseLeash, false),
        new(AttackRangeField, "attack range", t => t.AttackRangeUnits, MinAttackRangeUnits, MaxAttackRangeUnits, false),
        new(AttackDamageField, "attack damage", t => t.AttackDamage, MinAttackDamage, MaxAttackDamage, true),
        new(AttackCooldownMsField, "attack cooldown (ms)", t => t.AttackCooldownMs, MinAttackCooldownMs, MaxAttackCooldownMs, true),
        new(PauseMinMsField, "pause min (ms)", t => t.PauseMinMs, MinPauseMs, MaxPauseMs, true),
        new(PauseMaxMsField, "pause max (ms)", t => t.PauseMaxMs, MinPauseMs, MaxPauseMs, true),
        new(RespawnMsField, "respawn (ms)", t => t.RespawnMs, MinRespawnMs, MaxRespawnMs, true),
    };

    // The recognized per-type field suffixes, derived ONCE from the descriptor list so IsMonsterTypeKey never drifts
    // from BuildSnapshot. Ordinal (case-sensitive) to match the TryApply switch + the exact wire keys.
    private static readonly HashSet<string> FieldKeys =
        new(Descriptors.Select(d => d.Key), StringComparer.Ordinal);

    private readonly int _tickRate;
    private readonly Dictionary<string, MonsterType> _types = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MonsterType> _ordered = [];

    // The code-seeded ctor — the FALLBACK + test-seed. Seeds the one slime type in code so the server still
    // runs (and every existing test passes unchanged) when no data manifest is present. The shipped
    // Content/monsters.json is the AUTHORITATIVE runtime source (see FromManifestJson + GameServer); a parity
    // test pins that the data file and this code seed never drift.
    public MonsterTypeRegistry(int tickRate)
        : this(tickRate, seed: true)
    {
    }

    // Shared private ctor: `seed` gates the code default so FromManifestJson can build an EMPTY registry and
    // populate it purely from data (no code-seeded slime to dedupe against).
    private MonsterTypeRegistry(int tickRate, bool seed)
    {
        _tickRate = tickRate;
        if (seed)
        {
            // Seed the one type today. A new type is one Add() + its non-default values.
            // LOOT P4a: the slime rolls the "slime_loot" table on death (gel floor + the shared rare tail +
            // its signature core). Static content; the LootTableRegistry owns the table definition.
            Add(new MonsterType(DefaultTypeId, "Slime") { LootTableId = "slime_loot" });
        }
    }

    // P0 (monster-behavior architecture, docs/monster-behavior-design.md): build the registry from a JSON DATA
    // MANIFEST instead of the code seed, so monster TYPES are authored/edited in data with no code build. Loads
    // the SAME shape the shipped Content/monsters.json carries. Validation: a non-empty `id` + `displayName` are
    // required; duplicate ids are rejected; an empty/malformed manifest (or one with no types) throws a clear
    // ArgumentException; every OPTIONAL tunable that is omitted falls back to the MonsterType field default, and
    // every provided tunable is CLAMPED through the exact same TryApply bounds the live F1 tuning uses (single
    // source of truth — the data file cannot author an out-of-range monster). P0 SCHEMA = the current
    // MonsterType fields only; the composition selectors (locomotion/behavior/ability/visual ids) come in P1+.
    public static MonsterTypeRegistry FromManifestJson(int tickRate, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Monster manifest is empty.", nameof(json));
        }

        MonsterManifestDto? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<MonsterManifestDto>(json, ManifestJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Monster manifest is not valid JSON: {ex.Message}", nameof(json), ex);
        }

        if (manifest?.Types is null || manifest.Types.Count == 0)
        {
            throw new ArgumentException("Monster manifest has no types.", nameof(json));
        }

        var registry = new MonsterTypeRegistry(tickRate, seed: false);
        foreach (var dto in manifest.Types)
        {
            if (dto is null)
            {
                throw new ArgumentException("Monster manifest contains a null type entry.", nameof(json));
            }

            if (string.IsNullOrWhiteSpace(dto.Id))
            {
                throw new ArgumentException("Monster manifest type is missing a non-empty 'id'.", nameof(json));
            }

            if (string.IsNullOrWhiteSpace(dto.DisplayName))
            {
                throw new ArgumentException(
                    $"Monster type '{dto.Id}' is missing a non-empty 'displayName'.", nameof(json));
            }

            if (registry._types.ContainsKey(dto.Id))
            {
                throw new ArgumentException(
                    $"Monster manifest has a duplicate type id '{dto.Id}'.", nameof(json));
            }

            var type = new MonsterType(dto.Id, dto.DisplayName);
            // Non-tunable static content + the interp-cadence multiplier: set directly (no TryApply key). Omitted
            // → the MonsterType field default (LootTableId "" = drops nothing; MoveSpeedMultiplier 0.6).
            if (dto.LootTableId is not null)
            {
                type.LootTableId = dto.LootTableId;
            }

            if (dto.MoveSpeedMultiplier.HasValue)
            {
                type.MoveSpeedMultiplier = dto.MoveSpeedMultiplier.Value;
            }

            registry.Add(type);

            // Each provided tunable is clamped + applied through TryApply (the SAME bounds the F1 live tuning
            // uses); an omitted one keeps the field default. Note attackRange's wire key vs the AttackRangeUnits
            // JSON/field name. Pause min is applied before max so the non-inversion guard resolves like the F1 path.
            void Apply(string field, double? value)
            {
                if (value.HasValue)
                {
                    registry.TryApply($"{type.Id}.{field}", value.Value, out _);
                }
            }

            Apply(MaxHealthField, dto.MaxHealth);
            Apply(RoamRadiusField, dto.RoamRadius);
            Apply(PauseMinMsField, dto.PauseMinMs);
            Apply(PauseMaxMsField, dto.PauseMaxMs);
            Apply(AggroRadiusField, dto.AggroRadius);
            Apply(ChaseLeashField, dto.ChaseLeash);
            Apply(AttackRangeField, dto.AttackRangeUnits);
            Apply(AttackDamageField, dto.AttackDamage);
            Apply(AttackCooldownMsField, dto.AttackCooldownMs);
            Apply(RespawnMsField, dto.RespawnMs);
            Apply(HopDistanceField, dto.HopDistanceUnits);
            Apply(HopHeightField, dto.HopHeightUnits);
            Apply(HopAirborneMsField, dto.HopAirborneMs);
            Apply(HopDelayMsField, dto.HopDelayMs);
        }

        return registry;
    }

    // Tolerant of camelCase casing, `//` comments (the manifest is annotated), and trailing commas — but STRICT on
    // unknown members (P0 review): a typo'd field ("maxHelth") or an unsupported field would otherwise be silently
    // dropped and the monster keep the default — an invisible data-authoring trap for the first content loader.
    // Disallow makes it throw (→ caught in FromManifestJson → ArgumentException → GameServer's loud code-seed
    // fallback), so a typo fails LOUDLY + the server still starts on the default. (Discipline: a field added to the
    // JSON in a later phase must be added to the DTO in the same change.)
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    // The on-disk manifest shape. P0 = the CURRENT MonsterType fields only; later phases (P1+) GROW this with
    // the composition selectors (locomotionId / behaviorId / abilityIds / visualId). All tunables are nullable so
    // an omitted field falls back to the MonsterType default; id/displayName are validated as required in
    // FromManifestJson. JSON property names are camelCase (matched case-insensitively).
    private sealed record MonsterManifestDto(List<MonsterTypeDto?>? Types);

    private sealed record MonsterTypeDto(
        string? Id,
        string? DisplayName,
        string? LootTableId,
        int? MaxHealth,
        double? MoveSpeedMultiplier,
        double? RoamRadius,
        int? PauseMinMs,
        int? PauseMaxMs,
        double? AggroRadius,
        double? ChaseLeash,
        int? AttackDamage,
        int? AttackCooldownMs,
        double? AttackRangeUnits,
        double? HopDistanceUnits,
        double? HopHeightUnits,
        int? HopAirborneMs,
        int? HopDelayMs,
        int? RespawnMs);

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
    // The default type (used by /monster with no name). Prefer the canonical DefaultTypeId ("slime") when present,
    // else fall back to the FIRST registered type. P0 review (footgun): a DATA manifest may rename/omit "slime" — a
    // structurally-valid case the loader accepts — and the old `_types[DefaultTypeId]` threw KeyNotFoundException at
    // startup, defeating the "never crash, fall back" intent exactly when P1+ starts editing ids. The registry always
    // has >= 1 type (FromManifestJson rejects an empty manifest; the code seed always has slime), so _ordered[0] is
    // always valid. Keeps Default.Id == "slime" for the shipped/seed case (the manifest lists slime first).
    public MonsterType Default => _types.TryGetValue(DefaultTypeId, out var d) ? d : _ordered[0];

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
            case HopDistanceField:
                type.HopDistanceUnits = ClampDouble(value, MinHopDistance, MaxHopDistance, out applied);
                return true;
            case HopHeightField:
                type.HopHeightUnits = ClampDouble(value, MinHopHeight, MaxHopHeight, out applied);
                return true;
            case HopAirborneMsField:
                type.HopAirborneMs = ClampInt(value, MinHopAirborneMs, MaxHopAirborneMs, out applied);
                return true;
            case HopDelayMsField:
                type.HopDelayMs = ClampInt(value, MinHopDelayMs, MaxHopDelayMs, out applied);
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

    // SLIME-FEEL-POLISH: this type's grounded REST between hops in TICKS, derived from its live HopDelayMs (Round,
    // floored at 0 — a 0 ms delay means re-hop the instant it lands; same convention as RespawnTicks, NOT the floor-at-1
    // of MsToTicks). GameServer.StepMonsterAi feeds the monster's hop CADENCE as HopAirborneTicks + HopDelayTicks, so
    // this is the real, tunable idle pause the user asked for ("a delay between each jump").
    public uint HopDelayTicks(MonsterType type) =>
        (uint)Math.Max(0, (int)Math.Round(type.HopDelayMs / (1000d / _tickRate), MidpointRounding.AwayFromZero));

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
