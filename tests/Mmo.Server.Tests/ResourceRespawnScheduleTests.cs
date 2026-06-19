using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// Covers the depleted-only respawn schedule (S44): per-tick work is O(depleted), not O(total). Asserts
// the contract via the structure itself (PendingCount / which nodes the drain visits), not a wall-clock.
public sealed class ResourceRespawnScheduleTests
{
    private static readonly ResourceNodeDefinition TreeDefinition =
        new("tree", "Tree", YieldItemKey: "wood", YieldQuantity: 1, RespawnTicks: 10);

    private static WorldEntity Node(uint networkId)
    {
        return new WorldEntity(
            id: networkId,
            networkId: networkId,
            kind: EntityKind.Resource,
            tile: new TileCoord((int)networkId, 0),
            facing: Direction8.S,
            displayName: "Tree",
            characterId: null,
            ownerSession: null,
            isDurable: false,
            inventory: null,
            resource: new ResourceNode(TreeDefinition));
    }

    [Fact]
    public void AvailableNodesAreNeverScheduledOrDrained()
    {
        var schedule = new ResourceRespawnSchedule();

        // Nothing depleted: pending is zero and a drain far in the future touches nothing.
        Assert.Equal(0, schedule.PendingCount);
        var visited = new List<WorldEntity>();
        var respawned = schedule.DrainDue(serverTick: 1_000_000, n => visited.Add(n));
        Assert.Equal(0, respawned);
        Assert.Empty(visited);
    }

    [Fact]
    public void OnlyDepletedNodeReturnsToAvailableAfterRespawnTicks()
    {
        var schedule = new ResourceRespawnSchedule();
        var node = Node(1);
        node.DepleteResource(serverTick: 100); // respawns at 110
        schedule.Schedule(node);

        Assert.Equal(1, schedule.PendingCount);

        // Before the respawn tick: nothing drains, node stays depleted, entry remains pending.
        var visited = new List<WorldEntity>();
        Assert.Equal(0, schedule.DrainDue(serverTick: 109, n => visited.Add(n)));
        Assert.True(node.IsDepleted);
        Assert.Equal(1, schedule.PendingCount);
        Assert.Empty(visited);

        // At the respawn tick: node flips Available, the callback fires, and the queue empties.
        Assert.Equal(1, schedule.DrainDue(serverTick: 110, n => visited.Add(n)));
        Assert.False(node.IsDepleted);
        Assert.Equal(0, schedule.PendingCount);
        Assert.Single(visited);
        Assert.Same(node, visited[0]);
    }

    [Fact]
    public void DrainProcessesOnlyDueNodesProportionalToDepleted()
    {
        var schedule = new ResourceRespawnSchedule();
        var early = Node(1);
        var late = Node(2);
        early.DepleteResource(serverTick: 0);  // due at 10
        late.DepleteResource(serverTick: 100); // due at 110
        schedule.Schedule(early);
        schedule.Schedule(late);

        Assert.Equal(2, schedule.PendingCount);

        // Tick 10: only the early node is due. The late node is not visited at all (O(depleted-due)).
        var visited = new List<WorldEntity>();
        var respawned = schedule.DrainDue(serverTick: 10, n => visited.Add(n));

        Assert.Equal(1, respawned);
        Assert.Same(early, Assert.Single(visited));
        Assert.False(early.IsDepleted);
        Assert.True(late.IsDepleted);
        Assert.Equal(1, schedule.PendingCount);
    }

    [Fact]
    public void HarvestRespawnHarvestCycleRespawnsExactlyOncePerDepletion()
    {
        var schedule = new ResourceRespawnSchedule();
        var node = Node(1);

        node.DepleteResource(serverTick: 0); // due at 10
        schedule.Schedule(node);
        Assert.Equal(1, schedule.DrainDue(serverTick: 10, static _ => { })); // respawns; queue empties
        Assert.False(node.IsDepleted);

        // Re-harvest the now-available node and confirm it respawns exactly once at its new due tick.
        node.DepleteResource(serverTick: 100); // due at 110
        schedule.Schedule(node);
        Assert.Equal(0, schedule.DrainDue(serverTick: 109, static _ => { }));
        Assert.Equal(1, schedule.DrainDue(serverTick: 110, static _ => { }));
        Assert.False(node.IsDepleted);
        Assert.Equal(0, schedule.PendingCount);
    }

    [Fact]
    public void DrainSkipsAlreadyAvailableStaleEntry()
    {
        // Defensive path: if the same node ever ends up enqueued twice (e.g. a future code path), draining
        // must not "respawn" an already-available node a second time. Force two entries for one node.
        var schedule = new ResourceRespawnSchedule();
        var node = Node(1);
        node.DepleteResource(serverTick: 0); // due at 10
        schedule.Schedule(node);
        schedule.Schedule(node); // duplicate entry, same due tick

        var respawned = schedule.DrainDue(serverTick: 10, static _ => { });

        // First entry respawns the node; the second is dropped because the node is already Available.
        Assert.Equal(1, respawned);
        Assert.False(node.IsDepleted);
        Assert.Equal(0, schedule.PendingCount);
    }
}
