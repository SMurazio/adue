using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Shared.Tests;

public sealed class TerrainGeneratorTests
{
    [Theory]
    [InlineData(64, 64, 0)]
    [InlineData(128, 128, 0)]
    [InlineData(256, 256, 7)]
    [InlineData(2048, 2048, 42)]
    public void GenerateIsDeterministicAcrossCalls(int width, int height, int seed)
    {
        var first = TerrainGenerator.Generate(width, height, seed, TerrainGenerator.CurrentGenVersion);
        var second = TerrainGenerator.Generate(width, height, seed, TerrainGenerator.CurrentGenVersion);

        // Same inputs MUST yield the identical sequence (same order, same tiles).
        Assert.Equal(first, second);
        Assert.Equal(
            TerrainGenerator.ContentHash(first),
            TerrainGenerator.ContentHash(second));
    }

    [Fact]
    public void GenerateEmitsCanonicalRowMajorOrder()
    {
        var blocked = TerrainGenerator.Generate(128, 128, 0, TerrainGenerator.CurrentGenVersion);

        for (var i = 1; i < blocked.Count; i++)
        {
            var prev = blocked[i - 1];
            var current = blocked[i];
            var ordered = prev.Y < current.Y || (prev.Y == current.Y && prev.X < current.X);
            Assert.True(ordered, $"Tiles not in canonical row-major order at index {i}: {prev} then {current}.");
        }
    }

    [Fact]
    public void GenerateReproducesLegacyDefaultMap()
    {
        var blocked = TerrainGenerator.Generate(128, 128, 0, TerrainGenerator.CurrentGenVersion);
        var set = new HashSet<TileCoord>(blocked);

        // Perimeter border present on all four edges.
        Assert.Contains(new TileCoord(0, 0), set);
        Assert.Contains(new TileCoord(127, 0), set);
        Assert.Contains(new TileCoord(0, 127), set);
        Assert.Contains(new TileCoord(127, 127), set);

        // The three historical interior segments: vertical x=16 (y 8..20), horizontal y=24 (x 20..36),
        // vertical x=40 (y 12..18).
        Assert.Contains(new TileCoord(16, 8), set);
        Assert.Contains(new TileCoord(16, 20), set);
        Assert.Contains(new TileCoord(20, 24), set);
        Assert.Contains(new TileCoord(36, 24), set);
        Assert.Contains(new TileCoord(40, 12), set);
        Assert.Contains(new TileCoord(40, 18), set);

        // The legacy default spawn tile is carved back open.
        Assert.DoesNotContain(new TileCoord(8, 8), set);
    }

    [Fact]
    public void ContentHashIsStableForFixedInputs()
    {
        // Pins genVersion 1 / 64x64 / seed 0 so an accidental layout change is caught loudly. If a
        // future change to genVersion 1 is intentional, bump CurrentGenVersion instead of editing v1.
        var hash = TerrainGenerator.ContentHash(64, 64, 0, 1);
        var blocked = TerrainGenerator.Generate(64, 64, 0, 1);

        Assert.Equal(TerrainGenerator.ContentHash(blocked), hash);
        // The known-good value for the current genVersion-1 64x64 seed-0 layout.
        Assert.Equal(ExpectedHash64x64Seed0(), hash);
    }

    [Theory]
    [InlineData(64, 64, 0x3975429411B4ED3CUL)]
    [InlineData(128, 128, 0x4B7B8207799D7249UL)]
    public void GenVersion1HashIsPinnedToHistoricalLiteral(int width, int height, ulong expected)
    {
        // AUTHORED-MAP M1 regression pin: LITERAL genVersion-1 hash values, computed by an independent
        // out-of-process replication of the documented algorithm BEFORE the generator was refactored to
        // the layout result shape. If either moves, genVersion 1 output moved — every deployed client
        // would hard-fail the ZoneInfo drift check against an updated server. Never "fix" this test by
        // updating the literals; fix the generator (or ship the change as a NEW genVersion).
        Assert.Equal(expected, TerrainGenerator.ContentHash(width, height, 0, 1));
        // The seed is unused by the fixed v1 layout, so the hash is seed-independent too.
        Assert.Equal(expected, TerrainGenerator.ContentHash(width, height, 12345, 1));
    }

    [Fact]
    public void GenVersion1LayoutHasNoAuthoredDataAndDefaultsToGrass()
    {
        // The layout result shape must be a pure superset for genVersion 1: same blocked tiles, the
        // SAME blocked-only hash, no authored payload, and the historical defaults everywhere.
        var layout = TerrainGenerator.GenerateLayout(64, 64, 0, 1);

        Assert.Null(layout.Authored);
        Assert.Equal(TerrainGenerator.Generate(64, 64, 0, 1), layout.BlockedTiles);
        Assert.Equal(TerrainGenerator.ContentHash(layout.BlockedTiles), layout.ContentHash);
        Assert.Empty(layout.SpawnTiles);
        Assert.Empty(layout.Markers);
        Assert.Equal(SurfaceCategory.Grass, layout.CategoryAt(new TileCoord(5, 5)));
    }

    [Fact]
    public void GenVersion2ReturnsTheEmbeddedAuthoredMap()
    {
        var map = AuthoredMap.Parse(AuthoredMaps.TownAndFloor1);
        var layout = TerrainGenerator.GenerateLayout(map.Width, map.Height, 0, TerrainGenerator.AuthoredGenVersion);

        Assert.NotNull(layout.Authored);
        Assert.Equal(map.BlockedTiles, layout.BlockedTiles);
        Assert.Equal(map.SpawnTiles, layout.SpawnTiles);
        Assert.Equal(map.Markers, layout.Markers);
        Assert.Equal(TerrainGenerator.ContentHash(map), layout.ContentHash);

        // The seed is intentionally unused by an authored layout: any seed, identical output.
        Assert.Equal(
            layout.ContentHash,
            TerrainGenerator.ContentHash(map.Width, map.Height, 999, TerrainGenerator.AuthoredGenVersion));
    }

    [Fact]
    public void GenVersion2HashCoversMoreThanTheBlockedSet()
    {
        // The authored ContentHash must NOT equal a blocked-only re-hash — that inequality is what
        // guarantees category/marker/spawn drift hard-fails instead of hiding behind identical walls.
        // Dimensions come from the embedded grid itself so this survives M3 swapping in the real map.
        var map = AuthoredMap.Parse(AuthoredMaps.TownAndFloor1);
        var layout = TerrainGenerator.GenerateLayout(map.Width, map.Height, 0, TerrainGenerator.AuthoredGenVersion);
        Assert.NotEqual(TerrainGenerator.ContentHash(layout.BlockedTiles), layout.ContentHash);
    }

    [Fact]
    public void GenVersion2DimensionMismatchThrows()
    {
        // ZoneInfo carries dimensions on the wire; a server configured with a size that disagrees with
        // the authored grid must fail loudly at generation, not produce a world unlike its content.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TerrainGenerator.GenerateLayout(64, 64, 0, TerrainGenerator.AuthoredGenVersion));
    }

    [Fact]
    public void DifferentDimensionsProduceDifferentHashes()
    {
        var a = TerrainGenerator.ContentHash(64, 64, 0, 1);
        var b = TerrainGenerator.ContentHash(128, 128, 0, 1);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void UnsupportedGenVersionThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TerrainGenerator.Generate(64, 64, 0, 999));
    }

    // Computes the expected hash via an independent FNV-1a implementation over the canonically-ordered
    // tiles, so the pin verifies the algorithm rather than tautologically echoing it.
    private static ulong ExpectedHash64x64Seed0()
    {
        var blocked = TerrainGenerator.Generate(64, 64, 0, 1);
        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        var hash = fnvOffset;

        void Mix(int value)
        {
            var u = (uint)value;
            for (var shift = 0; shift < 32; shift += 8)
            {
                hash ^= (byte)(u >> shift);
                hash *= fnvPrime;
            }
        }

        Mix(blocked.Count);
        foreach (var tile in blocked)
        {
            Mix(tile.X);
            Mix(tile.Y);
        }

        return hash;
    }
}
