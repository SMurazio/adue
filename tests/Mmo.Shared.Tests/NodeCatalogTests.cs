using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Population;
using Xunit;

namespace Mmo.Shared.Tests;

// NODE-FIELD N1 (docs/node-field-design.md D1/D2/D8): pins NodeCatalog's determinism contract, the
// pin-stability contract (authored T/R markers ALWAYS occupy indices [0, pinCount) no matter how the
// scatter class table changes), the absolute grass-only/no-marker-tile/in-bounds invariants, the D2
// away-from-road distribution shape (same acceptance style as WeightedScatterTests/DecorPlacerTests),
// the D8 total-count sanity floor, and CatalogHash's bump-detection + shipped-literal pin (D2 -- this
// hash rides ZoneInfo in N2 and hard-fails a drifted client, so it gets ContentHash-level test rigor).
public sealed class NodeCatalogTests
{
    // The REAL genVersion 2 map (town-blockout §4) -- exactly ONE TreePin (188, 22) and ONE RockPin
    // (204, 22), per TownAndFloor1MapTests.MarkersAreSevenHousesTwoPortalsAndTheTwoPins. Pin count = 2.
    private static readonly AuthoredMap RealMap = AuthoredMap.Parse(AuthoredMaps.TownAndFloor1);
    private const int RealMapPinCount = 2;

    private static AuthoredMap StripedRoadMap(int width, int height)
    {
        var mid = width / 2;
        var rows = new string[height];
        for (var y = 0; y < height; y++)
        {
            var chars = new char[width];
            for (var x = 0; x < width; x++)
            {
                chars[x] = x == mid || x == mid + 1 ? ',' : '.';
            }

            rows[y] = new string(chars);
        }

        return AuthoredMap.Parse(rows);
    }

    [Fact]
    public void SameSeedAndMap_ProducesIdenticalCatalogueAndHash()
    {
        var first = NodeCatalog.Build(777, RealMap);
        var second = NodeCatalog.Build(777, RealMap);

        Assert.Equal(first.Entries, second.Entries);
        Assert.Equal(first.CatalogHash, second.CatalogHash);
    }

    [Fact]
    public void DifferentSeed_ProducesDifferentCatalogueAndHash()
    {
        var first = NodeCatalog.Build(1, RealMap);
        var second = NodeCatalog.Build(2, RealMap);

        Assert.NotEqual(first.Entries, second.Entries);
        Assert.NotEqual(first.CatalogHash, second.CatalogHash);
    }

    [Fact]
    public void PinsOccupyIndices0ToPinCountMinusOne_RegardlessOfClassTable()
    {
        // Pin-stability contract (D1): adding/removing/retuning scatter classes must NEVER renumber the
        // authored pins. Build the SAME map/seed once with the shipped class table and once with a
        // deliberately different one (fewer classes, different tunables) and assert the leading
        // RealMapPinCount entries are byte-identical either way.
        var shipped = NodeCatalog.Build(42, RealMap);

        // A deliberately different, single-class table (different Type/TargetCount/MinSpacing/salt from
        // every shipped class) -- if pin placement depended on the class table in any way, this would
        // produce different leading entries. It must not.
        var altered = NodeCatalog.Build(
            42,
            RealMap,
            new[]
            {
                new NodeClass(
                    Type: NodeType.Plant,
                    Category: SurfaceCategory.Grass,
                    TargetCount: 7,
                    MinSpacing: 25,
                    BaseDensity: 0.2,
                    RoadSuppression: 0.9,
                    RoadFalloffTiles: 3,
                    NoiseCellScale: 20,
                    Salt: 0x1234),
            });

        Assert.True(shipped.Entries.Count > RealMapPinCount, "Expected some scatter entries beyond the pins.");
        Assert.True(altered.Entries.Count > RealMapPinCount, "Expected some scatter entries beyond the pins.");

        for (var i = 0; i < RealMapPinCount; i++)
        {
            Assert.Equal(shipped.Entries[i], altered.Entries[i]);
        }

        Assert.Equal(new TileCoord(188, 22), shipped.Entries[0].Tile);
        Assert.Equal(NodeType.Tree, shipped.Entries[0].NodeType);
        Assert.Equal(0, shipped.Entries[0].Index);

        Assert.Equal(new TileCoord(204, 22), shipped.Entries[1].Tile);
        Assert.Equal(NodeType.Rock, shipped.Entries[1].NodeType);
        Assert.Equal(1, shipped.Entries[1].Index);
    }

    [Fact]
    public void RealMap_EveryScatterEntryIsGrassWalkableAndOffAnyMarkerTile()
    {
        var catalog = NodeCatalog.Build(2026, RealMap);
        var markerTiles = new HashSet<TileCoord>(RealMap.Markers.Select(m => m.Tile));

        Assert.True(catalog.Entries.Count > RealMapPinCount, "Expected at least some scatter entries beyond the pins.");

        foreach (var entry in catalog.Entries.Skip(RealMapPinCount))
        {
            Assert.True(RealMap.IsInBounds(entry.Tile), $"Entry {entry.Index} tile {entry.Tile} is out of bounds.");
            Assert.True(RealMap.IsWalkable(entry.Tile), $"Entry {entry.Index} tile {entry.Tile} is not walkable.");
            Assert.Equal(SurfaceCategory.Grass, RealMap.CategoryAt(entry.Tile));
            Assert.DoesNotContain(entry.Tile, markerTiles);
        }
    }

    [Fact]
    public void RealMap_TotalEntryCountIsAtLeast4000()
    {
        // D8 targets ~5,000 total (3,500 + 1,000 + 500 + 2 pins). Pinned LOOSELY: exact yield is
        // content-tunable (map area, minSpacing, density curve), this only guards against a gross
        // regression (e.g. a class silently placing near-zero instances).
        var catalog = NodeCatalog.Build(0, RealMap);

        Assert.True(catalog.Entries.Count >= 4000, $"Only {catalog.Entries.Count} total catalogue entries on the real map.");
    }

    [Fact]
    public void AwayFromRoadDensityCurve_TreesSparserNearRoadThanFar()
    {
        const int width = 100;
        const int height = 60;
        var map = StripedRoadMap(width, height);

        var catalog = NodeCatalog.Build(314, map);
        var treeXs = catalog.Entries.Where(e => e.NodeType == NodeType.Tree).Select(e => e.Tile.X).ToList();

        Assert.True(treeXs.Count > 100, $"Too few tree placements ({treeXs.Count}) to judge distribution.");

        var nearCount = treeXs.Count(x => x is >= 45 and <= 54);
        var farCount = treeXs.Count(x => x <= 24 || x >= 75);

        var nearArea = 10 * height;
        var farArea = 50 * height;

        var nearDensity = (double)nearCount / nearArea;
        var farDensity = (double)farCount / farArea;

        Assert.True(
            nearDensity < farDensity,
            $"Expected near-road tree density ({nearDensity:F4}, {nearCount} in {nearArea}) to be strictly less " +
            $"than far-road density ({farDensity:F4}, {farCount} in {farArea}).");
    }

    [Fact]
    public void CatalogHash_ChangesWhenClassTableChanges()
    {
        var baseline = NodeCatalog.Build(42, RealMap);

        var perturbed = NodeClassTable.Classes
            .Select(c => c.Type == NodeType.Tree ? c with { TargetCount = c.TargetCount - 500 } : c)
            .ToList();
        var altered = NodeCatalog.Build(42, RealMap, perturbed);

        Assert.NotEqual(baseline.CatalogHash, altered.CatalogHash);
    }

    // ==================== THE SHIPPED HASH — NEVER UPDATE THIS WITHOUT A CONSCIOUS RE-PIN ====================
    // FLAG FOR THE ORCHESTRATOR (mirrors the M3 F1 process behind
    // TownAndFloor1MapTests.ShippedTownAndFloor1ContentHash): N2 (docs/node-field-design.md, the accepted N1
    // fork) added the "candidates require >= 1 walkable 4-neighbour" adjacency guarantee to the scatter
    // predicate (NodeCatalog.Build) -- an INTENTIONAL, conscious change to which tiles the scatter classes can
    // land on, which moves this hash by construction. The implementer making that change has NO test-runner
    // access and cannot compute the new literal independently, so it is reset to an OBVIOUS placeholder (0UL).
    // Run this test once via the standard gate, read the ACTUAL computed hash out of the assertion failure
    // message, and paste it in below -- at that point it becomes a real "never silently update" pin again,
    // exactly like the terrain one. Do NOT guess a value; do NOT delete this test to make the suite green
    // without filling it in.
    // RE-PINNED 2026-07-04 for the forest densification (user feel-test: "even more forest" — Tree
    // 3500->6500 @ spacing 2, Plant 500->800; an INTENTIONAL catalogue change, moved in this same commit).
    // NEVER retune this literal to make an UNINTENTIONAL change green: a silently moved value hard-fails
    // every deployed client's ZoneInfo catalogue-drift check.
    // BOSS-1 REPIN: the Sunderer arena stamps its 22x22 interior as DungeonStone (a NON-grass surface), which masks
    // the Grass-only node scatter out of those tiles — an INTENTIONAL catalogue change that moves this hash by
    // construction. The literal below is STALE; the orchestrator runs the gate once, reads the actual computed hash
    // from this assertion's failure, and pastes it in (the M3 F1 process). Do NOT guess a value or delete the test.
    // REPINNED 2026-07-05 from the BOSS-1 gate run's actual computed value.
    // ADUE P2-A REPIN: the practice room ALSO stamps its 22x22 interior as DungeonStone, masking the Grass-only node
    // scatter out of those NW-corner tiles too — a SECOND intentional catalogue change that moves this hash AGAIN. The
    // literal below is STALE (still the BOSS-1 value); same process — the orchestrator runs the gate once, reads the
    // actual computed hash from this assertion's failure, and pastes it in. Do NOT guess a value or delete the test.
    // REPINNED 2026-08-09 from the ADUE P2-A gate run's actual computed value (practice-room DungeonStone stamp).
    private const ulong ShippedRealMapSeedZeroCatalogHash = 4830951142581154498UL;

    [Fact]
    public void CatalogHashForRealMapSeedZero_IsPinnedToShippedLiteral()
    {
        Assert.Equal(ShippedRealMapSeedZeroCatalogHash, NodeCatalog.Build(0, RealMap).CatalogHash);
    }
}
