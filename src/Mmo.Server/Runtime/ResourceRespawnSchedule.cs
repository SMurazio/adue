namespace Mmo.Server.Runtime;

// Depleted-only respawn scheduler for resource nodes. A node is enqueued when it is harvested, keyed by
// the tick it is due to respawn at; each tick the server pops only the nodes whose respawn time has
// arrived. This keeps per-tick respawn work O(depleted) — it never walks the (potentially thousands of)
// still-available nodes, which is the whole point of S44's world-wide scatter not bloating the tick.
//
// Internal so the server can own it and tests can assert the O(depleted) contract directly (no
// wall-clock): the schedule only ever hands back nodes that are actually due, and an available node is
// never visited by the per-tick drain.
internal sealed class ResourceRespawnSchedule
{
    private readonly PriorityQueue<WorldEntity, uint> _queue = new();

    // Number of nodes currently waiting to respawn. Available nodes are not counted; this is the exact
    // bound on per-tick drain work.
    public int PendingCount => _queue.Count;

    // Schedules a freshly-harvested node to respawn at the tick its ResourceNode computed on depletion.
    public void Schedule(WorldEntity node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _queue.Enqueue(node, node.ResourceRespawnAtTick);
    }

    // Drains every node whose scheduled respawn tick has arrived and flips it back to Available (the
    // callback observes the respawn so the caller can bump replication state). Stops at the first node
    // still inside its respawn window — available nodes are never examined. Stale entries (a node
    // re-harvested after respawning, so re-enqueued under a newer key) are dropped; the live entry for
    // that node handles its respawn. Returns the number of nodes respawned this drain.
    public int DrainDue(uint serverTick, Action<WorldEntity> onRespawned)
    {
        ArgumentNullException.ThrowIfNull(onRespawned);

        var respawned = 0;
        while (_queue.TryPeek(out var node, out var respawnAtTick) && respawnAtTick <= serverTick)
        {
            _queue.Dequeue();

            // Drop stale entries: act only on the entry whose key still matches the node's live schedule
            // and that is genuinely depleted.
            if (node.Resource is null || node.Resource.IsAvailable || node.ResourceRespawnAtTick != respawnAtTick)
            {
                continue;
            }

            if (node.TryRespawnResource(serverTick))
            {
                onRespawned(node);
                respawned++;
            }
        }

        return respawned;
    }
}
