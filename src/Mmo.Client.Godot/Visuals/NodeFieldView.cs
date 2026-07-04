using System.Collections.Generic;
using Godot;
using Mmo.Client.Core.Population;

namespace Mmo.Client.Godot.Visuals;

// NODE-FIELD N3 (docs/node-field-design.md D6): owns the field's per-chunk Node3D lifecycle so a NodeState
// flip rebuilds ONLY the affected chunk(s) — "rebuilding one 32-tile chunk's buffer on a state flip is
// microseconds" — instead of tearing down and rebuilding the whole ~5,000-instance field on every harvest.
// NodeFieldPainter (stateless) does the actual mesh/buffer work per chunk; this class just tracks which
// Node3D belongs to which chunk key and diffs the depleted set between calls.
public sealed class NodeFieldView
{
    private readonly Node3D _root;
    private readonly NodeFieldChunkIndex _chunkIndex;
    private readonly IReadOnlyList<NodeFieldPlacer.PlacedNode> _placements;
    private readonly Dictionary<(int Cx, int Cz), Node3D> _chunkNodes = new();
    private readonly HashSet<ushort> _renderedDepleted = new();

    // Total instances placed at the initial Build() — the zone-build timing print's read-out. NOT
    // maintained across later SyncDepletion rebuilds (nothing re-reads it after zone build, and a depleted
    // node still occupies an instance slot — the count doesn't actually change on a flip anyway).
    public int InstanceCount { get; private set; }

    private NodeFieldView(Node3D root, NodeFieldChunkIndex chunkIndex, IReadOnlyList<NodeFieldPlacer.PlacedNode> placements)
    {
        _root = root;
        _chunkIndex = chunkIndex;
        _placements = placements;
    }

    public static NodeFieldView Build(
        Node parent,
        NodeFieldChunkIndex chunkIndex,
        IReadOnlyList<NodeFieldPlacer.PlacedNode> placements,
        IReadOnlySet<ushort> depletedIndices)
    {
        var root = new Node3D { Name = "NodeField" };
        var view = new NodeFieldView(root, chunkIndex, placements);

        var total = 0;
        foreach (var chunkKey in chunkIndex.ChunkKeys)
        {
            var chunkNode = NodeFieldPainter.BuildChunkNode(
                chunkKey, chunkIndex.EntriesIn(chunkKey), placements, depletedIndices, out var instanceCount);
            view._chunkNodes[chunkKey] = chunkNode;
            root.AddChild(chunkNode);
            total += instanceCount;
        }

        view.InstanceCount = total;
        view._renderedDepleted.UnionWith(depletedIndices);
        parent.AddChild(root);
        return view;
    }

    // Call whenever MmoClient.NodeFieldVersion changes. Diffs the previously-rendered depleted set against
    // the current one (a HashSet<ushort> symmetric difference — cheap even at the D4 "typically dozens"
    // scale) and rebuilds only the chunk(s) whose membership actually changed.
    public void SyncDepletion(IReadOnlySet<ushort> depletedIndices)
    {
        var changed = new HashSet<ushort>(_renderedDepleted);
        changed.SymmetricExceptWith(depletedIndices);
        if (changed.Count == 0)
        {
            _renderedDepleted.Clear();
            _renderedDepleted.UnionWith(depletedIndices);
            return;
        }

        foreach (var chunkKey in _chunkIndex.ChunksTouchedBy(changed))
        {
            RebuildChunk(chunkKey, depletedIndices);
        }

        _renderedDepleted.Clear();
        _renderedDepleted.UnionWith(depletedIndices);
    }

    // Detaches this view's whole field root (zone teardown / rebuild-from-scratch on reconnect).
    public void Free()
    {
        _root.QueueFree();
        _chunkNodes.Clear();
    }

    private void RebuildChunk((int Cx, int Cz) chunkKey, IReadOnlySet<ushort> depletedIndices)
    {
        if (_chunkNodes.TryGetValue(chunkKey, out var existing))
        {
            existing.QueueFree();
            _chunkNodes.Remove(chunkKey);
        }

        var entries = _chunkIndex.EntriesIn(chunkKey);
        if (entries.Count == 0)
        {
            return;
        }

        var chunkNode = NodeFieldPainter.BuildChunkNode(chunkKey, entries, _placements, depletedIndices, out _);
        _chunkNodes[chunkKey] = chunkNode;
        _root.AddChild(chunkNode);
    }
}
