using System.Collections.Generic;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// CONTINUOUS MIGRATION (Phase 4, Stage 0a): the server's TileGrid.QueryNearbyWalls was refactored from inline
// swept-AABB box-math into a thin forwarder to the SHARED Mmo.Shared.Domain.TileWalls.NeighborhoodWallsForMove (the
// EXACT helper the Phase-4 client predictor calls). This pins that the extraction is BYTE-IDENTICAL — the forwarder
// emits the same walls, in the same order, as calling the shared helper directly with the same (blocked, start,
// delta, radius). If they ever diverge, server integration and client prediction would derive different wall sets and
// the prediction would desync at walls — so the parity is the determinism linchpin, asserted here.
public sealed class NeighborhoodWallsParityTests
{
    [Theory]
    // A spread of starts/deltas/radii including: into-a-wall, glancing, multi-tile sweep (anti-tunnel box), negative
    // delta, zero delta (in-place), and a fractional radius (the live-knob case).
    [InlineData(8.0, 8.0, 1.0, 0.0, 0.5)]
    [InlineData(8.0, 8.0, 1.0, 1.0, 0.5)]
    [InlineData(8.0, 8.0, -2.0, 0.5, 0.5)]
    [InlineData(8.0, 8.0, 3.0, -3.0, 0.5)]
    [InlineData(8.3, 8.7, 0.05, 0.05, 0.4)]
    [InlineData(8.0, 8.0, 0.0, 0.0, 0.5)]
    [InlineData(15.5, 12.25, 0.2, -0.1, 0.375)]
    public void QueryNearbyWalls_MatchesSharedHelper_ByteIdentical(
        double startX, double startY, double deltaX, double deltaY, double radius)
    {
        // A small blocked cluster around the test region (plus one far away that should never be emitted).
        var blocked = new[]
        {
            new TileCoord(10, 8),
            new TileCoord(10, 9),
            new TileCoord(9, 7),
            new TileCoord(7, 9),
            new TileCoord(8, 8),
            new TileCoord(100, 100),
        };
        var grid = new TileGrid(128, 128, blocked);

        var start = new WorldVector(startX, startY);
        var delta = new WorldVector(deltaX, deltaY);

        var viaForwarder = new List<ContinuousCollision.Wall>();
        grid.QueryNearbyWalls(start, delta, radius, viaForwarder);

        var viaShared = new List<ContinuousCollision.Wall>();
        TileWalls.NeighborhoodWallsForMove(grid.BlockedTiles, start, delta, radius, viaShared);

        // Same count, same walls, in the same (row-major) order — byte-identical.
        Assert.Equal(viaShared.Count, viaForwarder.Count);
        for (var i = 0; i < viaShared.Count; i++)
        {
            Assert.Equal(viaShared[i], viaForwarder[i]);
        }
    }
}
