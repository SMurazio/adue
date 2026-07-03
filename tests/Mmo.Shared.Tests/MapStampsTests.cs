using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Shared.Tests;

public sealed class MapStampsTests
{
    [Fact]
    public void OpsStampInclusiveCoordsInCallOrder()
    {
        // One tiny program covering every op; later ops overwrite earlier ones (the ordered-program
        // contract) and all coordinates are INCLUSIVE on both ends.
        var rows = new MapStamps(6, 5, '.')
            .FillRect(1, 1, 4, 3, ',')  // interior block
            .Border(0, 0, 5, 4, 1, '#') // ring OVER the fill's touching edge
            .HLine(2, 1, 4, ':')        // row y=2, x 1..4
            .VLine(2, 1, 3, '-')        // col x=2, y 1..3 (overwrites the HLine at (2,2))
            .Put(4, 3, 'S')
            .Emit();

        Assert.Equal(
            new[]
            {
                "######",
                "#,-,,#",
                "#:-::#",
                "#,-,S#",
                "######",
            },
            rows);
    }

    [Fact]
    public void BorderLeavesInteriorUntouchedAndSupportsThickness()
    {
        var rows = new MapStamps(7, 7, '.')
            .Border(1, 1, 5, 5, 2, '#')
            .Emit();

        Assert.Equal(
            new[]
            {
                ".......",
                ".#####.",
                ".#####.",
                ".##.##.",
                ".#####.",
                ".#####.",
                ".......",
            },
            rows);
    }

    [Theory]
    [InlineData(-1, 0, 2, 2)] // off west
    [InlineData(0, -1, 2, 2)] // off south
    [InlineData(0, 0, 4, 2)]  // off east
    [InlineData(0, 0, 2, 4)]  // off north
    [InlineData(2, 0, 1, 2)]  // inverted x
    [InlineData(0, 2, 2, 1)]  // inverted y
    public void OutOfBoundsOrInvertedStampThrows(int x0, int y0, int x1, int y1)
    {
        // A stamp outside the canvas is an AUTHORING error and must fail loudly — a silently clipped
        // stamp would be an authored layout that quietly differs from its program.
        var canvas = new MapStamps(4, 4, '.');
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.FillRect(x0, y0, x1, y1, '#'));
    }

    [Fact]
    public void ExpansionIsDeterministicAcrossRuns()
    {
        // The D2a determinism contract: re-running the SAME stamp program yields byte-identical rows.
        // Checked on the real program (the shipped map) — two independent expansions, plus the
        // canonical static instance they must both match.
        var first = AuthoredMaps.BuildTownAndFloor1();
        var second = AuthoredMaps.BuildTownAndFloor1();

        Assert.Equal(first, second);
        Assert.Equal(AuthoredMaps.TownAndFloor1, first);
        // And the parsed layouts hash identically (the value the drift guard actually compares).
        Assert.Equal(
            TerrainGenerator.ContentHash(AuthoredMap.Parse(first)),
            TerrainGenerator.ContentHash(AuthoredMap.Parse(second)));
    }
}
