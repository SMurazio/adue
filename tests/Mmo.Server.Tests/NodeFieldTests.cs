using System;
using System.Collections.Generic;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Population;
using Xunit;

namespace Mmo.Server.Tests;

// NODE-FIELD N2: covers NodeField's O(depleted) respawn-sweep contract — mirrors the retired
// ResourceRespawnScheduleTests' shape/assertions exactly (per-tick work is proportional to depleted count,
// not total; stale/duplicate entries never double-fire), now against index-keyed state instead of a
// WorldEntity — plus the basic index/entry/depleted-set surface HandleHarvestNode and the login batch rely on.
public sealed class NodeFieldTests
{
    // A tiny 3-PIN catalogue (an EMPTY class table -- no scatter at all, mirroring NodeCatalogTests'
    // PinsOccupyIndices0ToPinCountMinusOne pattern of passing an alternate class list): index 0 =
    // Tree@(1,1), index 1 = Rock@(3,1), index 2 = Tree@(5,1). Deterministic and stable regardless of any
    // scatter-class retuning — this file is about NodeField's mutable-state mechanics, not catalogue content.
    private static NodeField CreateField()
    {
        var map = AuthoredMap.Parse(new[]
        {
            "#########",
            "#T.R.T..#",
            "#########",
        });

        var catalog = NodeCatalog.Build(seed: 0, map, classes: Array.Empty<NodeClass>());
        return new NodeField(catalog);
    }

    [Fact]
    public void FreshFieldHasEveryIndexAvailable()
    {
        var field = CreateField();

        Assert.Equal(3, field.Count);
        for (var i = 0; i < field.Count; i++)
        {
            Assert.False(field.IsDepleted(i));
        }

        Assert.Empty(field.DepletedIndices());
    }

    [Fact]
    public void IsValidIndexRejectsNegativeAndOutOfRange()
    {
        var field = CreateField();

        Assert.True(field.IsValidIndex(0));
        Assert.True(field.IsValidIndex(2));
        Assert.False(field.IsValidIndex(-1));
        Assert.False(field.IsValidIndex(3));
    }

    [Fact]
    public void EntryAtExposesTheCatalogueTileAndType()
    {
        var field = CreateField();

        Assert.Equal(new TileCoord(1, 1), field.EntryAt(0).Tile);
        Assert.Equal(NodeType.Tree, field.EntryAt(0).NodeType);
        Assert.Equal(new TileCoord(3, 1), field.EntryAt(1).Tile);
        Assert.Equal(NodeType.Rock, field.EntryAt(1).NodeType);
    }

    [Fact]
    public void AvailableIndicesAreNeverScheduledOrDrained()
    {
        var field = CreateField();

        var visited = new List<int>();
        field.DrainDueRespawns(serverTick: 1_000_000, visited.Add);

        Assert.Empty(visited);
        Assert.False(field.IsDepleted(0));
    }

    [Fact]
    public void DepleteMarksUnavailableUntilRespawnTick()
    {
        var field = CreateField();

        field.Deplete(0, serverTick: 100, respawnTicks: 10); // due at 110
        Assert.True(field.IsDepleted(0));

        // Before the respawn tick: nothing drains, the index stays depleted.
        var visited = new List<int>();
        field.DrainDueRespawns(serverTick: 109, visited.Add);
        Assert.True(field.IsDepleted(0));
        Assert.Empty(visited);
    }

    [Fact]
    public void RespawnsExactlyAtScheduledTickAndNotifiesOnce()
    {
        var field = CreateField();
        field.Deplete(0, serverTick: 100, respawnTicks: 10); // due at 110

        var visited = new List<int>();
        field.DrainDueRespawns(serverTick: 110, visited.Add);

        Assert.False(field.IsDepleted(0));
        Assert.Equal(new[] { 0 }, visited);
    }

    [Fact]
    public void DrainProcessesOnlyDueIndicesProportionalToDepleted()
    {
        var field = CreateField();
        field.Deplete(0, serverTick: 0, respawnTicks: 10);   // due at 10
        field.Deplete(1, serverTick: 100, respawnTicks: 10); // due at 110

        // Tick 10: only index 0 is due. Index 1 is not visited at all (O(depleted-due)).
        var visited = new List<int>();
        field.DrainDueRespawns(serverTick: 10, visited.Add);

        Assert.Equal(new[] { 0 }, visited);
        Assert.False(field.IsDepleted(0));
        Assert.True(field.IsDepleted(1));
    }

    [Fact]
    public void HarvestRespawnHarvestCycleRespawnsExactlyOncePerDepletion()
    {
        var field = CreateField();

        field.Deplete(0, serverTick: 0, respawnTicks: 10); // due at 10
        Assert.Equal(1, DrainCount(field, 10));
        Assert.False(field.IsDepleted(0));

        // Re-harvest the now-available node and confirm it respawns exactly once at its NEW due tick.
        field.Deplete(0, serverTick: 100, respawnTicks: 10); // due at 110
        Assert.Equal(0, DrainCount(field, 109));
        Assert.Equal(1, DrainCount(field, 110));
        Assert.False(field.IsDepleted(0));
    }

    [Fact]
    public void DepletedIndicesReturnsOnlyTheCurrentExceptions()
    {
        var field = CreateField();
        field.Deplete(2, serverTick: 0, respawnTicks: 1000);

        Assert.Equal(new ushort[] { 2 }, field.DepletedIndices());
    }

    private static int DrainCount(NodeField field, uint serverTick)
    {
        var count = 0;
        field.DrainDueRespawns(serverTick, _ => count++);
        return count;
    }
}
