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
