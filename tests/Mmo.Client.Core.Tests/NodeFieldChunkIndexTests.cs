using System.Linq;
using Mmo.Client.Core.Population;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Population;
using Xunit;

namespace Mmo.Client.Core.Tests;

// NODE-FIELD N3 (docs/node-field-design.md D6): pins NodeFieldChunkIndex's bucketing (which 32-tile chunk each
// catalogue entry lands in) and the Fable-review bounds-safety fix — a mirrored index this catalogue never
// actually issued (drift/hostile) must resolve to "not found", never throw.
public sealed class NodeFieldChunkIndexTests
{
    // A grass map with ONLY authored Tree/Rock pins (an empty scatter class table — NodeCatalog.Build's Step 2
    // loop is a no-op) so every entry's tile/index is exact and hand-picked, with zero scatter noise to
    // account for. Four pins spanning four distinct 32-tile chunks (ChunkTiles = 32).
    private static AuthoredMap FourChunkMap()
    {
        var rows = new string[64];
        for (var y = 0; y < 64; y++)
        {
            rows[y] = new string('.', 96);
        }

        var chars = rows.Select(r => r.ToCharArray()).ToArray();
        chars[5][5] = 'T';   // chunk (0, 0)
        chars[5][33] = 'R';  // chunk (1, 0)
        chars[40][5] = 'T';  // chunk (0, 1)
        chars[50][70] = 'R'; // chunk (2, 1)

        return AuthoredMap.Parse(chars.Select(c => new string(c)).ToArray());
    }

    private static NodeCatalog BuildPinOnlyCatalog() =>
        NodeCatalog.Build(seed: 1, FourChunkMap(), classes: System.Array.Empty<NodeClass>());

    [Fact]
    public void PinsAreOrderedRowMajor_IndicesZeroToThree()
    {
        var catalog = BuildPinOnlyCatalog();

        Assert.Equal(4, catalog.Entries.Count);
        Assert.Equal(new TileCoord(5, 5), catalog.Entries[0].Tile);
        Assert.Equal(new TileCoord(33, 5), catalog.Entries[1].Tile);
        Assert.Equal(new TileCoord(5, 40), catalog.Entries[2].Tile);
        Assert.Equal(new TileCoord(70, 50), catalog.Entries[3].Tile);
    }

    [Fact]
    public void BucketsEachEntryIntoItsOwn32TileChunk()
    {
        var catalog = BuildPinOnlyCatalog();
        var index = NodeFieldChunkIndex.Build(catalog);

        Assert.Equal(4, index.ChunkKeys.Count);
        Assert.Contains((0, 0), index.ChunkKeys);
        Assert.Contains((1, 0), index.ChunkKeys);
        Assert.Contains((0, 1), index.ChunkKeys);
        Assert.Contains((2, 1), index.ChunkKeys);

        Assert.Single(index.EntriesIn((0, 0)));
        Assert.Equal(0, index.EntriesIn((0, 0))[0].Index);

        Assert.Single(index.EntriesIn((1, 0)));
        Assert.Equal(1, index.EntriesIn((1, 0))[0].Index);
    }

    [Fact]
    public void EntriesInAnEmptyChunk_ReturnsEmptyNotNull()
    {
        var catalog = BuildPinOnlyCatalog();
        var index = NodeFieldChunkIndex.Build(catalog);

        var entries = index.EntriesIn((5, 5));

        Assert.NotNull(entries);
        Assert.Empty(entries);
    }

    [Fact]
    public void TryChunkOfIndex_ResolvesEveryValidIndex()
    {
        var catalog = BuildPinOnlyCatalog();
        var index = NodeFieldChunkIndex.Build(catalog);

        Assert.True(index.TryChunkOfIndex(0, out var chunk0));
        Assert.Equal((0, 0), chunk0);

        Assert.True(index.TryChunkOfIndex(3, out var chunk3));
        Assert.Equal((2, 1), chunk3);
    }

    // Fable review (N1+N2): the depleted-index mirror is unchecked against the catalogue it was built from — a
    // drifted/hostile index must be safely ignored here, never thrown on.
    [Fact]
    public void TryChunkOfIndex_HostileOutOfRangeIndex_ReturnsFalseNoThrow()
    {
        var catalog = BuildPinOnlyCatalog();
        var index = NodeFieldChunkIndex.Build(catalog);

        Assert.False(index.TryChunkOfIndex(4, out _));
        Assert.False(index.TryChunkOfIndex(ushort.MaxValue, out _));
    }

    [Fact]
    public void ChunksTouchedBy_IgnoresHostileIndicesAndReturnsOnlyRealChunks()
    {
        var catalog = BuildPinOnlyCatalog();
        var index = NodeFieldChunkIndex.Build(catalog);

        var touched = index.ChunksTouchedBy(new ushort[] { 0, 3, 4, ushort.MaxValue });

        Assert.Equal(2, touched.Count);
        Assert.Contains((0, 0), touched);
        Assert.Contains((2, 1), touched);
    }
}
