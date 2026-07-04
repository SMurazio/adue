using System.Linq;
using Mmo.Client.Core.Population;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Population;
using Xunit;

namespace Mmo.Client.Core.Tests;

// NODE-FIELD N3 (docs/node-field-design.md D5/D6): pins the nearest-available-catalogue-node resolution math
// — the node-field analogue of HarvestTargetingTests, now over a NodeFieldChunkIndex instead of an entity
// list. Reach/tie-break rules mirror HarvestTargeting's own tests (same shared InteractionTuning radius, same
// lower-index tie-break). Also proves the Fable-review bounds-safety fix: a hostile/out-of-range index in the
// depleted-set mirror never crashes targeting.
public sealed class NodeFieldTargetingTests
{
    // A small all-grass map with ONLY authored pins (empty scatter class table) so every entry's tile/index is
    // exact, no scatter noise involved.
    private static AuthoredMap MapWithPinsAt(params (int X, int Y, char Marker)[] pins)
    {
        const int width = 40;
        const int height = 40;
        var chars = Enumerable.Range(0, height).Select(_ => Enumerable.Repeat('.', width).ToArray()).ToArray();
        foreach (var (x, y, marker) in pins)
        {
            chars[y][x] = marker;
        }

        return AuthoredMap.Parse(chars.Select(row => new string(row)).ToArray());
    }

    private static NodeFieldChunkIndex BuildIndex(params (int X, int Y, char Marker)[] pins)
    {
        var map = MapWithPinsAt(pins);
        var catalog = NodeCatalog.Build(seed: 1, map, classes: System.Array.Empty<NodeClass>());
        return NodeFieldChunkIndex.Build(catalog);
    }

    private static readonly System.Collections.Generic.HashSet<ushort> NoneDepleted = new();

    private static WorldVector Actor(int x, int y) => WorldVector.FromTile(new TileCoord(x, y));

    [Fact]
    public void PicksInRangeAvailableNode()
    {
        var index = BuildIndex((6, 5, 'T'));

        var found = NodeFieldTargeting.TryFindNearestAvailableNode(index, NoneDepleted, Actor(5, 5), out var nodeIndex, out _);

        Assert.True(found);
        Assert.Equal((ushort)0, nodeIndex);
    }

    [Fact]
    public void IgnoresNodesBeyondInteractionRadius()
    {
        // (7,5) is 2 tiles away — outside the 1.5-tile interaction radius.
        var index = BuildIndex((7, 5, 'T'));

        var found = NodeFieldTargeting.TryFindNearestAvailableNode(index, NoneDepleted, Actor(5, 5), out _, out _);

        Assert.False(found);
    }

    [Fact]
    public void PicksDiagonalNodeWithinRadius()
    {
        // sqrt(2) ~= 1.414 < 1.5.
        var index = BuildIndex((6, 6, 'T'));

        Assert.True(NodeFieldTargeting.TryFindNearestAvailableNode(index, NoneDepleted, Actor(5, 5), out var nodeIndex, out _));
        Assert.Equal((ushort)0, nodeIndex);
    }

    [Fact]
    public void SubTileActorOffsetPushesNodeOutOfRange()
    {
        var index = BuildIndex((7, 5, 'T'));

        // 7 - 5.4 = 1.6 > 1.5 -> out of range.
        Assert.False(NodeFieldTargeting.TryFindNearestAvailableNode(index, NoneDepleted, new WorldVector(5.4d, 5.0d), out _, out _));
    }

    [Fact]
    public void SubTileActorLeanBringsNodeInRange()
    {
        var index = BuildIndex((7, 5, 'T'));

        // 7 - 5.6 = 1.4 < 1.5 -> in range.
        Assert.True(NodeFieldTargeting.TryFindNearestAvailableNode(index, NoneDepleted, new WorldVector(5.6d, 5.0d), out var nodeIndex, out _));
        Assert.Equal((ushort)0, nodeIndex);
    }

    [Fact]
    public void ExcludesDepletedNodes()
    {
        var index = BuildIndex((5, 5, 'T'));
        var depleted = new System.Collections.Generic.HashSet<ushort> { 0 };

        var found = NodeFieldTargeting.TryFindNearestAvailableNode(index, depleted, Actor(5, 5), out _, out _);

        Assert.False(found);
    }

    [Fact]
    public void PrefersNearerNodeThenLowerIndexOnTies()
    {
        // Row-major pin order (y ascending, then x): (6,5) is index 0, (6,6) is index 1 — and (6,5) is also
        // the NEARER one (distance^2 1 vs 2), so both rules agree here.
        var index = BuildIndex((6, 6, 'T'), (6, 5, 'R'));

        var found = NodeFieldTargeting.TryFindNearestAvailableNode(index, NoneDepleted, Actor(5, 5), out var nodeIndex, out _);
        Assert.True(found);
        Assert.Equal((ushort)0, nodeIndex);

        // A genuine tie (distance^2 1 each): (4,5) is index 0, (6,5) is index 1 — lower index wins.
        var tieIndex = BuildIndex((4, 5, 'T'), (6, 5, 'R'));
        Assert.True(NodeFieldTargeting.TryFindNearestAvailableNode(tieIndex, NoneDepleted, Actor(5, 5), out var tieNode, out _));
        Assert.Equal((ushort)0, tieNode);
    }

    [Fact]
    public void ReportsDistanceSquaredOfThePickedNode()
    {
        var index = BuildIndex((6, 5, 'T')); // distance^2 == 1

        Assert.True(NodeFieldTargeting.TryFindNearestAvailableNode(index, NoneDepleted, Actor(5, 5), out _, out var distanceSquared));
        Assert.Equal(1.0, distanceSquared);
    }

    [Fact]
    public void FindsANodeInAnAdjacentChunk_ActorNearTheBoundary()
    {
        // The pin sits just across a chunk boundary (tile 32 is chunk 1; the actor at tile 31 is chunk 0) —
        // the 3x3 neighbour scan must still find it.
        var index = BuildIndex((32, 31, 'T'));

        var found = NodeFieldTargeting.TryFindNearestAvailableNode(index, NoneDepleted, Actor(31, 31), out var nodeIndex, out _);

        Assert.True(found);
        Assert.Equal((ushort)0, nodeIndex);
    }

    // Fable review (N1+N2): a hostile/out-of-range index sitting in the depleted-set mirror (drift, or a
    // corrupted value) must never crash targeting — it simply never matches any real catalogue entry.
    [Fact]
    public void HostileDepletedIndex_IsIgnoredNoThrow()
    {
        var index = BuildIndex((6, 5, 'T'));
        var depletedWithHostileIndex = new System.Collections.Generic.HashSet<ushort> { ushort.MaxValue, 4242 };

        var found = NodeFieldTargeting.TryFindNearestAvailableNode(
            index, depletedWithHostileIndex, Actor(5, 5), out var nodeIndex, out _);

        Assert.True(found);
        Assert.Equal((ushort)0, nodeIndex);
    }

    [Fact]
    public void NoCatalogueEntries_NeverFindsANode()
    {
        var index = BuildIndex(); // no pins at all

        var found = NodeFieldTargeting.TryFindNearestAvailableNode(index, NoneDepleted, Actor(5, 5), out _, out _);

        Assert.False(found);
    }
}
