using System.IO;
using System.Linq;
using Mmo.Server.Runtime;
using Xunit;

namespace Mmo.Server.Tests;

// P0 (monster-behavior architecture, docs/monster-behavior-design.md): coverage for the JSON DATA MANIFEST loader
// MonsterTypeRegistry.FromManifestJson — the data foundation that lets monster TYPES be authored/edited in data with
// no code build. Pins: a 1-type and 2-type manifest round-trip to the right fields; out-of-range values are clamped
// on load (same bounds the F1 live tuning uses); malformed/empty/no-types manifests + duplicate ids + missing
// required fields throw; and a PARITY test that the SHIPPED Content/monsters.json deserialises to the SAME slime as
// the code-seeded fallback ctor (so the data file and the code safety-net can never silently drift). P0 schema =
// the current MonsterType fields only; the composition selectors arrive in later phases.
public sealed class MonsterTypeManifestTests
{
    private const int TickRate = 20;

    [Fact]
    public void RoundTripsASingleTypeManifest()
    {
        const string json = """
        {
          "types": [
            {
              "id": "gnoll",
              "displayName": "Gnoll",
              "lootTableId": "gnoll_loot",
              "maxHealth": 250,
              "moveSpeedMultiplier": 0.9,
              "roamRadius": 5.5,
              "pauseMinMs": 1000,
              "pauseMaxMs": 3000,
              "aggroRadius": 8.5,
              "chaseLeash": 16,
              "attackDamage": 25,
              "attackCooldownMs": 1500,
              "attackRangeUnits": 2.0,
              "hopDistanceUnits": 2.5,
              "hopHeightUnits": 0.75,
              "hopAirborneMs": 250,
              "hopDelayMs": 200,
              "respawnMs": 8000
            }
          ]
        }
        """;

        var registry = MonsterTypeRegistry.FromManifestJson(TickRate, json);

        Assert.Single(registry.Types);
        Assert.True(registry.TryGet("gnoll", out var g));
        Assert.Equal("gnoll", g.Id);
        Assert.Equal("Gnoll", g.DisplayName);
        Assert.Equal("gnoll_loot", g.LootTableId);
        Assert.Equal(250, g.MaxHealth);
        Assert.Equal(0.9, g.MoveSpeedMultiplier, 6);
        Assert.Equal(5.5d, g.RoamRadius, 6);
        Assert.Equal(1000, g.PauseMinMs);
        Assert.Equal(3000, g.PauseMaxMs);
        Assert.Equal(8.5d, g.AggroRadius, 6);
        Assert.Equal(16d, g.ChaseLeash, 6);
        Assert.Equal(25, g.AttackDamage);
        Assert.Equal(1500, g.AttackCooldownMs);
        Assert.Equal(2.0d, g.AttackRangeUnits, 6);
        Assert.Equal(2.5d, g.HopDistanceUnits, 6);
        Assert.Equal(0.75d, g.HopHeightUnits, 6);
        Assert.Equal(250, g.HopAirborneMs);
        Assert.Equal(200, g.HopDelayMs);
        Assert.Equal(8000, g.RespawnMs);
    }

    [Fact]
    public void RoundTripsATwoTypeManifestInOrder()
    {
        const string json = """
        {
          "types": [
            { "id": "slime", "displayName": "Slime", "maxHealth": 100 },
            { "id": "gnoll", "displayName": "Gnoll", "maxHealth": 250 }
          ]
        }
        """;

        var registry = MonsterTypeRegistry.FromManifestJson(TickRate, json);

        Assert.Equal(2, registry.Types.Count);
        Assert.Equal(new[] { "slime", "gnoll" }, registry.Types.Select(t => t.Id).ToArray());
        Assert.True(registry.TryGet("slime", out var slime));
        Assert.Equal(100, slime.MaxHealth);
        Assert.True(registry.TryGet("gnoll", out var gnoll));
        Assert.Equal(250, gnoll.MaxHealth);
    }

    [Fact]
    public void OmittedOptionalFieldsFallBackToTheTypeDefaults()
    {
        // Only id + displayName provided — every tunable must equal the MonsterType field default.
        const string json = """
        { "types": [ { "id": "blob", "displayName": "Blob" } ] }
        """;

        var registry = MonsterTypeRegistry.FromManifestJson(TickRate, json);
        Assert.True(registry.TryGet("blob", out var b));

        // Compare against a fresh code-default MonsterType (the single source of the defaults).
        var def = new MonsterType("blob", "Blob");
        Assert.Equal(def.MaxHealth, b.MaxHealth);
        Assert.Equal(def.FleeHealthPct, b.FleeHealthPct, 6); // MONSTER-BEHAVIOR P4: omitted fleeHealthPct = 0 (never flee).
        // MONSTER-BEHAVIOR P5: omitted ability/charge fields fall back to no abilities + a 0 charge (never charge).
        Assert.Empty(b.AbilityIds);
        Assert.Equal(def.ChargeCooldownMs, b.ChargeCooldownMs);
        Assert.Equal(def.ChargeDistanceUnits, b.ChargeDistanceUnits, 6);
        Assert.Equal(def.ChargeTriggerRangeUnits, b.ChargeTriggerRangeUnits, 6);
        Assert.Equal(def.MoveSpeedMultiplier, b.MoveSpeedMultiplier, 6);
        Assert.Equal(def.RoamRadius, b.RoamRadius, 6);
        Assert.Equal(def.PauseMinMs, b.PauseMinMs);
        Assert.Equal(def.PauseMaxMs, b.PauseMaxMs);
        Assert.Equal(def.AggroRadius, b.AggroRadius, 6);
        Assert.Equal(def.ChaseLeash, b.ChaseLeash, 6);
        Assert.Equal(def.AttackDamage, b.AttackDamage);
        Assert.Equal(def.AttackCooldownMs, b.AttackCooldownMs);
        Assert.Equal(def.AttackRangeUnits, b.AttackRangeUnits, 6);
        Assert.Equal(def.HopDistanceUnits, b.HopDistanceUnits, 6);
        Assert.Equal(def.HopHeightUnits, b.HopHeightUnits, 6);
        Assert.Equal(def.HopAirborneMs, b.HopAirborneMs);
        Assert.Equal(def.HopDelayMs, b.HopDelayMs);
        Assert.Equal(def.RespawnMs, b.RespawnMs);
        Assert.Equal(string.Empty, b.LootTableId); // omitted lootTableId = drops nothing.
        Assert.Equal(def.LocomotionId, b.LocomotionId); // omitted locomotionId = the "hop" default.
        Assert.Equal(def.BehaviorId, b.BehaviorId); // MONSTER-BEHAVIOR P3: omitted behaviorId = the "basicRoamer" default.
    }

    // MONSTER-BEHAVIOR P3 (docs/monster-behavior-design.md): the behavior composition SELECTOR is STORED as-authored by
    // the loader — even an id that isn't registered (resolution + the fallback-to-basicRoamer is GameServer's job, not
    // the loader's). So a manifest naming an unregistered behavior must keep it on the type verbatim (not coerce it).
    [Fact]
    public void BehaviorIdRoundTripsEvenWhenNotRegistered()
    {
        const string json = """
        { "types": [ { "id": "raider", "displayName": "Raider", "behaviorId": "skirmisher" } ] }
        """;

        var registry = MonsterTypeRegistry.FromManifestJson(TickRate, json);
        Assert.True(registry.TryGet("raider", out var r));
        Assert.Equal("skirmisher", r.BehaviorId);
    }

    // MONSTER-BEHAVIOR P3: a type with NO behaviorId falls back to the "basicRoamer" default (the loader leaves the
    // MonsterType field default in place). Pinned separately so the behavior selector's default is explicit.
    [Fact]
    public void OmittedBehaviorIdDefaultsToBasicRoamer()
    {
        const string json = """
        { "types": [ { "id": "slime", "displayName": "Slime" } ] }
        """;

        var registry = MonsterTypeRegistry.FromManifestJson(TickRate, json);
        Assert.True(registry.TryGet("slime", out var s));
        Assert.Equal("basicRoamer", s.BehaviorId);
    }

    // MONSTER-BEHAVIOR P1 (docs/monster-behavior-design.md): the locomotion composition SELECTOR is STORED as-authored
    // by the loader — even an id that isn't registered yet ("glide" before P2 adds it). The loader does NOT resolve or
    // validate the id; resolution + the fallback-to-hop is GameServer's job. So a manifest naming "glide" must keep
    // "glide" on the type verbatim (not coerce it to a default).
    [Fact]
    public void LocomotionIdRoundTripsEvenWhenNotRegistered()
    {
        const string json = """
        { "types": [ { "id": "walker", "displayName": "Walker", "locomotionId": "glide" } ] }
        """;

        var registry = MonsterTypeRegistry.FromManifestJson(TickRate, json);
        Assert.True(registry.TryGet("walker", out var w));
        Assert.Equal("glide", w.LocomotionId);
    }

    // MONSTER-BEHAVIOR P1: a type with NO locomotionId falls back to the "hop" default (the loader leaves the
    // MonsterType field default in place). Pinned separately from the catch-all omitted-defaults test so the locomotion
    // selector's default is explicit.
    [Fact]
    public void OmittedLocomotionIdDefaultsToHop()
    {
        const string json = """
        { "types": [ { "id": "slime", "displayName": "Slime" } ] }
        """;

        var registry = MonsterTypeRegistry.FromManifestJson(TickRate, json);
        Assert.True(registry.TryGet("slime", out var s));
        Assert.Equal("hop", s.LocomotionId);
    }

    [Fact]
    public void OutOfRangeValuesAreClampedOnLoad()
    {
        // Wild values for several tunables — each must be clamped to the SAME bounds TryApply uses.
        const string json = """
        {
          "types": [
            {
              "id": "slime",
              "displayName": "Slime",
              "maxHealth": 0,
              "roamRadius": 999,
              "aggroRadius": -5,
              "attackRangeUnits": 99,
              "hopDistanceUnits": 0,
              "hopHeightUnits": 100,
              "respawnMs": 999999999
            }
          ]
        }
        """;

        var registry = MonsterTypeRegistry.FromManifestJson(TickRate, json);
        Assert.True(registry.TryGet("slime", out var s));

        Assert.Equal(1, s.MaxHealth);            // clamped up to MinMaxHealth.
        Assert.Equal(32d, s.RoamRadius, 6);      // clamped down to MaxRoamRadius.
        Assert.Equal(0.5d, s.AggroRadius, 6);    // clamped up to MinAggroRadius.
        Assert.Equal(8d, s.AttackRangeUnits, 6); // clamped down to MaxAttackRangeUnits.
        Assert.Equal(0.25d, s.HopDistanceUnits, 6); // clamped up to MinHopDistance.
        Assert.Equal(4d, s.HopHeightUnits, 6);   // clamped down to MaxHopHeight.
        Assert.Equal(300000, s.RespawnMs);       // clamped down to MaxRespawnMs (5 min).
    }

    // P0 review (footgun fix): a manifest that renames/omits the canonical "slime" id is STRUCTURALLY valid and must
    // NOT crash — Default falls back to the FIRST registered type so /monster (no name) + the GameServer constructor's
    // _monsterTypes.Default access keep working on a fully-data-authored monster set.
    [Fact]
    public void DefaultFallsBackToTheFirstTypeWhenNoCanonicalSlime()
    {
        const string json = """
        { "types": [
            { "id": "gnoll", "displayName": "Gnoll" },
            { "id": "wolf", "displayName": "Wolf" }
        ] }
        """;

        var registry = MonsterTypeRegistry.FromManifestJson(TickRate, json);

        Assert.Equal("gnoll", registry.Default.Id); // first listed, since there is no "slime"
        Assert.Equal(2, registry.Types.Count);
    }

    // P0 review (typo guard): an unknown / misspelled field must FAIL LOUDLY (throw → GameServer's code-seed fallback)
    // rather than be silently dropped leaving the monster on a default the author didn't intend.
    [Fact]
    public void UnknownFieldIsRejected()
    {
        const string json = """
        { "types": [ { "id": "slime", "displayName": "Slime", "maxHelth": 5000 } ] }
        """;

        Assert.Throws<System.ArgumentException>(() => MonsterTypeRegistry.FromManifestJson(TickRate, json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]                       // malformed JSON.
    [InlineData("{ }")]                              // no "types".
    [InlineData("{ \"types\": [] }")]                // empty types.
    [InlineData("{ \"types\": [ { \"displayName\": \"NoId\" } ] }")]      // missing id.
    [InlineData("{ \"types\": [ { \"id\": \"x\" } ] }")]                  // missing displayName.
    [InlineData("{ \"types\": [ { \"id\": \"\", \"displayName\": \"x\" } ] }")] // empty id.
    public void MalformedOrInvalidManifestThrows(string json)
    {
        Assert.Throws<System.ArgumentException>(() => MonsterTypeRegistry.FromManifestJson(TickRate, json));
    }

    [Fact]
    public void DuplicateTypeIdIsRejected()
    {
        const string json = """
        {
          "types": [
            { "id": "slime", "displayName": "Slime" },
            { "id": "slime", "displayName": "Slime Two" }
          ]
        }
        """;

        var ex = Assert.Throws<System.ArgumentException>(
            () => MonsterTypeRegistry.FromManifestJson(TickRate, json));
        Assert.Contains("duplicate", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateTypeIdIsRejectedCaseInsensitively()
    {
        // The registry keys ids case-insensitively, so "slime" and "SLIME" collide.
        const string json = """
        {
          "types": [
            { "id": "slime", "displayName": "Slime" },
            { "id": "SLIME", "displayName": "Slime Upper" }
          ]
        }
        """;

        Assert.Throws<System.ArgumentException>(() => MonsterTypeRegistry.FromManifestJson(TickRate, json));
    }

    // PARITY: the SHIPPED Content/monsters.json must deserialise to the SAME slime the code-seeded fallback ctor
    // builds — so the authoritative data file and the code safety-net can never silently drift. Reads the shipped
    // file (copied to the test output dir transitively via Mmo.Server.csproj's None+CopyToOutputDirectory; falls
    // back to the source path if the transitive copy is unavailable).
    [Fact]
    public void ShippedManifestMatchesTheCodeSeededSlime()
    {
        var json = ReadShippedManifest();
        var fromData = MonsterTypeRegistry.FromManifestJson(TickRate, json);
        var fromCode = new MonsterTypeRegistry(TickRate);

        Assert.True(fromData.TryGet("slime", out var d));
        Assert.True(fromCode.TryGet("slime", out var c));

        Assert.Equal(c.Id, d.Id);
        Assert.Equal(c.DisplayName, d.DisplayName);
        Assert.Equal(c.LootTableId, d.LootTableId);
        Assert.Equal(c.LocomotionId, d.LocomotionId); // MONSTER-BEHAVIOR P1: the locomotion selector must not drift.
        Assert.Equal(c.BehaviorId, d.BehaviorId); // MONSTER-BEHAVIOR P3: the behavior selector must not drift.
        Assert.Equal(c.FleeHealthPct, d.FleeHealthPct, 6); // MONSTER-BEHAVIOR P4: the slime never flees (0) in both.
        // TELEGRAPH T1: the slime composes exactly the SLAM ability (its first real attack pattern) + a 0 charge in
        // both the data file and the code seed — and the slam knobs must not drift either.
        Assert.Equal(c.AbilityIds, d.AbilityIds);
        Assert.Equal(new[] { "slam" }, d.AbilityIds);
        Assert.Equal(c.SlamRadiusUnits, d.SlamRadiusUnits, 6);
        Assert.Equal(c.SlamWindupMs, d.SlamWindupMs);
        Assert.Equal(c.SlamDamage, d.SlamDamage);
        Assert.Equal(c.SlamCooldownMs, d.SlamCooldownMs);
        Assert.Equal(c.ChargeCooldownMs, d.ChargeCooldownMs);
        Assert.Equal(c.ChargeDistanceUnits, d.ChargeDistanceUnits, 6);
        Assert.Equal(c.ChargeTriggerRangeUnits, d.ChargeTriggerRangeUnits, 6);
        // TELEGRAPH SHAPES WEDGE+LINE: the slime authors NO wedge/line shaping — its slam is a plain CIRCLE and it has
        // no charge/lunge, so the shape selector defaults to "circle" and the charge-telegraph fields are 0 in both the
        // data file and the code seed (they must not drift).
        Assert.Equal(c.SlamShape, d.SlamShape);
        Assert.Equal("circle", d.SlamShape);
        Assert.Equal(c.SlamWedgeAngleDeg, d.SlamWedgeAngleDeg, 6);
        Assert.Equal(0d, d.SlamWedgeAngleDeg, 6);
        Assert.Equal(c.ChargeWindupMs, d.ChargeWindupMs);
        Assert.Equal(0, d.ChargeWindupMs);
        Assert.Equal(c.ChargeDamage, d.ChargeDamage);
        Assert.Equal(c.ChargeWidthUnits, d.ChargeWidthUnits, 6);
        Assert.Equal(c.MaxHealth, d.MaxHealth);
        Assert.Equal(c.MoveSpeedMultiplier, d.MoveSpeedMultiplier, 6);
        Assert.Equal(c.RoamRadius, d.RoamRadius, 6);
        Assert.Equal(c.PauseMinMs, d.PauseMinMs);
        Assert.Equal(c.PauseMaxMs, d.PauseMaxMs);
        Assert.Equal(c.AggroRadius, d.AggroRadius, 6);
        Assert.Equal(c.ChaseLeash, d.ChaseLeash, 6);
        Assert.Equal(c.AttackDamage, d.AttackDamage);
        Assert.Equal(c.AttackCooldownMs, d.AttackCooldownMs);
        Assert.Equal(c.AttackRangeUnits, d.AttackRangeUnits, 6);
        Assert.Equal(c.HopDistanceUnits, d.HopDistanceUnits, 6);
        Assert.Equal(c.HopHeightUnits, d.HopHeightUnits, 6);
        Assert.Equal(c.HopAirborneMs, d.HopAirborneMs);
        Assert.Equal(c.HopDelayMs, d.HopDelayMs);
        Assert.Equal(c.RespawnMs, d.RespawnMs);
        // MONSTER-BEHAVIOR P6: the slime authors NO placeholder visual in either source → white (0xFFFFFF) + 1.0 scale
        // (the no-op the client renders unchanged); the data file and the code seed must not drift on these either.
        Assert.Equal(c.RenderTintRgb, d.RenderTintRgb);
        Assert.Equal(0xFFFFFFu, d.RenderTintRgb);
        Assert.Equal(c.RenderScale, d.RenderScale, 6);
        Assert.Equal(1.0d, d.RenderScale, 6);

        // The shipped manifest now carries additional types (MONSTER-BEHAVIOR P2 added the gnoll glider), so parity is
        // pinned on the SLIME only — and the slime must stay the canonical default so /monster (no name) spawns it.
        Assert.Equal("slime", fromData.Default.Id);
    }

    // MONSTER-BEHAVIOR P2 (docs/monster-behavior-design.md): the shipped manifest carries the "gnoll" GLIDER — the
    // first type that selects a non-hop locomotion. Pins that it loads with locomotionId "glide" + its authored stats
    // (so /monster gnoll spawns a walking gnoll), and that the slime is still present alongside it.
    [Fact]
    public void ShippedManifestLoadsTheGnollGlider()
    {
        var json = ReadShippedManifest();
        var registry = MonsterTypeRegistry.FromManifestJson(TickRate, json);

        Assert.True(registry.TryGet("slime", out _), "shipped manifest must still carry the slime.");
        Assert.True(registry.TryGet("gnoll", out var g), "shipped manifest must carry the gnoll glider.");
        Assert.Equal("Gnoll", g.DisplayName);
        Assert.Equal("glide", g.LocomotionId);
        Assert.Equal("skirmisher", g.BehaviorId); // MONSTER-BEHAVIOR P4: the gnoll runs the flee-when-wounded brain.
        Assert.Equal(0.3d, g.FleeHealthPct, 6);    // MONSTER-BEHAVIOR P4: flees below 30% HP.
        Assert.Equal(string.Empty, g.LootTableId); // no loot table authored yet — drops nothing.
        Assert.Equal(200, g.MaxHealth);
        Assert.Equal(0.9d, g.MoveSpeedMultiplier, 6);
        Assert.Equal(8d, g.AggroRadius, 6);
        Assert.Equal(16d, g.ChaseLeash, 6);
        Assert.Equal(20, g.AttackDamage);
        Assert.Equal(1200, g.AttackCooldownMs);
        Assert.Equal(1.5d, g.AttackRangeUnits, 6);
        // MONSTER-BEHAVIOR P5: the gnoll composes the "charge" ability + its charge tuning.
        Assert.Equal(new[] { "charge" }, g.AbilityIds);
        Assert.Equal(4000, g.ChargeCooldownMs);
        Assert.Equal(4.0d, g.ChargeDistanceUnits, 6);
        Assert.Equal(7.0d, g.ChargeTriggerRangeUnits, 6);
        Assert.True(MonsterTypeRegistry.ChargeEnabled(g)); // composed + a positive cooldown ⇒ charge-enabled.
        // MONSTER-BEHAVIOR P6: the gnoll authors the PLACEHOLDER per-type visual — a brown "#B5651D" tint + 1.4× scale,
        // so it renders visibly bigger + tinted vs the (default white / 1.0) slime, with NO art assets.
        Assert.Equal(0xB5651Du, g.RenderTintRgb);
        Assert.Equal(1.4d, g.RenderScale, 6);
    }

    // TELEGRAPH SHAPES WEDGE+LINE (docs/boss-encounter-sunderer-design.md): the shipped Sunderer loads its SHAPED kit —
    // Cleave as a 130° WEDGE slam, Lunge as a telegraphed LINE charge (chargeWindupMs > 0 → LungeEnabled). Pins the
    // content wiring + the enable predicates so the boss actually casts the new shapes.
    [Fact]
    public void ShippedManifestLoadsTheSundererShapedKit()
    {
        var json = ReadShippedManifest();
        var registry = MonsterTypeRegistry.FromManifestJson(TickRate, json);

        Assert.True(registry.TryGet("sunderer", out var s), "shipped manifest must carry the Sunderer boss.");
        // CLEAVE — a wedge slam.
        Assert.Equal(new[] { "slam", "charge" }, s.AbilityIds);
        Assert.Equal("wedge", s.SlamShape);
        Assert.Equal(130d, s.SlamWedgeAngleDeg, 6);
        Assert.Equal(2.8d, s.SlamRadiusUnits, 6);
        Assert.Equal(800, s.SlamWindupMs);
        Assert.Equal(25, s.SlamDamage);
        Assert.True(MonsterTypeRegistry.SlamEnabled(s));
        // LUNGE — a telegraphed line charge.
        Assert.Equal(900, s.ChargeWindupMs);
        Assert.Equal(20, s.ChargeDamage);
        Assert.Equal(2.0d, s.ChargeWidthUnits, 6);
        Assert.Equal(8.0d, s.ChargeDistanceUnits, 6);
        Assert.True(MonsterTypeRegistry.LungeEnabled(s));
    }

    // MONSTER-BEHAVIOR P6: the "#RRGGBB" render-tint authoring string parses to the packed 0xRRGGBB uint; a leading '#'
    // is optional. An omitted, blank, or malformed value falls back to white (0xFFFFFF = no tint) — a typo can never
    // author a bizarre tint, it just renders untinted.
    [Theory]
    [InlineData("\"#B5651D\"", 0xB5651Du)]   // canonical hex with '#'
    [InlineData("\"b5651d\"", 0xB5651Du)]    // '#' optional, lowercase
    [InlineData("\"#000000\"", 0x000000u)]   // black is a valid tint (kills the body to black)
    [InlineData("\"#FFFFFF\"", 0xFFFFFFu)]   // explicit white == the default no-op
    [InlineData("\"#12g456\"", 0xFFFFFFu)]   // non-hex char → white
    [InlineData("\"#1234\"", 0xFFFFFFu)]     // wrong length → white
    [InlineData("\"\"", 0xFFFFFFu)]          // blank → white
    public void ParsesRenderTintHexWithWhiteFallback(string tintJson, uint expected)
    {
        var json = $$"""
        {
          "types": [
            { "id": "t", "displayName": "T", "renderTint": {{tintJson}} }
          ]
        }
        """;

        var registry = MonsterTypeRegistry.FromManifestJson(TickRate, json);
        Assert.True(registry.TryGet("t", out var t));
        Assert.Equal(expected, t.RenderTintRgb);
    }

    // MONSTER-BEHAVIOR P6: an omitted renderTint/renderScale defaults to white + 1.0 (the visual no-op).
    [Fact]
    public void OmittedRenderVisualDefaultsToWhiteAndUnitScale()
    {
        const string json = """
        {
          "types": [ { "id": "t", "displayName": "T" } ]
        }
        """;

        var registry = MonsterTypeRegistry.FromManifestJson(TickRate, json);
        Assert.True(registry.TryGet("t", out var t));
        Assert.Equal(0xFFFFFFu, t.RenderTintRgb);
        Assert.Equal(1.0d, t.RenderScale, 6);
    }

    // MONSTER-BEHAVIOR P6: renderScale is clamped to [0.25, 4.0] on load (a data file cannot author an invisible or
    // world-filling placeholder); an in-range value is honoured verbatim.
    [Theory]
    [InlineData(1.4d, 1.4d)]      // in range → honoured
    [InlineData(0.1d, 0.25d)]     // below min → clamped up
    [InlineData(99.0d, 4.0d)]     // above max → clamped down
    public void ClampsRenderScale(double authored, double expected)
    {
        var json = $$"""
        {
          "types": [ { "id": "t", "displayName": "T", "renderScale": {{authored.ToString(System.Globalization.CultureInfo.InvariantCulture)}} } ]
        }
        """;

        var registry = MonsterTypeRegistry.FromManifestJson(TickRate, json);
        Assert.True(registry.TryGet("t", out var t));
        Assert.Equal(expected, t.RenderScale, 6);
    }

    // MONSTER-TUNING-SAVE: the CRITICAL round-trip — ToManifestJson must be the FAITHFUL INVERSE of FromManifestJson.
    // A 2-type registry (the default slime + a FULLY-COMPOSED gnoll: glide locomotion / skirmisher behavior / charge
    // ability + a NON-default tint + scale + every tunable) is serialized then re-loaded; EVERY field of EVERY type must
    // survive. This is the single most important pin: a Save that dropped a P1–P6 selector (locomotion/behavior/abilities/
    // tint/scale) would, on reload, revert the gnoll to a default slime-like monster. The gnoll authors a non-default
    // tint + abilities specifically to prove those survive.
    [Fact]
    public void ToManifestJsonRoundTripsEveryFieldOfEveryType()
    {
        const string json = """
        {
          "types": [
            {
              "id": "slime",
              "displayName": "Slime",
              "lootTableId": "slime_loot",
              "locomotionId": "hop",
              "behaviorId": "basicRoamer",
              "maxHealth": 100,
              "moveSpeedMultiplier": 0.6,
              "roamRadius": 4,
              "pauseMinMs": 2000,
              "pauseMaxMs": 5000,
              "aggroRadius": 6,
              "chaseLeash": 12,
              "attackDamage": 10,
              "attackCooldownMs": 1000,
              "attackRangeUnits": 1.5,
              "hopDistanceUnits": 1.5,
              "hopHeightUnits": 0.5,
              "hopAirborneMs": 300,
              "hopDelayMs": 400,
              "respawnMs": 5000
            },
            {
              "id": "gnoll",
              "displayName": "Gnoll",
              "lootTableId": "",
              "locomotionId": "glide",
              "behaviorId": "skirmisher",
              "renderTint": "#B5651D",
              "renderScale": 1.4,
              "abilityIds": ["charge"],
              "chargeCooldownMs": 4000,
              "chargeDistanceUnits": 4.0,
              "chargeTriggerRangeUnits": 7.0,
              "maxHealth": 200,
              "moveSpeedMultiplier": 0.9,
              "roamRadius": 5,
              "aggroRadius": 8,
              "chaseLeash": 16,
              "attackDamage": 20,
              "attackCooldownMs": 1200,
              "attackRangeUnits": 1.5,
              "fleeHealthPct": 0.3
            }
          ]
        }
        """;

        var original = MonsterTypeRegistry.FromManifestJson(TickRate, json);
        var reloaded = MonsterTypeRegistry.FromManifestJson(TickRate, original.ToManifestJson());

        AssertRegistriesEqual(original, reloaded);

        // Prove the gnoll's composition selectors specifically survived (the headline risk).
        Assert.True(reloaded.TryGet("gnoll", out var g));
        Assert.Equal("glide", g.LocomotionId);
        Assert.Equal("skirmisher", g.BehaviorId);
        Assert.Equal(new[] { "charge" }, g.AbilityIds);
        Assert.Equal(0xB5651Du, g.RenderTintRgb);
        Assert.Equal(1.4d, g.RenderScale, 6);
        Assert.Equal(0.3d, g.FleeHealthPct, 6);
        Assert.Equal(4000, g.ChargeCooldownMs);
    }

    // MONSTER-TUNING-SAVE: the SHIPPED Content/monsters.json round-trips through ToManifestJson → FromManifestJson with
    // every value preserved (parity) — so saving the unchanged shipped data does not silently mutate any monster.
    [Fact]
    public void ShippedManifestRoundTripsThroughToManifestJsonUnchanged()
    {
        var fromShipped = MonsterTypeRegistry.FromManifestJson(TickRate, ReadShippedManifest());
        var reloaded = MonsterTypeRegistry.FromManifestJson(TickRate, fromShipped.ToManifestJson());
        AssertRegistriesEqual(fromShipped, reloaded);
    }

    // Field-by-field equality of two registries (same types in the same order, every MonsterType field equal). Used by
    // the round-trip pins — a dropped field on Save is the main risk, so this checks ALL of them incl. the selectors.
    private static void AssertRegistriesEqual(MonsterTypeRegistry expected, MonsterTypeRegistry actual)
    {
        Assert.Equal(expected.Types.Count, actual.Types.Count);
        Assert.Equal(expected.Types.Select(t => t.Id), actual.Types.Select(t => t.Id));

        for (var i = 0; i < expected.Types.Count; i++)
        {
            var e = expected.Types[i];
            var a = actual.Types[i];
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.DisplayName, a.DisplayName);
            Assert.Equal(e.LootTableId, a.LootTableId);
            Assert.Equal(e.LocomotionId, a.LocomotionId);
            Assert.Equal(e.BehaviorId, a.BehaviorId);
            Assert.Equal(e.AbilityIds, a.AbilityIds);
            Assert.Equal(e.ChargeCooldownMs, a.ChargeCooldownMs);
            Assert.Equal(e.ChargeDistanceUnits, a.ChargeDistanceUnits, 6);
            Assert.Equal(e.ChargeTriggerRangeUnits, a.ChargeTriggerRangeUnits, 6);
            // TELEGRAPH SHAPES WEDGE+LINE: the charge-telegraph (Lunge) + slam-shape (Cleave) fields must survive Save.
            Assert.Equal(e.ChargeWindupMs, a.ChargeWindupMs);
            Assert.Equal(e.ChargeDamage, a.ChargeDamage);
            Assert.Equal(e.ChargeWidthUnits, a.ChargeWidthUnits, 6);
            Assert.Equal(e.SlamCooldownMs, a.SlamCooldownMs);
            Assert.Equal(e.SlamRadiusUnits, a.SlamRadiusUnits, 6);
            Assert.Equal(e.SlamWindupMs, a.SlamWindupMs);
            Assert.Equal(e.SlamDamage, a.SlamDamage);
            Assert.Equal(e.SlamShape, a.SlamShape);
            Assert.Equal(e.SlamWedgeAngleDeg, a.SlamWedgeAngleDeg, 6);
            Assert.Equal(e.MaxHealth, a.MaxHealth);
            Assert.Equal(e.FleeHealthPct, a.FleeHealthPct, 6);
            Assert.Equal(e.MoveSpeedMultiplier, a.MoveSpeedMultiplier, 6);
            Assert.Equal(e.RoamRadius, a.RoamRadius, 6);
            Assert.Equal(e.PauseMinMs, a.PauseMinMs);
            Assert.Equal(e.PauseMaxMs, a.PauseMaxMs);
            Assert.Equal(e.AggroRadius, a.AggroRadius, 6);
            Assert.Equal(e.ChaseLeash, a.ChaseLeash, 6);
            Assert.Equal(e.AttackDamage, a.AttackDamage);
            Assert.Equal(e.AttackCooldownMs, a.AttackCooldownMs);
            Assert.Equal(e.AttackRangeUnits, a.AttackRangeUnits, 6);
            Assert.Equal(e.HopDistanceUnits, a.HopDistanceUnits, 6);
            Assert.Equal(e.HopHeightUnits, a.HopHeightUnits, 6);
            Assert.Equal(e.HopAirborneMs, a.HopAirborneMs);
            Assert.Equal(e.HopDelayMs, a.HopDelayMs);
            Assert.Equal(e.RespawnMs, a.RespawnMs);
            Assert.Equal(e.RenderTintRgb, a.RenderTintRgb);
            Assert.Equal(e.RenderScale, a.RenderScale, 6);
        }
    }

    private static string ReadShippedManifest()
    {
        var shipped = Path.Combine(System.AppContext.BaseDirectory, "Content", "monsters.json");
        if (File.Exists(shipped))
        {
            return File.ReadAllText(shipped);
        }

        // Fallback: walk up from the test output dir to the repo root and read the source manifest.
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Mmo.Server", "Content", "monsters.json");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate the shipped monsters.json (neither the test output Content/ nor the source path).");
    }
}
