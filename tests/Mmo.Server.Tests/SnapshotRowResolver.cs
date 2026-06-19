using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;

namespace Mmo.Server.Tests;

// Test-side decoder for delta-coded world snapshots (S47b, protocol v16). The integration harnesses act as
// lightweight clients over a real socket; they used to read raw absolute fields off each EntityStateSnapshot
// row, but with delta encoding a row only carries the CHANGED fields (position as an absolute, a step, or
// omitted; facing/depleted only when changed). This resolver maintains per-entity absolute state and applies
// each row the same way MmoClient does, so the harnesses keep observing absolute tile/facing/depleted.
internal sealed class SnapshotRowResolver
{
    private readonly Dictionary<uint, ResolvedEntityState> _states = new();

    public IReadOnlyDictionary<uint, ResolvedEntityState> States => _states;

    public bool TryGet(uint networkId, out ResolvedEntityState state)
    {
        return _states.TryGetValue(networkId, out state);
    }

    public void Seed(uint networkId, TileCoord tile, Direction8 facing, bool depleted = false)
    {
        _states[networkId] = new ResolvedEntityState(tile, facing, depleted);
    }

    public void Remove(uint networkId)
    {
        _states.Remove(networkId);
    }

    // Resolves a decoded row against the entity's current state, stores the result, and returns it.
    public ResolvedEntityState Apply(EntityStateSnapshot row)
    {
        _states.TryGetValue(row.NetworkId, out var current);

        var tile = current.Tile;
        if (row.HasAbsolutePosition)
        {
            tile = row.Tile;
        }
        else if (row.HasStepPosition)
        {
            var delta = row.Step.Delta();
            tile = current.Tile.Offset(delta.X, delta.Y);
        }

        var facing = row.HasFacing ? row.Facing : current.Facing;
        var depleted = row.HasDepleted ? row.Depleted : current.Depleted;

        var resolved = new ResolvedEntityState(tile, facing, depleted);
        _states[row.NetworkId] = resolved;
        return resolved;
    }

    // Prunes any entity not present in a COMPLETE snapshot's row set, mirroring the real client reconcile.
    public void PruneTo(IEnumerable<EntityStateSnapshot> rows)
    {
        var present = new HashSet<uint>();
        foreach (var row in rows)
        {
            present.Add(row.NetworkId);
        }

        foreach (var networkId in _states.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            _states.Remove(networkId);
        }
    }
}

internal readonly record struct ResolvedEntityState(TileCoord Tile, Direction8 Facing, bool Depleted);
