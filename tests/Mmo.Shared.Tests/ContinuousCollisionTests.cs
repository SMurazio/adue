using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Shared.Tests;

// CONTINUOUS MIGRATION (Phase 2): headless, deterministic tests for the SHARED swept-circle collision resolver — a
// Z->Y port of the proven exp/continuous-movement spike tests. These pin the four resolver behaviours (stop at a
// wall, slide along it, no tunnelling, open move unaffected) AND the byte-identical determinism that the Phase-4
// client predictor depends on (the same (start, delta, walls, radius) must yield bit-for-bit identical doubles, so
// the server integrator and the client predictor land on the same position at a wall).
public sealed class ContinuousCollisionTests
{
    private const double Radius = 0.5d;

    // A single solid box centred at the origin, half-extent 2 => occupies [-2,2] x [-2,2].
    private static ContinuousCollision.Wall[] OneBox() => new[]
    {
        ContinuousCollision.Wall.FromCenter(0d, 0d, 2d, 2d),
    };

    private static bool Penetrates(double x, double y, double radius, ContinuousCollision.Wall[] walls)
    {
        foreach (var w in walls)
        {
            var cx = x < w.MinX ? w.MinX : (x > w.MaxX ? w.MaxX : x);
            var cy = y < w.MinY ? w.MinY : (y > w.MaxY ? w.MaxY : y);
            var dx = x - cx;
            var dy = y - cy;
            // A hair of slack for the push-out landing exactly on the surface (float-free double, but the divide
            // leaves ~1e-12). Anything deeper than that is a real penetration.
            if ((dx * dx) + (dy * dy) < (radius * radius) - 1e-9)
            {
                return true;
            }
        }

        return false;
    }

    // ---- resolver behaviours -------------------------------------------------------------------------------

    [Fact]
    public void StraightIntoWall_StopsAtSurface_NoPenetration_NoPassThrough()
    {
        var walls = OneBox();
        // Start west of the box at (-5,0), drive +X straight into the -X face. The box -X face is at x=-2; with a
        // radius of 0.5 the centre must stop at x = -2.5 (face minus radius), never inside, never beyond.
        var (x, y) = ContinuousCollision.Resolve(-5d, 0d, deltaX: 10d, deltaY: 0d, Radius, walls);

        Assert.False(Penetrates(x, y, Radius, walls), $"penetrated the wall: ({x},{y})");
        Assert.True(x <= -2d + 1e-9, $"passed through to x={x} (should stop before the -X face at -2)");
        Assert.Equal(-2.5d, x, 6); // stopped exactly at the surface (face -2 minus radius 0.5)
        Assert.Equal(0d, y, 6);    // no lateral drift on a head-on hit
    }

    [Fact]
    public void AngledIntoWall_SlidesAlong_TangentialMotionPreserved_NormalRemoved()
    {
        var walls = OneBox();
        // Start below-and-west, drive into the -X face with a strong +X (into-wall) component and a MODERATE +Y
        // (tangential) component sized to stay ON the face. The X component is blocked at the face; the tangential Y
        // is PRESERVED — the body slides UP along the face.
        var startY = -1d;
        var (x, y) = ContinuousCollision.Resolve(-5d, startY, deltaX: 6d, deltaY: 2d, Radius, walls);

        Assert.False(Penetrates(x, y, Radius, walls));
        // Normal (X) motion removed: stopped at the face (x ~ -2.5), did not pass through.
        Assert.True(x <= -2d, $"slid through the wall in X: {x}");
        Assert.Equal(-2.5d, x, 6);
        // Tangential (Y) motion preserved: moved a meaningful distance UP the face, not stopped dead.
        Assert.True(y > startY + 1d, $"tangential motion was not preserved (y only reached {y} from {startY})");
    }

    [Fact]
    public void FastMove_DoesNotTunnel_ThroughThinWall()
    {
        // A THIN wall (0.2 thick) and a single move that is many radii long. A naive endpoint test would teleport
        // start->end straight past it; sub-stepping must catch the intermediate penetration and stop at the surface.
        var thin = new[] { ContinuousCollision.Wall.FromCenter(0d, 0d, 0.1d, 10d) }; // 0.2 wide in X, tall in Y
        // Start well west, fire a 40-unit move east in ONE call (hundreds of radii).
        var (x, y) = ContinuousCollision.Resolve(-20d, 0d, deltaX: 40d, deltaY: 0d, Radius, thin);

        Assert.False(Penetrates(x, y, Radius, thin), $"tunnelled into the thin wall: ({x},{y})");
        // Must end on the NEAR (-X) side: x <= the -X face (-0.1) minus radius. NOT on the far side (~+20).
        Assert.True(x <= -0.1d + 1e-9, $"tunnelled THROUGH the thin wall to x={x}");
        Assert.Equal(-0.6d, x, 6); // stopped at face -0.1 minus radius 0.5
    }

    [Fact]
    public void OpenMove_AwayFromWalls_Unaffected()
    {
        var walls = OneBox();
        // A move that never comes near the box is returned exactly (start + delta), untouched.
        var (x, y) = ContinuousCollision.Resolve(10d, 10d, deltaX: 3d, deltaY: -2d, Radius, walls);
        Assert.Equal(13d, x, 9);
        Assert.Equal(8d, y, 9);
    }

    [Fact]
    public void NoWalls_IsPurePassThrough()
    {
        var (x, y) = ContinuousCollision.Resolve(1d, 2d, 5d, -3d, Radius, System.Array.Empty<ContinuousCollision.Wall>());
        Assert.Equal(6d, x, 9);
        Assert.Equal(-1d, y, 9);
    }

    [Fact]
    public void WorldVectorOverload_MatchesPrimitive_ByteIdentical()
    {
        // The thin WorldVector wrapper must produce bit-for-bit the same doubles as the primitive — it is the only
        // call form the server uses, so any divergence here would silently break the determinism contract.
        var walls = OneBox();
        var (px, py) = ContinuousCollision.Resolve(-5d, -1d, 6d, 2d, Radius, walls);
        var v = ContinuousCollision.Resolve(new WorldVector(-5d, -1d), new WorldVector(6d, 2d), Radius, walls);

        Assert.Equal(System.BitConverter.DoubleToInt64Bits(px), System.BitConverter.DoubleToInt64Bits(v.X));
        Assert.Equal(System.BitConverter.DoubleToInt64Bits(py), System.BitConverter.DoubleToInt64Bits(v.Y));
    }

    // ---- determinism / lockstep ----------------------------------------------------------------------------

    [Fact]
    public void Determinism_SameInputs_ByteIdenticalResult()
    {
        var walls = OneBox();
        // The same (start, delta, radius, walls) must produce bit-for-bit identical doubles every call — that is
        // what lets the client predict and the server integrate agree at a wall. Compare raw IEEE bits, not an eps.
        var (ax, ay) = ContinuousCollision.Resolve(-5d, -1d, 6d, 6d, Radius, walls);
        var (bx, by) = ContinuousCollision.Resolve(-5d, -1d, 6d, 6d, Radius, walls);

        Assert.Equal(System.BitConverter.DoubleToInt64Bits(ax), System.BitConverter.DoubleToInt64Bits(bx));
        Assert.Equal(System.BitConverter.DoubleToInt64Bits(ay), System.BitConverter.DoubleToInt64Bits(by));
    }

    [Fact]
    public void Determinism_ArrayAndList_ByteIdentical_SameOrder()
    {
        // The server hands a reused List<Wall> scratch; the tests hand a Wall[]. Both implement IReadOnlyList<Wall>
        // and are iterated by the SAME index order, so the result must be bit-for-bit identical — the container type
        // is NOT part of the contract, the wall ORDER is. (Two-wall layout so a slide settles across both.)
        var array = new[]
        {
            ContinuousCollision.Wall.FromCenter(0d, 0d, 2d, 2d),
            ContinuousCollision.Wall.FromCenter(4d, 0d, 1d, 2d),
        };
        var list = new System.Collections.Generic.List<ContinuousCollision.Wall>(array);

        var (ax, ay) = ContinuousCollision.Resolve(-5d, -0.3d, 12d, 0.7d, Radius, array);
        var (bx, by) = ContinuousCollision.Resolve(-5d, -0.3d, 12d, 0.7d, Radius, list);

        Assert.Equal(System.BitConverter.DoubleToInt64Bits(ax), System.BitConverter.DoubleToInt64Bits(bx));
        Assert.Equal(System.BitConverter.DoubleToInt64Bits(ay), System.BitConverter.DoubleToInt64Bits(by));
    }
}
