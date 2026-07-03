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
// TICK QUANTISATION: the AI consumes a MonsterAiTunables in TICKS; this registry owns the tick rate and derives
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
    // MONSTER-BEHAVIOR P5 charge bounds (behavior-specific data, clamped on load like FleeHealthPct — NOT live F1
    // knobs). Cooldown 0 (no charge) .. 60 s; dash distance 0 (disabled) .. 16 units; trigger range 0 (disabled) .. 64
    // units (the aggro band). A nonsense author value is clamped, never honoured, so the data file cannot author an
    // insane charge. MIN 0 (not a positive floor): 0 is the legitimate value a NON-charger carries (ChargeEnabled gates
    // on the "charge" ability + a positive cooldown, not on distance), so a positive floor would corrupt a non-charger's
    // 0 to the floor on manifest load — breaking the Save→reload round-trip (Part B). A real charger authors 0.5..16.
    private const int MinChargeCooldownMs = 0;
    private const int MaxChargeCooldownMs = 60000;
    private const double MinChargeDistanceUnits = 0d;
    private const double MaxChargeDistanceUnits = 16d;
    private const double MinChargeTriggerRangeUnits = 0d;
    private const double MaxChargeTriggerRangeUnits = 64d;
    // TELEGRAPH T1 slam bounds (ability data, clamped on load + via TryApply like the charge trio). Cooldown 0 (no
    // slam) .. 60 s; radius 0 (disabled) .. 16 units; windup 0 .. 10 s (the ~1.5-3 s fairness floor of the design is
    // content tuning, T3 — a short windup stays legal for dev testing); damage 0 .. 10000 (the attack-damage bounds).
    // MIN 0 throughout for the same round-trip reason as the charge: 0 is the legitimate value every NON-slammer
    // carries, so a positive floor would corrupt it on manifest load.
    private const int MinSlamCooldownMs = 0;
    private const int MaxSlamCooldownMs = 60000;
    private const double MinSlamRadiusUnits = 0d;
    private const double MaxSlamRadiusUnits = 16d;
    private const int MinSlamWindupMs = 0;
    private const int MaxSlamWindupMs = 10000;
    private const int MinSlamDamage = 0;
    private const int MaxSlamDamage = 10000;
    // MONSTER-BEHAVIOR P6 render-scale bounds: 0.25× (a quarter size) .. 4× (clearly large). A nonsense author value
    // is clamped, never honoured, so the data file cannot author an invisible or world-filling placeholder visual.
    private const double MinRenderScale = 0.25d;
    private const double MaxRenderScale = 4.0d;
    // CONTEXTUAL-KNOBS: walk-speed (MoveSpeedMultiplier) live bounds — exposed as a GLIDER-only F1 knob (for a glider it
    // IS the walk speed: it seeds SpeedUnitsPerSecond, read each tick by GlideLocomotion). 0.1× (a crawl) .. 3.0× (fast).
    private const double MinMoveSpeedMultiplier = 0.1d;
    private const double MaxMoveSpeedMultiplier = 3.0d;
    // CONTEXTUAL-KNOBS: wounded-flee threshold (a SkirmisherBehavior knob) live bounds — a FRACTION of MaxHealth in
    // [0,1], the SAME bounds FromManifestJson clamps the authored value to (0 = never flee).
    private const double MinFleeHealthPct = 0d;
    private const double MaxFleeHealthPct = 1d;

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
    // CONTEXTUAL-KNOBS: the per-composition field suffixes — exposed only on the types whose composition uses them
    // (see the descriptor AppliesTo predicates). MoveSpeed is glide-only; FleeHealthPct is skirmisher-only; the charge
    // trio is ability-"charge"-only. (FleeHealthPct + the charge fields were formerly manifest-only behavior DATA.)
    public const string MoveSpeedField = "moveSpeed";
    public const string FleeHealthPctField = "fleeHealthPct";
    public const string ChargeCooldownMsField = "chargeCooldownMs";
    public const string ChargeDistanceUnitsField = "chargeDistanceUnits";
    public const string ChargeTriggerRangeUnitsField = "chargeTriggerRangeUnits";
    // TELEGRAPH T1: the slam ability quartet — exposed only on types whose AbilityIds compose "slam" (see HasSlam).
    public const string SlamRadiusUnitsField = "slamRadiusUnits";
    public const string SlamWindupMsField = "slamWindupMs";
    public const string SlamDamageField = "slamDamage";
    public const string SlamCooldownMsField = "slamCooldownMs";

    // CONTEXTUAL-KNOBS: composition-applicability predicates a descriptor's AppliesTo points at. A type's
    // LocomotionId / BehaviorId / AbilityIds decide which knobs its F1 tab shows — so a glider never shows hop knobs and
    // a hopper never shows walk-speed/flee/charge. Locomotion/behavior are matched case-insensitively (they round-trip
    // as authored strings); the charge gate mirrors ChargeEnabled's case-insensitive AbilityIds.Contains.
    private static readonly Func<MonsterType, bool> Always = static _ => true;
    private static readonly Func<MonsterType, bool> IsHopper =
        static t => string.Equals(t.LocomotionId, "hop", StringComparison.OrdinalIgnoreCase);
    private static readonly Func<MonsterType, bool> IsGlider =
        static t => string.Equals(t.LocomotionId, "glide", StringComparison.OrdinalIgnoreCase);
    private static readonly Func<MonsterType, bool> IsSkirmisher =
        static t => string.Equals(t.BehaviorId, "skirmisher", StringComparison.OrdinalIgnoreCase);
    private static readonly Func<MonsterType, bool> HasCharge =
        static t => t.AbilityIds.Contains("charge", StringComparer.OrdinalIgnoreCase);
    private static readonly Func<MonsterType, bool> HasSlam =
        static t => t.AbilityIds.Contains("slam", StringComparer.OrdinalIgnoreCase);

    // DATA-DRIVEN tuning (v40): the SINGLE source of the per-type tunable knobs. Each descriptor names a field's wire
    // Key (the "<typeId>." suffix), its human Label (the F1 caption), a Getter that reads the CURRENT value off a
    // MonsterType, its clamp Min/Max (shown as a hint; TryApply clamps authoritatively), whether it is an integer, and
    // (CONTEXTUAL-KNOBS) an AppliesTo predicate that decides whether the knob shows on a given type's F1 tab. So adding
    // a knob is ONE descriptor entry here + one TryApply case + the MonsterType field. Order = F1 row order.
    private readonly record struct TunableDescriptor(
        string Key,
        string Label,
        Func<MonsterType, double> Getter,
        double Min,
        double Max,
        bool IsInteger,
        Func<MonsterType, bool> AppliesTo);

    // CONTEXTUAL-KNOBS: the F1 row order, GROUPED — common stats, then the per-locomotion knobs (walk speed for a
    // glider; the hop quartet for a hopper — mutually exclusive, so only one group ever shows), then the behavior knob
    // (flee, skirmisher-only), then the ability knobs (charge trio, "charge"-only). BuildSnapshot(type) ships ONLY the
    // descriptors whose AppliesTo(type) is true, so a glider (gnoll) shows walk speed + flee + charge and NO hop knobs,
    // while a hopper (slime) shows the hop quartet and NO walk-speed/flee/charge. IsMonsterTypeKey/FieldKeys derive from
    // the FULL list (recognition is type-independent; the display filter is BuildSnapshot's job).
    private static readonly TunableDescriptor[] Descriptors =
    {
        // COMMON — always shown.
        new(MaxHealthField, "hp (max)", t => t.MaxHealth, MinMaxHealth, MaxMaxHealth, true, Always),
        // LOCOMOTION — walk speed (glider) OR the hop quartet (hopper); one group per type.
        new(MoveSpeedField, "walk speed (x)", t => t.MoveSpeedMultiplier, MinMoveSpeedMultiplier, MaxMoveSpeedMultiplier, false, IsGlider),
        new(HopDistanceField, "hop distance", t => t.HopDistanceUnits, MinHopDistance, MaxHopDistance, false, IsHopper),
        new(HopHeightField, "hop height", t => t.HopHeightUnits, MinHopHeight, MaxHopHeight, false, IsHopper),
        new(HopAirborneMsField, "hop airborne (ms)", t => t.HopAirborneMs, MinHopAirborneMs, MaxHopAirborneMs, true, IsHopper),
        new(HopDelayMsField, "hop delay (ms)", t => t.HopDelayMs, MinHopDelayMs, MaxHopDelayMs, true, IsHopper),
        // COMMON nav/combat stats — always shown.
        new(RoamRadiusField, "roam range", t => t.RoamRadius, MinRoamRadius, MaxRoamRadius, false, Always),
        new(AggroRadiusField, "aggro range", t => t.AggroRadius, MinAggroRadius, MaxAggroRadius, false, Always),
        new(ChaseLeashField, "chase leash", t => t.ChaseLeash, MinChaseLeash, MaxChaseLeash, false, Always),
        new(AttackRangeField, "attack range", t => t.AttackRangeUnits, MinAttackRangeUnits, MaxAttackRangeUnits, false, Always),
        new(AttackDamageField, "attack damage", t => t.AttackDamage, MinAttackDamage, MaxAttackDamage, true, Always),
        new(AttackCooldownMsField, "attack cooldown (ms)", t => t.AttackCooldownMs, MinAttackCooldownMs, MaxAttackCooldownMs, true, Always),
        new(PauseMinMsField, "pause min (ms)", t => t.PauseMinMs, MinPauseMs, MaxPauseMs, true, Always),
        new(PauseMaxMsField, "pause max (ms)", t => t.PauseMaxMs, MinPauseMs, MaxPauseMs, true, Always),
        new(RespawnMsField, "respawn (ms)", t => t.RespawnMs, MinRespawnMs, MaxRespawnMs, true, Always),
        // BEHAVIOR — wounded-flee threshold, skirmisher-only.
        new(FleeHealthPctField, "flee health %", t => t.FleeHealthPct, MinFleeHealthPct, MaxFleeHealthPct, false, IsSkirmisher),
        // ABILITY — the charge trio, "charge"-composing types only.
        new(ChargeCooldownMsField, "charge cooldown (ms)", t => t.ChargeCooldownMs, MinChargeCooldownMs, MaxChargeCooldownMs, true, HasCharge),
        new(ChargeDistanceUnitsField, "charge distance", t => t.ChargeDistanceUnits, MinChargeDistanceUnits, MaxChargeDistanceUnits, false, HasCharge),
        new(ChargeTriggerRangeUnitsField, "charge trigger range", t => t.ChargeTriggerRangeUnits, MinChargeTriggerRangeUnits, MaxChargeTriggerRangeUnits, false, HasCharge),
        // ABILITY — the slam quartet (TELEGRAPH T1), "slam"-composing types only.
        new(SlamRadiusUnitsField, "slam radius", t => t.SlamRadiusUnits, MinSlamRadiusUnits, MaxSlamRadiusUnits, false, HasSlam),
        new(SlamWindupMsField, "slam windup (ms)", t => t.SlamWindupMs, MinSlamWindupMs, MaxSlamWindupMs, true, HasSlam),
        new(SlamDamageField, "slam damage", t => t.SlamDamage, MinSlamDamage, MaxSlamDamage, true, HasSlam),
        new(SlamCooldownMsField, "slam cooldown (ms)", t => t.SlamCooldownMs, MinSlamCooldownMs, MaxSlamCooldownMs, true, HasSlam),
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
            // TELEGRAPH T1: the slime composes the "slam" ability — its first real attack pattern (a circle
            // telegraph locked at the target's cast-time position, resolving after the windup). Values mirror the
            // shipped manifest byte-for-byte (the parity test pins that the two can never drift).
            Add(new MonsterType(DefaultTypeId, "Slime")
            {
                LootTableId = "slime_loot",
                AbilityIds = ["slam"],
                SlamRadiusUnits = 2.0,
                SlamWindupMs = 1500,
                SlamDamage = 15,
                SlamCooldownMs = 4000,
            });
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

            // MONSTER-BEHAVIOR P1: the locomotion composition selector. STORE the string as-authored (non-blank);
            // an omitted/blank id keeps the MonsterType default "hop". The loader does NOT validate/resolve it (an
            // unknown id like "glide" before P2 registers it is accepted here) — resolution + the loud-but-safe
            // fallback-to-hop is GameServer's job (ResolveLocomotion), mirroring how it already owns the type map.
            if (!string.IsNullOrWhiteSpace(dto.LocomotionId))
            {
                type.LocomotionId = dto.LocomotionId;
            }

            // MONSTER-BEHAVIOR P3: the behavior composition selector. STORE the string as-authored (non-blank); an
            // omitted/blank id keeps the MonsterType default "basicRoamer". Like LocomotionId, the loader does NOT
            // validate/resolve it (an unknown id is accepted verbatim) — resolution + the loud-but-safe fallback to
            // basicRoamer is GameServer's job (ResolveBehavior), mirroring how it already owns the type map.
            if (!string.IsNullOrWhiteSpace(dto.BehaviorId))
            {
                type.BehaviorId = dto.BehaviorId;
            }

            if (dto.MoveSpeedMultiplier.HasValue)
            {
                type.MoveSpeedMultiplier = dto.MoveSpeedMultiplier.Value;
            }

            // MONSTER-BEHAVIOR P4: the wounded-flee threshold (a SkirmisherBehavior knob). Set directly + clamp to
            // [0,1] (NOT a TryApply/F1 field — it is behavior-specific, not a live global tunable). Omitted → 0 (never
            // flee), the MonsterType default. An out-of-range author value is clamped, never honoured.
            if (dto.FleeHealthPct.HasValue)
            {
                type.FleeHealthPct = Math.Clamp(dto.FleeHealthPct.Value, 0d, 1d);
            }

            // MONSTER-BEHAVIOR P5: the ability composition set + the charge tuning. AbilityIds stores the authored ids
            // verbatim (the BEHAVIOR + ChargeEnabled gate which ones actually fire — an unknown id is simply inert);
            // omitted -> empty (no abilities). The charge numerics are set directly + clamped to sane bounds (NOT a
            // TryApply/F1 field — behavior-specific, like FleeHealthPct); omitted -> 0 (no charge), the field default.
            if (dto.AbilityIds is not null)
            {
                type.AbilityIds = dto.AbilityIds;
            }

            if (dto.ChargeCooldownMs.HasValue)
            {
                type.ChargeCooldownMs = Math.Clamp(dto.ChargeCooldownMs.Value, MinChargeCooldownMs, MaxChargeCooldownMs);
            }

            if (dto.ChargeDistanceUnits.HasValue)
            {
                type.ChargeDistanceUnits =
                    Math.Clamp(dto.ChargeDistanceUnits.Value, MinChargeDistanceUnits, MaxChargeDistanceUnits);
            }

            if (dto.ChargeTriggerRangeUnits.HasValue)
            {
                type.ChargeTriggerRangeUnits =
                    Math.Clamp(dto.ChargeTriggerRangeUnits.Value, MinChargeTriggerRangeUnits, MaxChargeTriggerRangeUnits);
            }

            // TELEGRAPH T1: the slam ability tuning — set directly + clamped like the charge trio above (behavior/
            // ability-specific data with contextual F1 exposure); omitted -> 0 (no slam), the field default.
            if (dto.SlamCooldownMs.HasValue)
            {
                type.SlamCooldownMs = Math.Clamp(dto.SlamCooldownMs.Value, MinSlamCooldownMs, MaxSlamCooldownMs);
            }

            if (dto.SlamRadiusUnits.HasValue)
            {
                type.SlamRadiusUnits = Math.Clamp(dto.SlamRadiusUnits.Value, MinSlamRadiusUnits, MaxSlamRadiusUnits);
            }

            if (dto.SlamWindupMs.HasValue)
            {
                type.SlamWindupMs = Math.Clamp(dto.SlamWindupMs.Value, MinSlamWindupMs, MaxSlamWindupMs);
            }

            if (dto.SlamDamage.HasValue)
            {
                type.SlamDamage = Math.Clamp(dto.SlamDamage.Value, MinSlamDamage, MaxSlamDamage);
            }

            // MONSTER-BEHAVIOR P6: the placeholder per-type VISUAL. RenderTint is authored as a friendly "#RRGGBB" hex
            // string, parsed to a packed 0xRRGGBB uint (an omitted/blank/malformed value → white 0xFFFFFF = no tint, so
            // a type that authors nothing is visually unchanged). RenderScale is set directly + clamped to [0.25, 4.0]
            // (behavior-specific DATA, NOT a live F1 knob — like FleeHealthPct/charge); omitted → 1.0 (unchanged).
            type.RenderTintRgb = ParseTintRgb(dto.RenderTint);
            if (dto.RenderScale.HasValue)
            {
                type.RenderScale = Math.Clamp(dto.RenderScale.Value, MinRenderScale, MaxRenderScale);
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

    // MONSTER-TUNING-SAVE: the FAITHFUL INVERSE of FromManifestJson — serialize ALL live types back to the manifest
    // JSON shape, writing EVERY field FromManifestJson reads (id, displayName, the lootTableId/locomotion/behavior/
    // ability selectors, the charge tuning, every numeric tunable, the renderTint as "#RRGGBB" + renderScale). Used by
    // the F1 Monster-tab Save button (via GameServer) to PERSIST live-tuned values to Content/monsters.json so they
    // survive a restart (today AdminSetTuning is in-memory only). CRITICAL: it must drop NOTHING — a Save that omitted a
    // selector (locomotion/behavior/abilities/tint/scale) would, on reload, revert e.g. the gnoll to a default slime-like
    // monster. Round-trip pinned by a test: FromManifestJson(tickRate, ToManifestJson()) reproduces every field of every
    // type. The values are written AS-IS (already in-range — clamped on the first load / each TryApply); the reload
    // re-clamps idempotently. System.Text.Json cannot write `//` comments, so the annotations the shipped file carries
    // are DROPPED on Save (acceptable for a dev tool — the data is preserved, only the prose is lost).
    public string ToManifestJson()
    {
        var types = _ordered.Select(t => (MonsterTypeDto?)ToDto(t)).ToList();
        return JsonSerializer.Serialize(new MonsterManifestDto(types), SerializeManifestJsonOptions);
    }

    // MONSTER-TUNING-SAVE: project a live MonsterType onto the on-disk DTO — the inverse mapping of the per-field reads
    // in FromManifestJson. NAMED args (not positional) so a new DTO field forces a compile error here until it is mapped,
    // keeping the serializer from silently dropping a field a later phase adds. AbilityIds is copied (a fresh list) so the
    // DTO never aliases the live type's mutable list. RenderTintRgb is formatted back to the authoring "#RRGGBB" hex.
    private static MonsterTypeDto ToDto(MonsterType t) => new(
        Id: t.Id,
        DisplayName: t.DisplayName,
        LootTableId: t.LootTableId,
        LocomotionId: t.LocomotionId,
        BehaviorId: t.BehaviorId,
        AbilityIds: new List<string>(t.AbilityIds),
        ChargeCooldownMs: t.ChargeCooldownMs,
        ChargeDistanceUnits: t.ChargeDistanceUnits,
        ChargeTriggerRangeUnits: t.ChargeTriggerRangeUnits,
        SlamCooldownMs: t.SlamCooldownMs,
        SlamRadiusUnits: t.SlamRadiusUnits,
        SlamWindupMs: t.SlamWindupMs,
        SlamDamage: t.SlamDamage,
        MaxHealth: t.MaxHealth,
        FleeHealthPct: t.FleeHealthPct,
        MoveSpeedMultiplier: t.MoveSpeedMultiplier,
        RoamRadius: t.RoamRadius,
        PauseMinMs: t.PauseMinMs,
        PauseMaxMs: t.PauseMaxMs,
        AggroRadius: t.AggroRadius,
        ChaseLeash: t.ChaseLeash,
        AttackDamage: t.AttackDamage,
        AttackCooldownMs: t.AttackCooldownMs,
        AttackRangeUnits: t.AttackRangeUnits,
        HopDistanceUnits: t.HopDistanceUnits,
        HopHeightUnits: t.HopHeightUnits,
        HopAirborneMs: t.HopAirborneMs,
        HopDelayMs: t.HopDelayMs,
        RespawnMs: t.RespawnMs,
        RenderTint: FormatTintRgb(t.RenderTintRgb),
        RenderScale: t.RenderScale);

    // MONSTER-TUNING-SAVE: the inverse of ParseTintRgb — pack a 0xRRGGBB uint back to the authoring "#RRGGBB" hex string
    // (uppercase, 6 digits, '#'-prefixed). The high byte is masked so only the 24 RGB bits are emitted; white 0xFFFFFF
    // → "#FFFFFF" (re-parsed to white = no tint, the round-trip identity for an untinted type).
    private static string FormatTintRgb(uint rgb) =>
        "#" + (rgb & 0xFFFFFFu).ToString("X6", System.Globalization.CultureInfo.InvariantCulture);

    // MONSTER-TUNING-SAVE: write options — indented (human-readable) + camelCase property names (matching the shipped
    // manifest's casing; FromManifestJson reads case-insensitively, so the exact casing is cosmetic). DISTINCT from the
    // READ options (ManifestJsonOptions) — Disallow/comment-skip/trailing-commas are read-time concerns.
    private static readonly JsonSerializerOptions SerializeManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // MONSTER-BEHAVIOR P6: parse an authoring-friendly "#RRGGBB" (or "RRGGBB") hex string into a packed 0xRRGGBB uint.
    // Tolerant: a leading '#' is optional; surrounding whitespace is trimmed. ANY malformed/missing/blank value (null,
    // wrong length, non-hex) falls back to white 0xFFFFFF (= no tint) so a typo can never author a bizarre tint — it
    // just renders untinted, the safe default. The high byte is masked off so only the 24 RGB bits survive.
    private static uint ParseTintRgb(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return 0xFFFFFFu;
        }

        var s = hex.Trim();
        if (s.StartsWith('#'))
        {
            s = s[1..];
        }

        if (s.Length == 6
            && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb))
        {
            return rgb & 0xFFFFFFu;
        }

        return 0xFFFFFFu;
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

    // The on-disk manifest shape. P0 = the CURRENT MonsterType fields; P1 added the `locomotionId` selector, P3 the
    // `behaviorId` selector, P4 the `fleeHealthPct` behavior knob, and P5 (this change) the `abilityIds` composition set
    // + the `chargeCooldownMs`/`chargeDistanceUnits`/`chargeTriggerRangeUnits` charge tuning; a later phase adds the
    // `visualId` selector. Each selector is a plain STORED string (resolution is GameServer's job). `fleeHealthPct` +
    // the charge fields are behavior-specific DATA (NOT live F1 tunables). All tunables are nullable so
    // an omitted field falls back to the MonsterType default; id/displayName are validated as required in
    // FromManifestJson. JSON property names are camelCase (matched case-insensitively).
    private sealed record MonsterManifestDto(List<MonsterTypeDto?>? Types);

    private sealed record MonsterTypeDto(
        string? Id,
        string? DisplayName,
        string? LootTableId,
        string? LocomotionId,
        string? BehaviorId,
        List<string>? AbilityIds,
        int? ChargeCooldownMs,
        double? ChargeDistanceUnits,
        double? ChargeTriggerRangeUnits,
        // TELEGRAPH T1: the slam ability tuning (ability-specific data like the charge trio; omitted -> 0 = no slam).
        int? SlamCooldownMs,
        double? SlamRadiusUnits,
        int? SlamWindupMs,
        int? SlamDamage,
        int? MaxHealth,
        double? FleeHealthPct,
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
        int? RespawnMs,
        // MONSTER-BEHAVIOR P6: the placeholder per-type visual. RenderTint is an authoring-friendly "#RRGGBB" hex string
        // (parsed by ParseTintRgb; omitted/malformed → white); RenderScale is a double clamped to [0.25, 4.0] (omitted → 1.0).
        string? RenderTint,
        double? RenderScale);

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
    public MonsterAiTunables BuildTunables(MonsterType type) => new(
        RoamRadius: type.RoamRadius,
        PauseMinTicks: MsToTicks(type.PauseMinMs),
        PauseMaxTicks: PauseMaxTicks(type),
        AggroRadius: type.AggroRadius,
        DeaggroRadius: DeaggroRadius(type),
        ChaseLeash: type.ChaseLeash,
        AttackRangeUnits: type.AttackRangeUnits,
        AttackDamage: type.AttackDamage,
        AttackCooldownTicks: CooldownMsToTicks(type.AttackCooldownMs),
        AggroScanIntervalTicks: AggroScanIntervalTicks,
        // MONSTER-BEHAVIOR P4: pass the flee threshold straight through (a fraction, not a duration — NO tick-
        // quantisation), clamped to [0,1] so a nonsense out-of-range author value can't misbehave. 0 = never flee.
        FleeHealthPct: Math.Clamp(type.FleeHealthPct, 0d, 1d),
        // MONSTER-BEHAVIOR P5: the charge config. ChargeEnabled iff the type COMPOSED "charge" (case-insensitive) AND
        // authored a positive cooldown — so a type with the tuning but no ability id (or vice versa) never charges. The
        // cooldown is tick-quantised (the EXECUTOR enforces it via CanStart); the distance/trigger ranges pass through.
        ChargeEnabled: ChargeEnabled(type),
        ChargeDistanceUnits: type.ChargeDistanceUnits,
        ChargeTriggerRangeUnits: type.ChargeTriggerRangeUnits,
        ChargeCooldownTicks: ChargeCooldownTicks(type),
        // TELEGRAPH T1: the slam config the brain's trigger reads. SlamEnabled iff the type COMPOSED "slam" AND a
        // positive cooldown (mirroring ChargeEnabled); the cooldown is tick-quantised (the brain's own NextSlamTick
        // enforces it — a scheduled world event has no executor cooldown clock). Radius/windup/damage stay on the
        // TYPE (GameServer's TryBeginMonsterSlam reads them at cast) — the brain only needs the WHEN.
        SlamEnabled: SlamEnabled(type),
        SlamCooldownTicks: SlamCooldownTicks(type));

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
            // CONTEXTUAL-KNOBS: the newly-exposed per-composition knobs. TryApply is type-INDEPENDENT (it applies the
            // field to whatever type the key names, mirroring every other field) — the per-composition VISIBILITY is
            // BuildSnapshot's AppliesTo filter, so the F1 tab only ever SENDS a key it showed. moveSpeed mutates
            // MoveSpeedMultiplier; the LIVE re-pace of an already-spawned glider's SpeedUnitsPerSecond is wired in
            // GameServer.PropagateMonsterTypeSpeedToSpawned (runs after ANY per-type edit). FleeHealthPct + the charge
            // trio flow into BuildTunables, which the AI reads fresh each tick → a retune takes effect next tick.
            case MoveSpeedField:
                type.MoveSpeedMultiplier = ClampDouble(value, MinMoveSpeedMultiplier, MaxMoveSpeedMultiplier, out applied);
                return true;
            case FleeHealthPctField:
                type.FleeHealthPct = ClampDouble(value, MinFleeHealthPct, MaxFleeHealthPct, out applied);
                return true;
            case ChargeCooldownMsField:
                type.ChargeCooldownMs = ClampInt(value, MinChargeCooldownMs, MaxChargeCooldownMs, out applied);
                return true;
            case ChargeDistanceUnitsField:
                type.ChargeDistanceUnits = ClampDouble(value, MinChargeDistanceUnits, MaxChargeDistanceUnits, out applied);
                return true;
            case ChargeTriggerRangeUnitsField:
                type.ChargeTriggerRangeUnits = ClampDouble(value, MinChargeTriggerRangeUnits, MaxChargeTriggerRangeUnits, out applied);
                return true;
            // TELEGRAPH T1: the slam quartet — same contextual-knob treatment as the charge trio (type-independent
            // apply; the per-composition VISIBILITY is BuildSnapshot's AppliesTo filter). Windup/cooldown flow into
            // the brain/schedule via SlamWindupTicks/BuildTunables, read fresh — a retune takes effect next cast.
            case SlamRadiusUnitsField:
                type.SlamRadiusUnits = ClampDouble(value, MinSlamRadiusUnits, MaxSlamRadiusUnits, out applied);
                return true;
            case SlamWindupMsField:
                type.SlamWindupMs = ClampInt(value, MinSlamWindupMs, MaxSlamWindupMs, out applied);
                return true;
            case SlamDamageField:
                type.SlamDamage = ClampInt(value, MinSlamDamage, MaxSlamDamage, out applied);
                return true;
            case SlamCooldownMsField:
                type.SlamCooldownMs = ClampInt(value, MinSlamCooldownMs, MaxSlamCooldownMs, out applied);
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

    // MONSTER-BEHAVIOR P5: true iff this type can CHARGE — it composed the "charge" ability (case-insensitive) AND
    // authored a positive cooldown. Both are required so a type with the tuning but no ability id (or an ability id but
    // no tuning) never charges; the brain reads the derived MonsterAiTunables.ChargeEnabled, never these fields directly.
    public static bool ChargeEnabled(MonsterType type) =>
        type.ChargeCooldownMs > 0 && type.AbilityIds.Contains("charge", StringComparer.OrdinalIgnoreCase);

    // MONSTER-BEHAVIOR P5: this type's charge re-trigger cooldown in TICKS (Ceiling, >= 1 so even a tiny ms gates one
    // tick — same convention as the attack cooldown). Fed onto the charge def's CooldownTicks so the EXECUTOR's CanStart
    // enforces the re-charge gate (unlike the hop, whose cadence is the locomotion's TryBeginHop, not an executor clock).
    public uint ChargeCooldownTicks(MonsterType type) => CooldownMsToTicks(type.ChargeCooldownMs);

    // TELEGRAPH T1: true iff this type can SLAM — it composed the "slam" ability (case-insensitive) AND authored a
    // positive cooldown. Both are required (mirroring ChargeEnabled) so tuning without the ability id — or the id
    // without tuning — is inert; the brain reads the derived MonsterAiTunables.SlamEnabled, never these directly.
    public static bool SlamEnabled(MonsterType type) =>
        type.SlamCooldownMs > 0 && type.AbilityIds.Contains("slam", StringComparer.OrdinalIgnoreCase);

    // TELEGRAPH T1: this type's slam WINDUP (cast → resolve deadline) and re-cast cooldown in TICKS (Ceiling, >= 1 —
    // the cooldown convention, so even a tiny authored ms yields at least one telegraphed tick before resolve). Read
    // fresh at each cast (TryBeginMonsterSlam / the brain's re-arm) so a live retune applies to the NEXT cast.
    public uint SlamWindupTicks(MonsterType type) => CooldownMsToTicks(type.SlamWindupMs);

    public uint SlamCooldownTicks(MonsterType type) => CooldownMsToTicks(type.SlamCooldownMs);

    // The current per-type tuning as the wire snapshot the server replicates (login + on change). DATA-DRIVEN: each
    // type ships the GENERIC field list built from the descriptor table (current value via the getter, bounds from the
    // descriptor), so the F1 tab renders + edits the authoritative numbers without per-field code.
    public MonsterTuningSnapshot BuildSnapshot()
    {
        var entries = new MonsterTypeSnapshot[_ordered.Count];
        for (var i = 0; i < _ordered.Count; i++)
        {
            var t = _ordered[i];
            // CONTEXTUAL-KNOBS: ship ONLY the descriptors that apply to THIS type's composition (locomotion/behavior/
            // ability), in the table's grouped order — so a glider's tab carries walk speed + flee + charge (no hop
            // knobs) and a hopper's carries the hop quartet (no walk-speed/flee/charge). The wire shape is unchanged (a
            // per-type VARIABLE field list), so this needs no protocol bump and the data-driven client renders what it's sent.
            var fields = new List<MonsterTuningField>(Descriptors.Length);
            foreach (var d in Descriptors)
            {
                if (d.AppliesTo(t))
                {
                    fields.Add(new MonsterTuningField(d.Key, d.Label, d.Getter(t), d.Min, d.Max, d.IsInteger));
                }
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
