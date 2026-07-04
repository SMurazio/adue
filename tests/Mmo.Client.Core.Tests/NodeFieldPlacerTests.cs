using System.Linq;
using Mmo.Client.Core;
using Mmo.Client.Core.Population;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Population;
using Xunit;

namespace Mmo.Client.Core.Tests;

// NODE-FIELD N3 (docs/node-field-design.md D6): pins NodeFieldPlacer's determinism contract and the jitter
// bounds — the "for life" per-instance rotation/scale/sub-tile-offset DecorPlacer already gives scattered
// decor, reused here for catalogue nodes.
public sealed class NodeFieldPlacerTests
{
    private static AuthoredMap StripedRoadMap(int width, int height)
    {
        var mid = width / 2;
        var rows = new string[height];
        for (var y = 0; y < height; y++)
        {
            var chars = new char[width];
            for (var x = 0; x < width; x++)
            {
                chars[x] = x == mid || x == mid + 1 ? ',' : '.';
            }

            rows[y] = new string(chars);
        }

        return AuthoredMap.Parse(rows);
    }

    [Fact]
    public void SameSeed_ProducesIdenticalPlacements()
    {
        var map = StripedRoadMap(60, 60);
        var catalog = NodeCatalog.Build(42, map);

        var first = NodeFieldPlacer.PlaceAll(catalog, zoneSeed: 42);
        var second = NodeFieldPlacer.PlaceAll(catalog, zoneSeed: 42);

        Assert.True(first.SequenceEqual(second), "Same (catalogue, zoneSeed) produced different placements.");
    }

    [Fact]
    public void DifferentSeed_ProducesDifferentJitter()
    {
        var map = StripedRoadMap(60, 60);
        var catalog = NodeCatalog.Build(42, map);

        var first = NodeFieldPlacer.PlaceAll(catalog, zoneSeed: 42);
        var second = NodeFieldPlacer.PlaceAll(catalog, zoneSeed: 99);

        Assert.False(first.SequenceEqual(second), "Two different zone seeds produced byte-identical node jitter.");
    }

    [Fact]
    public void OnePlacementPerCatalogueEntry_IndexAndTypeAligned()
    {
        var map = StripedRoadMap(60, 60);
        var catalog = NodeCatalog.Build(7, map);

        var placements = NodeFieldPlacer.PlaceAll(catalog, zoneSeed: 7);

        Assert.Equal(catalog.Entries.Count, placements.Count);
        for (var i = 0; i < catalog.Entries.Count; i++)
        {
            Assert.Equal(catalog.Entries[i].Index, placements[i].Index);
            Assert.Equal(catalog.Entries[i].NodeType, placements[i].Type);
            Assert.Equal(i, placements[i].Index); // Index IS the position, per NodeCatalog.Build's contract.
        }
    }

    [Fact]
    public void JitteredPosition_StaysWithinHalfATileOfTheCatalogueTile()
    {
        var map = StripedRoadMap(60, 60);
        var catalog = NodeCatalog.Build(7, map);

        var placements = NodeFieldPlacer.PlaceAll(catalog, zoneSeed: 7);

        Assert.NotEmpty(placements);
        for (var i = 0; i < placements.Count; i++)
        {
            var tile = catalog.Entries[i].Tile;
            var instance = placements[i].Instance;
            Assert.True(System.Math.Abs(instance.X - tile.X) < 0.5f, $"Entry {i}: X jitter out of bounds.");
            Assert.True(System.Math.Abs(instance.Z - tile.Y) < 0.5f, $"Entry {i}: Z jitter out of bounds.");
        }
    }

    [Fact]
    public void RotationIsWithinTheFullCircle()
    {
        var map = StripedRoadMap(60, 60);
        var catalog = NodeCatalog.Build(7, map);

        var placements = NodeFieldPlacer.PlaceAll(catalog, zoneSeed: 7);

        Assert.All(placements, p => Assert.InRange(p.Instance.RotationRadians, 0f, (float)(2.0 * System.Math.PI)));
    }

    [Fact]
    public void ScaleStaysPositiveAndNearOne()
    {
        var map = StripedRoadMap(60, 60);
        var catalog = NodeCatalog.Build(7, map);

        var placements = NodeFieldPlacer.PlaceAll(catalog, zoneSeed: 7);

        // The largest configured per-type scale-jitter half-range is 0.25 (Plant) -- every scale must land in
        // (1 - 0.25, 1 + 0.25) with headroom, and always stay strictly positive (a zero/negative scale would
        // be a degenerate, invisible or inverted instance).
        Assert.All(placements, p =>
        {
            Assert.True(p.Instance.Scale > 0f, "A placed node's scale was not positive.");
            Assert.InRange(p.Instance.Scale, 0.5f, 1.5f);
        });
    }

    [Fact]
    public void EmptyCatalogue_ProducesNoPlacements()
    {
        var placements = NodeFieldPlacer.PlaceAll(NodeCatalog.Empty(), zoneSeed: 1);

        Assert.Empty(placements);
    }

    [Fact]
    public void PackIntoABuffer_ViaTheExistingDecorTransformPacker()
    {
        // The whole point of reusing DecorPlacer.DecorInstance: NodeFieldPlacer's output must round-trip
        // through the EXISTING MultiMeshTileBuffer.PackDecorTransforms with no new buffer layout.
        var map = StripedRoadMap(60, 60);
        var catalog = NodeCatalog.Build(7, map);
        var placements = NodeFieldPlacer.PlaceAll(catalog, zoneSeed: 7);
        var instances = placements.Select(p => p.Instance).ToList();

        var buffer = MultiMeshTileBuffer.PackDecorTransforms(instances, groundY: 0.032f);

        Assert.Equal(instances.Count * MultiMeshTileBuffer.FloatsPerInstance, buffer.Length);
        for (var i = 0; i < instances.Count; i++)
        {
            var o = i * MultiMeshTileBuffer.FloatsPerInstance;
            Assert.Equal(instances[i].X, buffer[o + 3]);
            Assert.Equal(0.032f, buffer[o + 7]);
            Assert.Equal(instances[i].Z, buffer[o + 11]);
        }
    }
}
