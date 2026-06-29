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

        // And the data registry shipped exactly the one slime type.
        Assert.Single(fromData.Types);
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
