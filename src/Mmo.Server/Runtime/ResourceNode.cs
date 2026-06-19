namespace Mmo.Server.Runtime;

// Transient, server-memory-only state for one placed resource node: whether it is currently harvestable
// and, once depleted, the tick at which it becomes Available again. NOT persisted — on restart every
// node respawns fresh as Available (the design's durable/transient split). Pure logic; replication and
// the StateRevision bump live on the owning WorldEntity.
public sealed class ResourceNode
{
    private uint _respawnAtTick;

    public ResourceNode(ResourceNodeDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    public ResourceNodeDefinition Definition { get; }

    // True when harvestable. A freshly placed node starts Available.
    public bool IsAvailable { get; private set; } = true;

    // Marks the node harvested: it becomes Depleted now and is scheduled to respawn after the
    // definition's RespawnTicks. No-op if already depleted (caller validates availability first).
    public void Deplete(uint serverTick)
    {
        IsAvailable = false;
        _respawnAtTick = serverTick + Definition.RespawnTicks;
    }

    // Returns true (and flips back to Available) on the first tick at/after the scheduled respawn time;
    // false otherwise. Cheap to call every tick for depleted nodes.
    public bool TryRespawn(uint serverTick)
    {
        if (IsAvailable || serverTick < _respawnAtTick)
        {
            return false;
        }

        IsAvailable = true;
        return true;
    }
}
