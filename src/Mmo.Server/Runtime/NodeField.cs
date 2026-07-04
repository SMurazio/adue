using Mmo.Shared.Domain.Population;

namespace Mmo.Server.Runtime;

// NODE-FIELD N2 (docs/node-field-design.md D3): the server-side MUTABLE per-index state for the shared,
// immutable NodeCatalog -- NOT WorldEntities (D3's whole point: an untouched node costs ZERO network/tick,
// only memory in a flat array). Built ONCE at zone construction from the SAME (seed, authored map) both
// sides derive independently (the CatalogHash drift guard on ZoneInfo, D2) -- this class owns only the two
// per-index mutable bits every harvest/respawn flips: whether a node is currently depleted, and (while
// depleted) the tick it respawns at.
//
// Respawn sweep choice (implementer's call, per the design doc): a depleted-only PriorityQueue<index,
// respawnTick>, mirroring the retired ResourceRespawnSchedule exactly -- O(depleted) per drain, never
// rescans the (thousands of) still-available nodes. The other documented option was a coarse periodic
// full-array sweep; the priority queue was chosen because the identical approach is already proven at this
// scale (S44) and costs nothing extra to reuse verbatim.
public sealed class NodeField
{
    private readonly NodeCatalog _catalog;
    private readonly bool[] _depleted;
    private readonly uint[] _respawnAtTick;
    private readonly PriorityQueue<int, uint> _respawnQueue = new();

    public NodeField(NodeCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _depleted = new bool[catalog.Entries.Count];
        _respawnAtTick = new uint[catalog.Entries.Count];
    }

    /// <summary>Total catalogue entries (== the shared NodeCatalog's).</summary>
    public int Count => _depleted.Length;

    /// <summary>The drift-guard hash this field's catalogue was built from (rides ZoneInfo.CatalogHash).</summary>
    public ulong CatalogHash => _catalog.CatalogHash;

    public bool IsValidIndex(int index) => index >= 0 && index < _depleted.Length;

    public bool IsDepleted(int index) => _depleted[index];

    public NodeCatalogEntry EntryAt(int index) => _catalog.Entries[index];

    // Harvest: the caller has already validated range/availability/reach/inventory. Flips the node depleted
    // and schedules its respawn under the SAME due-tick key DrainDueRespawns pops by (folds together what the
    // entity path split across ResourceNode.Deplete + ResourceRespawnSchedule.Schedule -- there is no
    // separate WorldEntity here to hand a scheduler, so one call does both).
    public void Deplete(int index, uint serverTick, uint respawnTicks)
    {
        _depleted[index] = true;
        var respawnAt = serverTick + respawnTicks;
        _respawnAtTick[index] = respawnAt;
        _respawnQueue.Enqueue(index, respawnAt);
    }

    // O(depleted-due) respawn sweep (mirrors ResourceRespawnSchedule.DrainDue byte-for-byte): pops only
    // entries whose due tick has arrived and drops STALE entries (a node re-harvested after this entry was
    // queued has a newer _respawnAtTick, so the popped key no longer matches -- the newer entry owns the
    // respawn). Invokes onRespawned once per index that actually flips back to Available; a still-available
    // node is never visited.
    public void DrainDueRespawns(uint serverTick, Action<int> onRespawned)
    {
        ArgumentNullException.ThrowIfNull(onRespawned);

        while (_respawnQueue.TryPeek(out var index, out var respawnAtTick) && respawnAtTick <= serverTick)
        {
            _respawnQueue.Dequeue();

            if (!_depleted[index] || _respawnAtTick[index] != respawnAtTick)
            {
                continue;
            }

            _depleted[index] = false;
            onRespawned(index);
        }
    }

    // NODE-FIELD N2 (D4): the login snapshot payload -- only the currently-depleted indices (typically a
    // handful among thousands), already narrowed to the wire's ushort index type (NodeCatalog.Build enforces
    // the ushort entry cap, so every valid index fits).
    public List<ushort> DepletedIndices()
    {
        var indices = new List<ushort>();
        for (var i = 0; i < _depleted.Length; i++)
        {
            if (_depleted[i])
            {
                indices.Add((ushort)i);
            }
        }

        return indices;
    }
}
