using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Shared.Tests;

public sealed class AuthoredMapTests
{
    // A minimal grid exercising EVERY alphabet char (town-blockout D3):
    // walls, all four walkable surfaces, water, a spawn anchor, all four markers, out-of-world padding.
    // Laid out so every walkable tile is reachable from S (the flood-fill tests rely on that).
    private static readonly string[] AlphabetRows =
    [
        "#####",
        "#.,:#",
        "#-S~#",
        "#HPT#",
        "#R# #",
        "#####",
    ];

    [Fact]
    public void ParseClassifiesEveryAlphabetChar()
    {
        var map = AuthoredMap.Parse(AlphabetRows);

        Assert.Equal(5, map.Width);
        Assert.Equal(6, map.Height);

        // Walls: blocked, NOT out-of-world, default (Grass) category.
        Assert.True(map.IsBlocked(new TileCoord(0, 0)));
        Assert.False(map.IsOutOfWorld(new TileCoord(0, 0)));
        Assert.Equal(SurfaceCategory.Grass, map.CategoryAt(0, 0));
        Assert.True(map.IsBlocked(new TileCoord(2, 4)));

        // Space: blocked AND out-of-world.
        Assert.True(map.IsBlocked(new TileCoord(3, 4)));
        Assert.True(map.IsOutOfWorld(new TileCoord(3, 4)));
        Assert.Equal(new[] { new TileCoord(3, 4) }, map.OutOfWorldTiles);

        // Water: blocked but a real painted surface.
        Assert.True(map.IsBlocked(new TileCoord(3, 2)));
        Assert.False(map.IsOutOfWorld(new TileCoord(3, 2)));
        Assert.Equal(SurfaceCategory.Water, map.CategoryAt(3, 2));

        // The four plain walkable surfaces.
        Assert.True(map.IsWalkable(new TileCoord(1, 1)));
        Assert.Equal(SurfaceCategory.Grass, map.CategoryAt(1, 1));
        Assert.Equal(SurfaceCategory.Dirt, map.CategoryAt(2, 1));
        Assert.Equal(SurfaceCategory.Cobble, map.CategoryAt(3, 1));
        Assert.Equal(SurfaceCategory.DungeonStone, map.CategoryAt(1, 2));

        // Spawn anchor: walkable COBBLE (D3) and listed.
        Assert.True(map.IsWalkable(new TileCoord(2, 2)));
        Assert.Equal(SurfaceCategory.Cobble, map.CategoryAt(2, 2));
        Assert.Equal(new[] { new TileCoord(2, 2) }, map.SpawnTiles);

        // Markers: walkable GRASS tiles (D3), emitted in row-major scan order.
        Assert.Equal(
            new[]
            {
                new AuthoredMarker(AuthoredMarkerKind.House, new TileCoord(1, 3)),
                new AuthoredMarker(AuthoredMarkerKind.Portal, new TileCoord(2, 3)),
                new AuthoredMarker(AuthoredMarkerKind.TreePin, new TileCoord(3, 3)),
                new AuthoredMarker(AuthoredMarkerKind.RockPin, new TileCoord(1, 4)),
            },
            map.Markers);
        foreach (var marker in map.Markers)
        {
            Assert.True(map.IsWalkable(marker.Tile));
            Assert.Equal(SurfaceCategory.Grass, map.CategoryAt(marker.Tile));
        }

        // Walkable count (the flood-fill target): 9 walkable tiles — the 4 plain surfaces, S, and the
        // 4 marker tiles; the other 21 of 30 are wall/water/space. Independently counted from the grid.
        Assert.Equal(9, map.WalkableTileCount);
        Assert.Equal(21, map.BlockedTiles.Count);
    }

    [Fact]
    public void ParseEmitsBlockedInCanonicalRowMajorOrder()
    {
        // Same canonical-order invariant TerrainGenerator's blocked list has: hashing depends on it.
        var map = AuthoredMap.Parse(AlphabetRows);
        var blocked = map.BlockedTiles;

        for (var i = 1; i < blocked.Count; i++)
        {
            var prev = blocked[i - 1];
            var current = blocked[i];
            var ordered = prev.Y < current.Y || (prev.Y == current.Y && prev.X < current.X);
            Assert.True(ordered, $"Blocked tiles not in canonical row-major order at index {i}: {prev} then {current}.");
        }
    }

    [Fact]
    public void UnknownCharThrowsWithPosition()
    {
        var rows = new[]
        {
            "###",
            "#X#",
            "###",
        };

        var exception = Assert.Throws<ArgumentException>(() => AuthoredMap.Parse(rows));
        // Fail LOUD and locatable: the message names the char and its coordinates.
        Assert.Contains("'X'", exception.Message);
        Assert.Contains("column 1", exception.Message);
        Assert.Contains("row 1", exception.Message);
    }

    [Fact]
    public void RaggedRowsThrow()
    {
        var rows = new[]
        {
            "####",
            "#.#",
            "####",
        };

        var exception = Assert.Throws<ArgumentException>(() => AuthoredMap.Parse(rows));
        Assert.Contains("ragged", exception.Message);
    }

    [Fact]
    public void EmptyOrNullInputThrows()
    {
        Assert.Throws<ArgumentNullException>(() => AuthoredMap.Parse(null!));
        Assert.Throws<ArgumentException>(() => AuthoredMap.Parse([]));
        Assert.Throws<ArgumentException>(() => AuthoredMap.Parse([string.Empty]));
        Assert.Throws<ArgumentException>(() => AuthoredMap.Parse(["##", null!]));
    }

    [Fact]
    public void ParseIsDeterministicAcrossCalls()
    {
        var first = AuthoredMap.Parse(AlphabetRows);
        var second = AuthoredMap.Parse(AlphabetRows);

        Assert.Equal(first.BlockedTiles, second.BlockedTiles);
        Assert.Equal(first.SpawnTiles, second.SpawnTiles);
        Assert.Equal(first.Markers, second.Markers);
        Assert.Equal(first.OutOfWorldTiles, second.OutOfWorldTiles);
        Assert.Equal(TerrainGenerator.ContentHash(first), TerrainGenerator.ContentHash(second));
    }

    [Fact]
    public void FloodFillCoversAllWalkableFromSpawn()
    {
        // The no-orphan-pockets invariant (town-blockout §4): from any S, EVERY walkable tile is
        // reachable. Checked on both the alphabet grid and the embedded genVersion 2 map — the same
        // reusable helper M3's real 192x192 map test will use.
        foreach (var rows in new[] { AlphabetRows, AuthoredMaps.TownAndFloor1 })
        {
            var map = AuthoredMap.Parse(rows);
            Assert.NotEmpty(map.SpawnTiles);
            foreach (var spawn in map.SpawnTiles)
            {
                Assert.True(map.AllWalkableReachableFrom(spawn), $"Orphan walkable pocket: not all walkable tiles reachable from spawn {spawn}.");
                Assert.Equal(map.WalkableTileCount, map.FloodFillWalkableFrom(spawn).Count);
            }
        }
    }

    [Fact]
    public void FloodFillDetectsOrphanPocket()
    {
        // A walled-off walkable pocket (bottom row) must FAIL the reachability invariant — this is the
        // failure mode the test exists to catch on real authored content.
        var rows = new[]
        {
            "#####",
            "#S..#",
            "#####",
            "#...#",
            "#####",
        };

        var map = AuthoredMap.Parse(rows);
        var spawn = Assert.Single(map.SpawnTiles);

        Assert.False(map.AllWalkableReachableFrom(spawn));
        // Exactly the top corridor (S + two grass tiles) is reachable; the pocket's 3 tiles are not.
        Assert.Equal(3, map.FloodFillWalkableFrom(spawn).Count);
        Assert.Equal(6, map.WalkableTileCount);
    }

    [Fact]
    public void FloodFillFromNonWalkableStartThrows()
    {
        var map = AuthoredMap.Parse(AlphabetRows);
        Assert.Throws<ArgumentException>(() => map.FloodFillWalkableFrom(new TileCoord(0, 0)));   // wall
        Assert.Throws<ArgumentException>(() => map.FloodFillWalkableFrom(new TileCoord(99, 99))); // out of bounds
    }

    [Fact]
    public void ContentHashChangesWhenAsciiChanges()
    {
        // The hash must cover the WHOLE authored layout, so edits that leave the blocked set intact —
        // recoloring a surface, retyping a marker, demoting a spawn, walling out-of-world — must all
        // move the hash (a blocked-only hash would let them drift silently between client and server).
        var baseline = TerrainGenerator.ContentHash(AuthoredMap.Parse(AlphabetRows));

        var variants = new (string Reason, string[] Rows)[]
        {
            ("category-only: '.' -> ',' (blocked set unchanged)", ["#####", "#,,:#", "#-S~#", "#HPT#", "#R# #", "#####"]),
            ("marker kind: 'H' -> 'P' (same tile, still walkable grass)", ["#####", "#.,:#", "#-S~#", "#PPT#", "#R# #", "#####"]),
            ("spawn demoted: 'S' -> ':' (same category, same walkability)", ["#####", "#.,:#", "#-:~#", "#HPT#", "#R# #", "#####"]),
            ("wall vs out-of-world: '#' -> ' ' (blocked set unchanged)", ["#####", "#.,:#", "#-S~#", "#HPT#", "#R#  ", "#####"]),
            ("geometry: '.' -> '#' (the classic blocked-set change)", ["#####", "##,:#", "#-S~#", "#HPT#", "#R# #", "#####"]),
        };

        foreach (var (reason, rows) in variants)
        {
            var variantHash = TerrainGenerator.ContentHash(AuthoredMap.Parse(rows));
            Assert.True(baseline != variantHash, $"ContentHash failed to change for: {reason}");
        }
    }

    [Fact]
    public void EmbeddedTownAndFloor1ExercisesEveryAlphabetChar()
    {
        // M1's contract: the placeholder genVersion 2 map keeps EVERY alphabet char under test until
        // M3 replaces the rows with the real content (which then re-satisfies its own invariants).
        var used = new HashSet<char>(string.Concat(AuthoredMaps.TownAndFloor1));
        foreach (var required in "#.,:-~SHPTR ")
        {
            Assert.Contains(required, used);
        }
    }
}
