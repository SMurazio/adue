using System.Collections.Generic;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Shared.Tests;

// CONTINUOUS MIGRATION (Phase 2): tests for the SHARED tile -> collision-wall derivation. Pins the exact tile AABB
// geometry (a blocked tile (tx,ty) is the 1x1 box [tx-0.5..tx+0.5] x [ty-0.5..ty+0.5]), the stable-order SUPERSET
// guarantee of the neighbourhood query, and its determinism — the same blocked set + box must yield the same Wall
// list in the same row-major order, because the Phase-4 client derives walls from the SAME function and any order
// divergence desyncs prediction.
public sealed class TileWallsTests
{
    [Fact]
    public void ForTile_ProducesExactUnitBoxCentredOnTile()
    {
        var wall = TileWalls.ForTile(new TileCoord(3, 5));

        Assert.Equal(2.5d, wall.MinX, 12);
        Assert.Equal(4.5d, wall.MinY, 12);
        Assert.Equal(3.5d, wall.MaxX, 12);
        Assert.Equal(5.5d, wall.MaxY, 12);
    }

    [Fact]
    public void ForTile_NegativeTile_StillExactUnitBox()
    {
        var wall = TileWalls.ForTile(new TileCoord(-1, 0));

        Assert.Equal(-1.5d, wall.MinX, 12);
        Assert.Equal(-0.5d, wall.MinY, 12);
        Assert.Equal(-0.5d, wall.MaxX, 12);
        Assert.Equal(0.5d, wall.MaxY, 12);
    }

    [Fact]
    public void NeighborhoodWalls_EmitsOnlyBlockedTilesInBox_StableRowMajorOrder()
    {
        // Blocked tiles scattered; some inside the box, some outside. The query must emit ONLY the in-box blocked
        // tiles, in row-major (y outer, x inner) order, regardless of insertion order into the set.
        var blocked = new HashSet<TileCoord>
        {
            new(5, 5),    // outside the box
            new(2, 1),    // inside, row y=1
            new(1, 0),    // inside, row y=0
            new(2, 0),    // inside, row y=0
            new(0, 2),    // outside the box (x<min)
        };

        var output = new List<ContinuousCollision.Wall>();
        TileWalls.NeighborhoodWalls(blocked, minTileX: 1, minTileY: 0, maxTileX: 3, maxTileY: 2, output);

        // Expected row-major: (1,0), (2,0) [row 0], then (2,1) [row 1]. (5,5) and (0,2) excluded.
        Assert.Equal(3, output.Count);
        Assert.Equal(TileWalls.ForTile(new TileCoord(1, 0)), output[0]);
        Assert.Equal(TileWalls.ForTile(new TileCoord(2, 0)), output[1]);
        Assert.Equal(TileWalls.ForTile(new TileCoord(2, 1)), output[2]);
    }

    [Fact]
    public void NeighborhoodWalls_IsAStableSupersetOfTheRegion()
    {
        // The box may over-cover (a conservative superset). Every blocked tile in the box appears; nothing outside
        // does; the order is positional (row-major), NOT the set's iteration order.
        var blocked = new HashSet<TileCoord> { new(7, 7), new(8, 7), new(7, 8), new(8, 8) };

        var output = new List<ContinuousCollision.Wall>();
        TileWalls.NeighborhoodWalls(blocked, minTileX: 6, minTileY: 6, maxTileX: 9, maxTileY: 9, output);

        Assert.Equal(4, output.Count);
        Assert.Equal(TileWalls.ForTile(new TileCoord(7, 7)), output[0]);
        Assert.Equal(TileWalls.ForTile(new TileCoord(8, 7)), output[1]);
        Assert.Equal(TileWalls.ForTile(new TileCoord(7, 8)), output[2]);
        Assert.Equal(TileWalls.ForTile(new TileCoord(8, 8)), output[3]);
    }

    [Fact]
    public void NeighborhoodWalls_ClearsTheScratchBuffer_NoStaleResidue()
    {
        var blocked = new HashSet<TileCoord> { new(0, 0) };
        var output = new List<ContinuousCollision.Wall>
        {
            // Pre-seed stale residue; the query must Clear() before filling.
            ContinuousCollision.Wall.FromCenter(99d, 99d, 1d, 1d),
        };

        TileWalls.NeighborhoodWalls(blocked, minTileX: 0, minTileY: 0, maxTileX: 0, maxTileY: 0, output);

        Assert.Single(output);
        Assert.Equal(TileWalls.ForTile(new TileCoord(0, 0)), output[0]);
    }

    [Fact]
    public void NeighborhoodWalls_Deterministic_SameSetAndBox_IdenticalWallList()
    {
        // Two independently-constructed sets with the SAME content (different insertion orders) + the same box must
        // yield byte-identical Wall lists in the same order — the determinism linchpin for client/server parity.
        var a = new HashSet<TileCoord> { new(3, 3), new(4, 3), new(3, 4) };
        var b = new HashSet<TileCoord> { new(3, 4), new(3, 3), new(4, 3) };

        var outA = new List<ContinuousCollision.Wall>();
        var outB = new List<ContinuousCollision.Wall>();
        TileWalls.NeighborhoodWalls(a, 2, 2, 5, 5, outA);
        TileWalls.NeighborhoodWalls(b, 2, 2, 5, 5, outB);

        Assert.Equal(outA.Count, outB.Count);
        for (var i = 0; i < outA.Count; i++)
        {
            Assert.Equal(System.BitConverter.DoubleToInt64Bits(outA[i].MinX), System.BitConverter.DoubleToInt64Bits(outB[i].MinX));
            Assert.Equal(System.BitConverter.DoubleToInt64Bits(outA[i].MinY), System.BitConverter.DoubleToInt64Bits(outB[i].MinY));
            Assert.Equal(System.BitConverter.DoubleToInt64Bits(outA[i].MaxX), System.BitConverter.DoubleToInt64Bits(outB[i].MaxX));
            Assert.Equal(System.BitConverter.DoubleToInt64Bits(outA[i].MaxY), System.BitConverter.DoubleToInt64Bits(outB[i].MaxY));
        }
    }
}
