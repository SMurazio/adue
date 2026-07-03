using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Shared.Tests;

// The REAL genVersion 2 map content (town-blockout §4, authored by M3). These tests pin the layout's
// load-bearing facts — dims, spawn anchors, prop markers, the structural landmarks, the no-orphan
// reachability invariant, and the shipped ContentHash literal.
public sealed class TownAndFloor1MapTests
{
    // ============================ THE SHIPPED HASH — NEVER UPDATE THIS ============================
    // The genVersion 2 ContentHash of the shipped TownAndFloor1 map, computed by an INDEPENDENT
    // out-of-process replication of the documented FNV-1a chain (TerrainGenerator.ContentHash order:
    // blocked count/X/Y, W, H, category bytes row-major, spawns, marker kind/X/Y, out-of-world) over
    // an independent expansion of the same stamp program. If this assert ever fails, genVersion 2's
    // shipped layout MOVED — every deployed client would hard-fail the ZoneInfo drift check against
    // an updated server. Never "fix" the test by updating this literal to silence a diff; either
    // revert the accidental map/hash change, or — for a DELIBERATE map edit — ship it consciously as
    // the new world (one commit, client+server together) and recompute this pin as part of that
    // decision (M1 review F1).
    private const ulong ShippedTownAndFloor1ContentHash = 0x323B2EBD502EA05EUL;

    private static readonly AuthoredMap Map = AuthoredMap.Parse(AuthoredMaps.TownAndFloor1);

    [Fact]
    public void ContentHashIsPinnedToShippedLiteral()
    {
        Assert.Equal(ShippedTownAndFloor1ContentHash, TerrainGenerator.ContentHash(Map));
        // And the generator serves exactly that layout for the (dims, any-seed, genVersion 2) descriptor.
        Assert.Equal(
            ShippedTownAndFloor1ContentHash,
            TerrainGenerator.ContentHash(Map.Width, Map.Height, 0, TerrainGenerator.AuthoredGenVersion));
    }

    [Fact]
    public void DimensionsMatchTheAdvertisedConstants()
    {
        // ServerOptions derives its world-size defaults from these constants; the emitted grid must
        // actually have them or the boot default would be a guaranteed generator throw.
        Assert.Equal(AuthoredMaps.TownAndFloor1Width, Map.Width);
        Assert.Equal(AuthoredMaps.TownAndFloor1Height, Map.Height);
        Assert.Equal(384, Map.Width);
        Assert.Equal(384, Map.Height);
    }

    [Fact]
    public void EveryWalkableTileIsReachableFromEverySpawn()
    {
        // The §4 no-orphan-pockets invariant on the REAL map. House footprints are blocked tiles
        // (M1 review F4), so this flood-fill genuinely sees house collision, the wall+gate, the
        // arena mouths, the pass, and the Verge pockets.
        Assert.Equal(6, Map.SpawnTiles.Count);
        foreach (var spawn in Map.SpawnTiles)
        {
            Assert.True(
                Map.AllWalkableReachableFrom(spawn),
                $"Orphan walkable pocket: not all walkable tiles reachable from spawn {spawn}.");
        }
    }

    [Fact]
    public void SpawnAnchorsRingThePlazaCenter()
    {
        Assert.Equal(
            new[]
            {
                new TileCoord(193, 37),
                new TileCoord(195, 37),
                new TileCoord(192, 38),
                new TileCoord(196, 38),
                new TileCoord(193, 39),
                new TileCoord(195, 39),
            },
            Map.SpawnTiles);
        foreach (var spawn in Map.SpawnTiles)
        {
            Assert.True(Map.IsWalkable(spawn));
            Assert.Equal(SurfaceCategory.Cobble, Map.CategoryAt(spawn));
        }
    }

    [Fact]
    public void MarkersAreSevenHousesTwoPortalsAndTheTwoPins()
    {
        var houses = Map.Markers.Where(m => m.Kind == AuthoredMarkerKind.House).ToArray();
        var portals = Map.Markers.Where(m => m.Kind == AuthoredMarkerKind.Portal).ToArray();

        Assert.Equal(7, houses.Length);
        Assert.Equal(2, portals.Length);
        Assert.Equal(
            new[] { new TileCoord(189, 108), new TileCoord(196, 108) },
            portals.Select(p => p.Tile).ToArray());
        Assert.Equal(
            new AuthoredMarker(AuthoredMarkerKind.TreePin, new TileCoord(188, 22)),
            Assert.Single(Map.Markers, m => m.Kind == AuthoredMarkerKind.TreePin));
        Assert.Equal(
            new AuthoredMarker(AuthoredMarkerKind.RockPin, new TileCoord(204, 22)),
            Assert.Single(Map.Markers, m => m.Kind == AuthoredMarkerKind.RockPin));

        // Every marker tile is walkable (it anchors an entity) — houses collide via their footprints.
        foreach (var marker in Map.Markers)
        {
            Assert.True(Map.IsWalkable(marker.Tile));
        }
    }

    [Fact]
    public void HouseFootprintsAreBlockedNorthOfEachAnchor()
    {
        // M1 review F4: the house COLLISION is stamped into the map — a 4x3 blocked rect whose south
        // edge sits one tile north of the walkable `H` anchor, centered so the anchor is at x0+1.
        foreach (var house in Map.Markers.Where(m => m.Kind == AuthoredMarkerKind.House))
        {
            var anchor = house.Tile;
            for (var dy = 1; dy <= 3; dy++)
            {
                for (var dx = -1; dx <= 2; dx++)
                {
                    var tile = new TileCoord(anchor.X + dx, anchor.Y + dy);
                    Assert.True(Map.IsBlocked(tile), $"House footprint tile {tile} (anchor {anchor}) is not blocked.");
                }
            }
        }
    }

    [Fact]
    public void StructuralLandmarksAreWhereTheBriefPutsThem()
    {
        // World border.
        Assert.True(Map.IsBlocked(new TileCoord(0, 0)));
        Assert.True(Map.IsBlocked(new TileCoord(383, 383)));

        // The great wall spans full width on rows 110-112...
        Assert.True(Map.IsBlocked(new TileCoord(50, 111)));
        Assert.True(Map.IsBlocked(new TileCoord(350, 111)));
        // ...except the 4-wide gate (x 191-194), which is walkable road dirt on all three rows.
        for (var y = 110; y <= 112; y++)
        {
            for (var x = 191; x <= 194; x++)
            {
                var tile = new TileCoord(x, y);
                Assert.True(Map.IsWalkable(tile), $"Gate tile {tile} is not walkable.");
                Assert.Equal(SurfaceCategory.Dirt, Map.CategoryAt(tile));
            }
        }

        // Gate shoulders are wall.
        Assert.True(Map.IsBlocked(new TileCoord(190, 111)));
        Assert.True(Map.IsBlocked(new TileCoord(195, 111)));

        // The pond and the Verge tarn: blocked water.
        Assert.True(Map.IsBlocked(new TileCoord(146, 36)));
        Assert.Equal(SurfaceCategory.Water, Map.CategoryAt(146, 36));
        Assert.True(Map.IsBlocked(new TileCoord(200, 336)));
        Assert.Equal(SurfaceCategory.Water, Map.CategoryAt(200, 336));

        // Town surfaces: cobble plaza center, dirt ring road, the road to the gate.
        Assert.Equal(SurfaceCategory.Cobble, Map.CategoryAt(194, 38));
        Assert.Equal(SurfaceCategory.Dirt, Map.CategoryAt(176, 38));
        Assert.Equal(SurfaceCategory.Dirt, Map.CategoryAt(193, 80));

        // The north pass narrows: 19-wide at its mouth, 8-wide at the top step.
        Assert.True(Map.IsBlocked(new TileCoord(185, 225)));
        Assert.True(Map.IsBlocked(new TileCoord(205, 225)));
        Assert.True(Map.IsWalkable(new TileCoord(195, 225)));
        Assert.True(Map.IsBlocked(new TileCoord(190, 295)));
        Assert.True(Map.IsBlocked(new TileCoord(199, 295)));
        Assert.True(Map.IsWalkable(new TileCoord(195, 295)));

        // No out-of-world padding on this map (fully rectangular world).
        Assert.Empty(Map.OutOfWorldTiles);
    }
}
