using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Population;
using Xunit;

namespace Mmo.Shared.Tests;

// The genVersion 3 base-camp map (ADUE base-camp reframe, docs/duo-base-camp-reframe.md), authored in
// commit 1 while the live world is STILL genVersion 2. Pins the layout's load-bearing facts — dims, the
// walkable non-grass camp island, the clustered spawn anchors, the two SEALED teleport-only pockets, the
// no-orphan reachability invariant, and the ContentHash / (empty) CatalogHash literals. Mirrors
// TownAndFloor1MapTests; leaves that file (and the whole v2 suite) untouched.
public sealed class BaseCampMapTests
{
    private static readonly AuthoredMap Map = AuthoredMap.Parse(AuthoredMaps.BaseCamp);

    // Pocket interiors (exterior inset by the 1-tile wall ring) — the reachability carve-outs.
    private const int PracticeInteriorMinX = AuthoredMaps.BaseCampPracticeExteriorMinX + 1;
    private const int PracticeInteriorMinY = AuthoredMaps.BaseCampPracticeExteriorMinY + 1;
    private const int PracticeInteriorMaxX = AuthoredMaps.BaseCampPracticeExteriorMaxX - 1;
    private const int PracticeInteriorMaxY = AuthoredMaps.BaseCampPracticeExteriorMaxY - 1;
    private const int BossInteriorMinX = AuthoredMaps.BaseCampBossExteriorMinX + 1;
    private const int BossInteriorMinY = AuthoredMaps.BaseCampBossExteriorMinY + 1;
    private const int BossInteriorMaxX = AuthoredMaps.BaseCampBossExteriorMaxX - 1;
    private const int BossInteriorMaxY = AuthoredMaps.BaseCampBossExteriorMaxY - 1;

    // ============================ THE SHIPPED HASH — FILL VIA THE GATE ============================
    // ADUE REFRAME REPIN: the genVersion 3 base-camp map is BRAND NEW content, so its ContentHash has no
    // prior value. The implementer has no test-runner access to compute it — the literal below is a STALE
    // placeholder (0UL). The orchestrator runs the gate once, reads the actual computed hash out of this
    // assertion's failure message, and pastes it in (the M3-F1 process, same as
    // TownAndFloor1MapTests.ShippedTownAndFloor1ContentHash). Do NOT guess a value or delete the test.
    // REPINNED 2026-08-09 from the ADUE-reframe gate run's actual computed value (new BaseCamp genVersion-3 map).
    private const ulong ShippedBaseCampContentHash = 16048708980041123436UL;

    [Fact]
    public void ContentHashIsPinnedToShippedLiteral()
    {
        Assert.Equal(ShippedBaseCampContentHash, TerrainGenerator.ContentHash(Map));
        // And the generator serves exactly that layout for the (dims, any-seed, genVersion 3) descriptor.
        Assert.Equal(
            ShippedBaseCampContentHash,
            TerrainGenerator.ContentHash(Map.Width, Map.Height, 0, TerrainGenerator.BaseCampGenVersion));
    }

    [Fact]
    public void DimensionsMatchTheAdvertisedConstants()
    {
        // The commit-2 ServerOptions v3 derivation will read these constants; the emitted grid must have
        // them or that boot default would be a guaranteed generator throw.
        Assert.Equal(AuthoredMaps.BaseCampWidth, Map.Width);
        Assert.Equal(AuthoredMaps.BaseCampHeight, Map.Height);
        Assert.Equal(48, Map.Width);
        Assert.Equal(48, Map.Height);
    }

    [Fact]
    public void CampIslandIsWalkableAndNonGrass()
    {
        // The one place the pair stands: a 16x16 walkable COBBLE platform (non-grass, so it masks node
        // scatter out for free — the BossArena trick).
        for (var y = AuthoredMaps.BaseCampIslandMinY; y <= AuthoredMaps.BaseCampIslandMaxY; y++)
        {
            for (var x = AuthoredMaps.BaseCampIslandMinX; x <= AuthoredMaps.BaseCampIslandMaxX; x++)
            {
                var tile = new TileCoord(x, y);
                Assert.True(Map.IsWalkable(tile), $"camp island tile {tile} is not walkable");
                Assert.NotEqual(SurfaceCategory.Grass, Map.CategoryAt(tile));
                Assert.Equal(SurfaceCategory.Cobble, Map.CategoryAt(tile));
            }
        }

        // The entire walkable map is non-grass — no walkable Grass tile exists anywhere (that is what
        // guarantees the empty catalogue below). Blocked walls/void carry the default Grass category but
        // are never walkable, so this scans only walkable tiles.
        for (var y = 0; y < Map.Height; y++)
        {
            for (var x = 0; x < Map.Width; x++)
            {
                var tile = new TileCoord(x, y);
                if (Map.IsWalkable(tile))
                {
                    Assert.NotEqual(SurfaceCategory.Grass, Map.CategoryAt(tile));
                }
            }
        }
    }

    [Fact]
    public void SpawnAnchorsAreClusteredOnTheCampIsland()
    {
        // The pair wakes a few tiles apart, together, near the island centre.
        Assert.Equal(
            new[]
            {
                new TileCoord(22, 11),
                new TileCoord(25, 11),
                new TileCoord(22, 12),
                new TileCoord(25, 12),
            },
            Map.SpawnTiles);

        foreach (var spawn in Map.SpawnTiles)
        {
            Assert.True(Map.IsWalkable(spawn));
            Assert.Equal(SurfaceCategory.Cobble, Map.CategoryAt(spawn));
            // On the island, and clustered (every anchor within a few tiles of every other).
            Assert.InRange(spawn.X, AuthoredMaps.BaseCampIslandMinX, AuthoredMaps.BaseCampIslandMaxX);
            Assert.InRange(spawn.Y, AuthoredMaps.BaseCampIslandMinY, AuthoredMaps.BaseCampIslandMaxY);
        }

        foreach (var a in Map.SpawnTiles)
        {
            foreach (var b in Map.SpawnTiles)
            {
                Assert.True(
                    Math.Abs(a.X - b.X) <= 4 && Math.Abs(a.Y - b.Y) <= 4,
                    $"spawn anchors {a} and {b} are not clustered (the pair must land together).");
            }
        }
    }

    [Fact]
    public void BothPocketsAreSealedDungeonStonePockets()
    {
        // Each pocket: a 1-tile wall ring around a 22x22 DungeonStone floor, NO mouth. Byte-identical in
        // SHAPE to its live-world twin (the gated-combat interior geometry is interior-relative and does
        // not move — commit 2 re-points the shared BossArena/PracticeRoom consts at these origins).
        AssertSealedPocket(
            AuthoredMaps.BaseCampPracticeExteriorMinX, AuthoredMaps.BaseCampPracticeExteriorMinY,
            AuthoredMaps.BaseCampPracticeExteriorMaxX, AuthoredMaps.BaseCampPracticeExteriorMaxY);
        AssertSealedPocket(
            AuthoredMaps.BaseCampBossExteriorMinX, AuthoredMaps.BaseCampBossExteriorMinY,
            AuthoredMaps.BaseCampBossExteriorMaxX, AuthoredMaps.BaseCampBossExteriorMaxY);

        // 24x24 rooms.
        Assert.Equal(24, AuthoredMaps.BaseCampPracticeExteriorMaxX - AuthoredMaps.BaseCampPracticeExteriorMinX + 1);
        Assert.Equal(24, AuthoredMaps.BaseCampPracticeExteriorMaxY - AuthoredMaps.BaseCampPracticeExteriorMinY + 1);
        Assert.Equal(24, AuthoredMaps.BaseCampBossExteriorMaxX - AuthoredMaps.BaseCampBossExteriorMinX + 1);
        Assert.Equal(24, AuthoredMaps.BaseCampBossExteriorMaxY - AuthoredMaps.BaseCampBossExteriorMinY + 1);

        // The two pockets must not overlap (adjacent, disjoint — practice west, boss east of the north half).
        Assert.False(
            AuthoredMaps.BaseCampPracticeExteriorMaxX >= AuthoredMaps.BaseCampBossExteriorMinX
            && AuthoredMaps.BaseCampBossExteriorMaxX >= AuthoredMaps.BaseCampPracticeExteriorMinX
            && AuthoredMaps.BaseCampPracticeExteriorMaxY >= AuthoredMaps.BaseCampBossExteriorMinY
            && AuthoredMaps.BaseCampBossExteriorMaxY >= AuthoredMaps.BaseCampPracticeExteriorMinY,
            "the two base-camp pockets must not overlap");
    }

    private static void AssertSealedPocket(int exMinX, int exMinY, int exMaxX, int exMaxY)
    {
        for (var x = exMinX; x <= exMaxX; x++)
        {
            Assert.True(Map.IsBlocked(new TileCoord(x, exMinY)), $"pocket south wall gap at x={x}");
            Assert.True(Map.IsBlocked(new TileCoord(x, exMaxY)), $"pocket north wall gap at x={x}");
        }

        for (var y = exMinY; y <= exMaxY; y++)
        {
            Assert.True(Map.IsBlocked(new TileCoord(exMinX, y)), $"pocket west wall gap at y={y}");
            Assert.True(Map.IsBlocked(new TileCoord(exMaxX, y)), $"pocket east wall gap at y={y}");
        }

        for (var y = exMinY + 1; y <= exMaxY - 1; y++)
        {
            for (var x = exMinX + 1; x <= exMaxX - 1; x++)
            {
                var tile = new TileCoord(x, y);
                Assert.True(Map.IsWalkable(tile), $"pocket interior tile {tile} is not walkable");
                Assert.Equal(SurfaceCategory.DungeonStone, Map.CategoryAt(tile));
            }
        }
    }

    [Fact]
    public void EveryWalkableTileIsReachableFromEverySpawn_ExceptTheSealedPockets()
    {
        // The no-orphan-pockets invariant on the base camp: from every `S`, EVERY non-pocket walkable
        // tile is reachable and NO pocket interior is. The camp island is the only non-pocket walkable
        // region (256 tiles); the two 22x22 pockets (484 each) are sealed — total walkable 1224.
        Assert.NotEmpty(Map.SpawnTiles);

        var sealedTiles = 0;
        for (var y = PracticeInteriorMinY; y <= PracticeInteriorMaxY; y++)
        {
            for (var x = PracticeInteriorMinX; x <= PracticeInteriorMaxX; x++)
            {
                if (Map.IsWalkable(new TileCoord(x, y)))
                {
                    sealedTiles++;
                }
            }
        }

        for (var y = BossInteriorMinY; y <= BossInteriorMaxY; y++)
        {
            for (var x = BossInteriorMinX; x <= BossInteriorMaxX; x++)
            {
                if (Map.IsWalkable(new TileCoord(x, y)))
                {
                    sealedTiles++;
                }
            }
        }

        Assert.Equal(484 + 484, sealedTiles);
        Assert.Equal(256 + 484 + 484, Map.WalkableTileCount);

        foreach (var spawn in Map.SpawnTiles)
        {
            var reached = Map.FloodFillWalkableFrom(spawn);
            foreach (var tile in reached)
            {
                Assert.False(
                    InInterior(tile, PracticeInteriorMinX, PracticeInteriorMinY, PracticeInteriorMaxX, PracticeInteriorMaxY),
                    $"sealed practice pocket tile {tile} reachable on foot from spawn {spawn}");
                Assert.False(
                    InInterior(tile, BossInteriorMinX, BossInteriorMinY, BossInteriorMaxX, BossInteriorMaxY),
                    $"sealed boss pocket tile {tile} reachable on foot from spawn {spawn}");
            }

            // Everything NOT sealed is one connected region reachable from every spawn (the camp island).
            Assert.Equal(Map.WalkableTileCount - sealedTiles, reached.Count);
        }
    }

    private static bool InInterior(TileCoord tile, int minX, int minY, int maxX, int maxY) =>
        tile.X >= minX && tile.X <= maxX && tile.Y >= minY && tile.Y <= maxY;

    // ============================ THE SHIPPED CATALOG HASH — FILL VIA THE GATE ============================
    // ADUE REFRAME REPIN: the base camp is ALL non-grass (cobble island + DungeonStone pockets) with no
    // T/R pins, so its NodeCatalog is EMPTY — the scatter classes are Grass-only and there is no walkable
    // Grass tile to land on. An empty catalogue's hash is a fixed value (NodeCatalog.Empty().CatalogHash);
    // the assertions below prove the catalogue really is empty regardless of seed. This literal is a STALE
    // placeholder (0UL) for the ContentHash-style pin — the orchestrator runs the gate once, reads the
    // actual computed hash out of the failure, and pastes it in (never guessed). Do NOT delete the test.
    // REPINNED 2026-08-09 from the ADUE-reframe gate run's actual computed value (empty BaseCamp catalogue).
    private const ulong ShippedBaseCampSeedZeroCatalogHash = 5558979605539197941UL;

    [Fact]
    public void CatalogueIsEmptyAndHashIsPinnedToShippedLiteral()
    {
        var catalog = NodeCatalog.Build(0, Map);

        // All-non-grass camp ⇒ empty catalogue: no pins, no scatter.
        Assert.Empty(catalog.Entries);
        Assert.Equal(NodeCatalog.Empty().CatalogHash, catalog.CatalogHash);

        // Seed-independent (nothing to scatter, so the seed can change nothing).
        Assert.Equal(catalog.CatalogHash, NodeCatalog.Build(12345, Map).CatalogHash);

        Assert.Equal(ShippedBaseCampSeedZeroCatalogHash, catalog.CatalogHash);
    }

    [Fact]
    public void GeneratorRoundTripsThroughGenVersion3()
    {
        // genVersion 3 is authored + generatable NOW (the world only FLIPS to it in commit 2). The parsed
        // BaseCamp map and the generator's genVersion-3 layout must be the same content.
        var layout = TerrainGenerator.GenerateLayout(
            AuthoredMaps.BaseCampWidth, AuthoredMaps.BaseCampHeight, 0, TerrainGenerator.BaseCampGenVersion);

        Assert.NotNull(layout.Authored);
        Assert.Equal(TerrainGenerator.ContentHash(Map), layout.ContentHash);
        Assert.Equal(Map.BlockedTiles, layout.BlockedTiles);

        // Round-trips through ToAsciiRows exactly, like the shipped maps.
        Assert.Equal(AuthoredMaps.BaseCamp, Map.ToAsciiRows());
    }
}
