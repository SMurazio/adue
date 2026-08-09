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
    // BOSS-1 REPIN: the Sunderer arena stamp (AuthoredMaps.BuildTownAndFloor1) is a DELIBERATE map edit that moves
    // this hash by construction. The implementer has no test-runner access to compute it — the literal below is STALE;
    // the orchestrator runs the gate once, reads the actual computed hash from this assertion's failure, and pastes it
    // in (the M3 F1 process). Do NOT guess a value or delete the test.
    // REPINNED 2026-07-05 from the BOSS-1 gate run's actual computed value.
    // ADUE P2-A REPIN: the practice-room stamp (AuthoredMaps.BuildTownAndFloor1) is a SECOND deliberate map edit — a
    // 24x24 wall ring + DungeonStone floor in the NW corner — that moves this hash AGAIN by construction. The literal
    // below is STALE (still the BOSS-1 value); same process — the orchestrator runs the gate once, reads the actual
    // computed hash from this assertion's failure, and pastes it in. Do NOT guess a value or delete the test.
    // REPINNED 2026-08-09 from the ADUE P2-A gate run's actual computed value (practice-room stamp).
    private const ulong ShippedTownAndFloor1ContentHash = 14933617869436013510UL;

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
        //
        // BOSS-1 + ADUE P2-A: TWO deliberate exceptions — the Sunderer arena AND the practice room are both SEALED,
        // teleport-only pockets (players /boss or /practice in, never walk in), so their 22x22 interiors are
        // intentionally unreachable on foot. We assert (a) NO interior tile of EITHER pocket is reachable (they really
        // are sealed), and (b) every OTHER walkable tile IS reachable (the count equals all-walkable minus BOTH
        // pockets). An ACCIDENTAL future orphan ANYWHERE ELSE still fails (b).
        Assert.Equal(6, Map.SpawnTiles.Count);

        var sealedPockets = new HashSet<TileCoord>();
        for (var y = BossArena.InteriorMinY; y <= BossArena.InteriorMaxY; y++)
        {
            for (var x = BossArena.InteriorMinX; x <= BossArena.InteriorMaxX; x++)
            {
                sealedPockets.Add(new TileCoord(x, y));
            }
        }

        for (var y = PracticeRoom.InteriorMinY; y <= PracticeRoom.InteriorMaxY; y++)
        {
            for (var x = PracticeRoom.InteriorMinX; x <= PracticeRoom.InteriorMaxX; x++)
            {
                sealedPockets.Add(new TileCoord(x, y));
            }
        }

        foreach (var spawn in Map.SpawnTiles)
        {
            var reached = Map.FloodFillWalkableFrom(spawn);
            Assert.False(
                reached.Any(sealedPockets.Contains),
                $"The Sunderer arena + practice room must be sealed pockets, but a tile was reachable on foot from spawn {spawn}.");
            Assert.Equal(Map.WalkableTileCount - sealedPockets.Count, reached.Count);
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

    [Fact]
    public void GateRowHasExactlyFourWalkableTiles()
    {
        // M3-REVIEW-FOLLOWUPS item 2: the "only-one-gate" structural invariant. StructuralLandmarksAre-
        // WhereTheBriefPutsThem above only SAMPLES row 111 (walls at x50/x350, the 4-wide gate walkable,
        // the two shoulder tiles blocked) — it would stay green even if a FUTURE map edit accidentally
        // punched a SECOND hole elsewhere on that row, since none of those sampled points touch it. A
        // deliberate map edit is expected to re-pin ContentHashIsPinnedToShippedLiteral (M1 review F1's
        // documented process), but nothing forces a human to also re-examine the wall's exact shape — so
        // THIS test counts every walkable tile on the ENTIRE row instead of sampling a few: exactly the
        // 4-wide gate (x191-194), independent of the hash. An accidental second gap anywhere else on
        // y=111 fails this even though the hash literal, the sampled wall/gate points, AND the
        // reachability test (a second hole only adds MORE paths — it can never orphan a pocket) would
        // all stay green.
        var walkableXs = Enumerable.Range(0, Map.Width)
            .Where(x => Map.IsWalkable(new TileCoord(x, 111)))
            .ToArray();

        Assert.Equal(new[] { 191, 192, 193, 194 }, walkableXs);
    }

    [Fact]
    public void SundererArenaIsASealedDungeonStonePocket()
    {
        // BOSS-1: the far-NE Sunderer arena — a 1-tile wall ring around a 22x22 DungeonStone floor (the non-grass
        // surface masks the node scatter out for free), with NO mouth. Pins the stamp (AuthoredMaps + BossArena): the
        // ring is fully blocked, the interior is all walkable dungeon stone, and the fixed entry tiles + boss-spawn
        // centre the engine teleports to are walkable interior tiles.
        for (var x = BossArena.ExteriorMinX; x <= BossArena.ExteriorMaxX; x++)
        {
            Assert.True(Map.IsBlocked(new TileCoord(x, BossArena.ExteriorMinY)), $"arena south wall gap at x={x}");
            Assert.True(Map.IsBlocked(new TileCoord(x, BossArena.ExteriorMaxY)), $"arena north wall gap at x={x}");
        }

        for (var y = BossArena.ExteriorMinY; y <= BossArena.ExteriorMaxY; y++)
        {
            Assert.True(Map.IsBlocked(new TileCoord(BossArena.ExteriorMinX, y)), $"arena west wall gap at y={y}");
            Assert.True(Map.IsBlocked(new TileCoord(BossArena.ExteriorMaxX, y)), $"arena east wall gap at y={y}");
        }

        for (var y = BossArena.InteriorMinY; y <= BossArena.InteriorMaxY; y++)
        {
            for (var x = BossArena.InteriorMinX; x <= BossArena.InteriorMaxX; x++)
            {
                var tile = new TileCoord(x, y);
                Assert.True(Map.IsWalkable(tile), $"arena interior tile {tile} is not walkable");
                Assert.Equal(SurfaceCategory.DungeonStone, Map.CategoryAt(tile));
            }
        }

        foreach (var tile in new[] { BossArena.IssuerEntryTile, BossArena.PartnerEntryTile, BossArena.BossSpawnTile })
        {
            Assert.True(Map.IsWalkable(tile), $"arena landmark {tile} must be walkable interior");
            Assert.True(BossArena.ContainsInterior(tile), $"arena landmark {tile} must be inside the interior");
        }
    }

    [Fact]
    public void PracticeRoomIsASealedDungeonStonePocket()
    {
        // ADUE P2-A (todo/S-p2-practice-room-and-dummy.md): the far-NW practice room — the BossArena's twin: a 1-tile
        // wall ring around a 22x22 DungeonStone floor (the non-grass surface masks the node scatter out for free), with
        // NO mouth. Pins the stamp (AuthoredMaps + PracticeRoom): the ring is fully blocked, the interior is all walkable
        // dungeon stone, and the fixed entry tiles + dummy-spawn tile the /practice command teleports to / spawns at are
        // walkable interior tiles.
        for (var x = PracticeRoom.ExteriorMinX; x <= PracticeRoom.ExteriorMaxX; x++)
        {
            Assert.True(Map.IsBlocked(new TileCoord(x, PracticeRoom.ExteriorMinY)), $"practice-room south wall gap at x={x}");
            Assert.True(Map.IsBlocked(new TileCoord(x, PracticeRoom.ExteriorMaxY)), $"practice-room north wall gap at x={x}");
        }

        for (var y = PracticeRoom.ExteriorMinY; y <= PracticeRoom.ExteriorMaxY; y++)
        {
            Assert.True(Map.IsBlocked(new TileCoord(PracticeRoom.ExteriorMinX, y)), $"practice-room west wall gap at y={y}");
            Assert.True(Map.IsBlocked(new TileCoord(PracticeRoom.ExteriorMaxX, y)), $"practice-room east wall gap at y={y}");
        }

        for (var y = PracticeRoom.InteriorMinY; y <= PracticeRoom.InteriorMaxY; y++)
        {
            for (var x = PracticeRoom.InteriorMinX; x <= PracticeRoom.InteriorMaxX; x++)
            {
                var tile = new TileCoord(x, y);
                Assert.True(Map.IsWalkable(tile), $"practice-room interior tile {tile} is not walkable");
                Assert.Equal(SurfaceCategory.DungeonStone, Map.CategoryAt(tile));
            }
        }

        foreach (var tile in new[] { PracticeRoom.IssuerEntryTile, PracticeRoom.PartnerEntryTile, PracticeRoom.DummySpawnTile })
        {
            Assert.True(Map.IsWalkable(tile), $"practice-room landmark {tile} must be walkable interior");
            Assert.True(PracticeRoom.ContainsInterior(tile), $"practice-room landmark {tile} must be inside the interior");
        }

        // The two sealed pockets must not overlap (they live in opposite corners).
        Assert.False(
            PracticeRoom.ExteriorMaxX >= BossArena.ExteriorMinX && BossArena.ExteriorMaxX >= PracticeRoom.ExteriorMinX
            && PracticeRoom.ExteriorMaxY >= BossArena.ExteriorMinY && BossArena.ExteriorMaxY >= PracticeRoom.ExteriorMinY,
            "the practice room and Sunderer arena must not overlap");
    }
}
