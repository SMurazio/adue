using System;
using System.IO;
using System.Linq;
using Mmo.Server.Runtime;
using Xunit;

namespace Mmo.Server.Tests;

// ECOLOGY E1 (docs/ecology-v1-design.md §3/§7/§8): coverage for the JSON DATA MANIFEST loader
// EcologyRegistry.FromManifestJson — mirrors MonsterTypeManifestTests' shape (round-trip, clamping, malformed/
// duplicate rejection, and a PARITY test that the SHIPPED Content/ecology.json matches the code-seeded fallback
// byte-for-byte so the data file and the code safety-net can never silently drift).
public sealed class EcologyRegistryTests
{
    [Fact]
    public void RoundTripsARegionWithTwoTypes()
    {
        const string json = """
        {
          "regions": [
            {
              "id": "the_verge",
              "displayName": "The Verge",
              "minX": 100, "minY": 300, "maxX": 300, "maxY": 370,
              "types": [
                { "typeId": "slime", "k": 6, "rPerMinute": 0.25, "maxLive": 6 },
                { "typeId": "gnoll", "k": 6, "rPerMinute": 0.25, "maxLive": 6 }
              ]
            }
          ]
        }
        """;

        var registry = EcologyRegistry.FromManifestJson(json);

        Assert.Single(registry.Regions);
        Assert.True(registry.TryGet("the_verge", out var region));
        Assert.Equal("The Verge", region.DisplayName);
        Assert.Equal(100, region.MinX);
        Assert.Equal(300, region.MinY);
        Assert.Equal(300, region.MaxX);
        Assert.Equal(370, region.MaxY);
        Assert.Equal(2, region.Types.Count);
        Assert.Equal(6d, region.Types["slime"].K, 6);
        Assert.Equal(0.25d, region.Types["slime"].RPerMinute, 6);
        Assert.Equal(6, region.Types["slime"].MaxLive);
        Assert.Equal(6d, region.Types["gnoll"].K, 6);
    }

    [Fact]
    public void RegionIdAndTypeIdAreCaseInsensitive()
    {
        const string json = """
        {
          "regions": [
            { "id": "Slime_Hollow", "displayName": "Slime Hollow", "minX": 0, "minY": 0, "maxX": 10, "maxY": 10,
              "types": [ { "typeId": "Slime", "k": 10, "rPerMinute": 1.0, "maxLive": 10 } ] }
          ]
        }
        """;

        var registry = EcologyRegistry.FromManifestJson(json);
        Assert.True(registry.TryGet("slime_hollow", out var region));
        Assert.True(region.Types.TryGetValue("SLIME", out _));
    }

    [Theory]
    [InlineData(0.0, 3.0)]     // K below min -> clamped up to 3 (raised 1->3 by the E1 review: K <= 2 puts the
                               // 0.5 stock floor at/above the 0.25K band, making DEPLETED unreachable)
    [InlineData(2.0, 3.0)]     // the exact latent-trap K the review named
    [InlineData(1000.0, 64.0)] // K above max -> clamped down to 64
    public void KIsClampedOnLoad(double authoredK, double expectedK)
    {
        var json = $$"""
        {
          "regions": [
            { "id": "r", "displayName": "R", "minX": 0, "minY": 0, "maxX": 10, "maxY": 10,
              "types": [ { "typeId": "t", "k": {{authoredK}}, "rPerMinute": 1.0, "maxLive": 5 } ] }
          ]
        }
        """;

        var registry = EcologyRegistry.FromManifestJson(json);
        Assert.True(registry.TryGet("r", out var region));
        Assert.Equal(expectedK, region.Types["t"].K, 6);
    }

    [Theory]
    [InlineData(0.001, 0.05)] // r below min -> clamped up to 0.05
    [InlineData(100.0, 10.0)] // r above max -> clamped down to 10
    public void RPerMinuteIsClampedOnLoad(double authoredR, double expectedR)
    {
        var json = $$"""
        {
          "regions": [
            { "id": "r", "displayName": "R", "minX": 0, "minY": 0, "maxX": 10, "maxY": 10,
              "types": [ { "typeId": "t", "k": 5, "rPerMinute": {{authoredR}}, "maxLive": 5 } ] }
          ]
        }
        """;

        var registry = EcologyRegistry.FromManifestJson(json);
        Assert.True(registry.TryGet("r", out var region));
        Assert.Equal(expectedR, region.Types["t"].RPerMinute, 6);
    }

    [Theory]
    [InlineData(0, 1)]   // maxLive below min -> clamped up to 1
    [InlineData(999, 32)] // maxLive above max -> clamped down to 32
    public void MaxLiveIsClampedOnLoad(int authoredMaxLive, int expectedMaxLive)
    {
        var json = $$"""
        {
          "regions": [
            { "id": "r", "displayName": "R", "minX": 0, "minY": 0, "maxX": 10, "maxY": 10,
              "types": [ { "typeId": "t", "k": 5, "rPerMinute": 1.0, "maxLive": {{authoredMaxLive}} } ] }
          ]
        }
        """;

        var registry = EcologyRegistry.FromManifestJson(json);
        Assert.True(registry.TryGet("r", out var region));
        Assert.Equal(expectedMaxLive, region.Types["t"].MaxLive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]                                    // malformed JSON.
    [InlineData("{ }")]                                           // no "regions".
    [InlineData("{ \"regions\": [] }")]                           // empty regions.
    [InlineData("{ \"regions\": [ { \"displayName\": \"NoId\", \"minX\": 0, \"minY\": 0, \"maxX\": 1, \"maxY\": 1, \"types\": [ { \"typeId\": \"t\" } ] } ] }")] // missing id.
    [InlineData("{ \"regions\": [ { \"id\": \"r\", \"minX\": 0, \"minY\": 0, \"maxX\": 1, \"maxY\": 1, \"types\": [ { \"typeId\": \"t\" } ] } ] }")] // missing displayName.
    [InlineData("{ \"regions\": [ { \"id\": \"r\", \"displayName\": \"R\", \"minX\": 0, \"minY\": 0, \"maxX\": 1, \"maxY\": 1, \"types\": [] } ] }")] // no types.
    [InlineData("{ \"regions\": [ { \"id\": \"r\", \"displayName\": \"R\", \"minX\": 5, \"minY\": 0, \"maxX\": 1, \"maxY\": 1, \"types\": [ { \"typeId\": \"t\" } ] } ] }")] // minX > maxX.
    [InlineData("{ \"regions\": [ { \"id\": \"r\", \"displayName\": \"R\", \"minX\": 0, \"minY\": 5, \"maxX\": 1, \"maxY\": 1, \"types\": [ { \"typeId\": \"t\" } ] } ] }")] // minY > maxY.
    public void MalformedOrInvalidManifestThrows(string json)
    {
        Assert.Throws<ArgumentException>(() => EcologyRegistry.FromManifestJson(json));
    }

    [Fact]
    public void DuplicateRegionIdIsRejected()
    {
        const string json = """
        {
          "regions": [
            { "id": "r", "displayName": "R", "minX": 0, "minY": 0, "maxX": 1, "maxY": 1, "types": [ { "typeId": "t", "k": 5 } ] },
            { "id": "r", "displayName": "R2", "minX": 2, "minY": 2, "maxX": 3, "maxY": 3, "types": [ { "typeId": "t", "k": 5 } ] }
          ]
        }
        """;

        var ex = Assert.Throws<ArgumentException>(() => EcologyRegistry.FromManifestJson(json));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateTypeIdWithinARegionIsRejected()
    {
        const string json = """
        {
          "regions": [
            { "id": "r", "displayName": "R", "minX": 0, "minY": 0, "maxX": 1, "maxY": 1,
              "types": [ { "typeId": "slime", "k": 5 }, { "typeId": "slime", "k": 8 } ] }
          ]
        }
        """;

        Assert.Throws<ArgumentException>(() => EcologyRegistry.FromManifestJson(json));
    }

    [Fact]
    public void UnknownFieldIsRejected()
    {
        const string json = """
        { "regions": [ { "id": "r", "displayName": "R", "minX": 0, "minY": 0, "maxX": 1, "maxY": 1, "bogus": 1,
            "types": [ { "typeId": "t", "k": 5 } ] } ] }
        """;

        Assert.Throws<ArgumentException>(() => EcologyRegistry.FromManifestJson(json));
    }

    [Fact]
    public void TryGetRegionAtFindsTheContainingRegion()
    {
        const string json = """
        {
          "regions": [
            { "id": "a", "displayName": "A", "minX": 0, "minY": 0, "maxX": 10, "maxY": 10, "types": [ { "typeId": "t", "k": 5 } ] },
            { "id": "b", "displayName": "B", "minX": 20, "minY": 20, "maxX": 30, "maxY": 30, "types": [ { "typeId": "t", "k": 5 } ] }
          ]
        }
        """;

        var registry = EcologyRegistry.FromManifestJson(json);

        Assert.True(registry.TryGetRegionAt(5, 5, out var a));
        Assert.Equal("a", a.Id);
        Assert.True(registry.TryGetRegionAt(0, 0, out var edge)); // inclusive rect boundary.
        Assert.Equal("a", edge.Id);
        Assert.True(registry.TryGetRegionAt(10, 10, out var farEdge));
        Assert.Equal("a", farEdge.Id);
        Assert.True(registry.TryGetRegionAt(25, 25, out var b));
        Assert.Equal("b", b.Id);
        Assert.False(registry.TryGetRegionAt(15, 15, out _)); // in neither rect (the gap between them).
    }

    // PARITY: the SHIPPED Content/ecology.json must deserialise to the SAME three §7 starter regions the
    // code-seeded fallback ctor builds — so the authoritative data file and the code safety-net can never
    // silently drift.
    [Fact]
    public void ShippedManifestMatchesTheCodeSeededRegions()
    {
        var fromData = EcologyRegistry.FromManifestJson(ReadShippedManifest());
        var fromCode = new EcologyRegistry();

        Assert.Equal(fromCode.Regions.Count, fromData.Regions.Count);
        Assert.Equal(fromCode.Regions.Select(r => r.Id), fromData.Regions.Select(r => r.Id));

        for (var i = 0; i < fromCode.Regions.Count; i++)
        {
            var c = fromCode.Regions[i];
            var d = fromData.Regions[i];
            Assert.Equal(c.Id, d.Id);
            Assert.Equal(c.DisplayName, d.DisplayName);
            Assert.Equal(c.MinX, d.MinX);
            Assert.Equal(c.MinY, d.MinY);
            Assert.Equal(c.MaxX, d.MaxX);
            Assert.Equal(c.MaxY, d.MaxY);
            Assert.Equal(c.Types.Count, d.Types.Count);
            foreach (var (typeId, config) in c.Types)
            {
                Assert.True(d.Types.TryGetValue(typeId, out var dConfig), $"{c.Id}: missing type '{typeId}' in the shipped manifest.");
                Assert.Equal(config.K, dConfig.K, 6);
                Assert.Equal(config.RPerMinute, dConfig.RPerMinute, 6);
                Assert.Equal(config.MaxLive, dConfig.MaxLive);
            }
        }
    }

    private static string ReadShippedManifest()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "Content", "ecology.json");
        if (File.Exists(shipped))
        {
            return File.ReadAllText(shipped);
        }

        // Fallback: walk up from the test output dir to the repo root and read the source manifest.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Mmo.Server", "Content", "ecology.json");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate the shipped ecology.json (neither the test output Content/ nor the source path).");
    }
}
