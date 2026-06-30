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

    // ---- circular body obstacles (PLAYER↔MONSTER COLLISION) -------------------------------------------------

    private static readonly ContinuousCollision.Wall[] NoWalls = System.Array.Empty<ContinuousCollision.Wall>();

    private static double Dist(double ax, double ay, double bx, double by)
        => System.Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));

    [Fact]
    public void Circle_BlocksHeadOnMove_StopsAtRadiusSum_NeverInside()
    {
        // An obstacle of radius 0.5 at the origin; a body of radius 0.5 drives straight east into it from x=-5. The
        // bodies overlap when their centres are within 0.5+0.5 = 1.0, so the moving centre must stop at x=-1.0 — exactly
        // the radius-sum west of the obstacle centre — never penetrating it.
        var obstacles = new[] { new ContinuousCollision.Circle(0d, 0d, 0.5d) };
        var (x, y) = ContinuousCollision.Resolve(-5d, 0d, deltaX: 10d, deltaY: 0d, Radius, NoWalls, obstacles);

        Assert.True(Dist(x, y, 0d, 0d) >= 1.0d - 1e-9, $"penetrated the obstacle: dist={Dist(x, y, 0d, 0d)}");
        Assert.Equal(-1.0d, x, 6); // stopped exactly at the radius-sum surface
        Assert.Equal(0d, y, 6);    // no lateral drift on a head-on hit
    }

    [Fact]
    public void Circle_AngledApproach_SlidesTangentially_NeverInside()
    {
        // Drive into the obstacle with a strong into-it (+X) component and a tangential (+Y) component. The into-circle
        // motion is removed (the body never penetrates), but the tangential motion is preserved => it slides AROUND.
        var obstacles = new[] { new ContinuousCollision.Circle(0d, 0d, 0.5d) };
        var startY = -0.2d;
        var (x, y) = ContinuousCollision.Resolve(-5d, startY, deltaX: 6d, deltaY: 2d, Radius, NoWalls, obstacles);

        Assert.True(Dist(x, y, 0d, 0d) >= 1.0d - 1e-9, $"penetrated the obstacle: dist={Dist(x, y, 0d, 0d)}");
        Assert.True(y > startY + 0.5d, $"tangential motion was not preserved (y only reached {y} from {startY})");
    }

    [Fact]
    public void Circle_MultipleObstacles_ResolveIsDeterministicAndFinite()
    {
        // The player-vs-crowd path resolves against 2+ circles. The de-penetration is Gauss-Seidel (order-dependent),
        // so for a FIXED obstacle order the result must be deterministic + finite. PARITY: the client predictor and the
        // server integrate feed the SAME order (both gathers sort by the shared NetworkId), so they resolve a crowd
        // identically → no rubber-band. (Across DIFFERENT orders the result CAN differ — exactly why the gathers sort.)
        var obstacles = new[]
        {
            new ContinuousCollision.Circle(0.3d, 0.1d, 0.5d),
            new ContinuousCollision.Circle(-0.2d, -0.3d, 0.5d),
            new ContinuousCollision.Circle(0.1d, 0.4d, 0.5d),
        };
        var (x1, y1) = ContinuousCollision.Resolve(0d, 0d, deltaX: 0.4d, deltaY: 0.2d, Radius, NoWalls, obstacles);
        var (x2, y2) = ContinuousCollision.Resolve(0d, 0d, deltaX: 0.4d, deltaY: 0.2d, Radius, NoWalls, obstacles);

        Assert.True(double.IsFinite(x1) && double.IsFinite(y1), "resolve produced a non-finite result");
        Assert.Equal(x1, x2, 12); // same obstacles + same order → identical result
        Assert.Equal(y1, y2, 12);
    }

    [Fact]
    public void Circle_BetweenWallAndObstacle_ResolvesBoth_NoPenetrationOfEither()
    {
        // A body pinned between a wall (below) and a circle obstacle (ahead) must settle clear of BOTH — the
        // walls+circles passes iterate so neither is penetrated. Wall: the box [-10,10]x[-10,0] (a floor; its +Y face
        // is y=0). Obstacle: a circle at (0, 1.0) radius 0.5. Drive a body up-and-into both from (-5,-0.2).
        var walls = new[] { ContinuousCollision.Wall.FromCenter(0d, -5d, 10d, 5d) }; // +Y face at y=0
        var obstacles = new[] { new ContinuousCollision.Circle(0d, 1.0d, 0.5d) };
        var (x, y) = ContinuousCollision.Resolve(-5d, -0.2d, deltaX: 6d, deltaY: 3d, Radius, walls, obstacles);

        // Clear of the wall: centre y must be >= the +Y face (0) + radius (0.5).
        Assert.True(y >= 0.5d - 1e-9, $"penetrated the wall floor: y={y}");
        // Clear of the obstacle: centre at least the radius-sum (1.0) from the obstacle centre.
        Assert.True(Dist(x, y, 0d, 1.0d) >= 1.0d - 1e-9, $"penetrated the obstacle: dist={Dist(x, y, 0d, 1.0d)}");
    }

    [Fact]
    public void Circle_ExactOverlap_EjectsDeterministically_NoNaN_ToRadiusSum()
    {
        // Degenerate: the body centre starts EXACTLY on the obstacle centre (a monster spawned/teleported on top). The
        // resolver must eject it to the radius-sum along a DETERMINISTIC axis (no 0/0 NaN), and repeat byte-identically.
        var obstacles = new[] { new ContinuousCollision.Circle(3d, 3d, 0.5d) };
        var (x, y) = ContinuousCollision.Resolve(3d, 3d, deltaX: 0d, deltaY: 0d, Radius, NoWalls, obstacles);

        Assert.True(double.IsFinite(x) && double.IsFinite(y), $"NaN/Inf eject: ({x},{y})");
        Assert.Equal(1.0d, Dist(x, y, 3d, 3d), 6); // ejected to exactly the radius-sum

        var (x2, y2) = ContinuousCollision.Resolve(3d, 3d, deltaX: 0d, deltaY: 0d, Radius, NoWalls, obstacles);
        Assert.Equal(System.BitConverter.DoubleToInt64Bits(x), System.BitConverter.DoubleToInt64Bits(x2));
        Assert.Equal(System.BitConverter.DoubleToInt64Bits(y), System.BitConverter.DoubleToInt64Bits(y2));
    }

    [Fact]
    public void ObstacleOverload_WithEmptyObstacles_ByteIdentical_ToWallsOnlyResolve()
    {
        // The new walls+obstacles overload with an EMPTY obstacle list must reproduce the walls-only Resolve bit-for-bit
        // — the regression guard that the obstacle plumbing never perturbs the existing predicted/integrated wall path.
        var walls = OneBox();
        var noObstacles = System.Array.Empty<ContinuousCollision.Circle>();

        var (wx, wy) = ContinuousCollision.Resolve(-5d, -1d, 6d, 2d, Radius, walls);
        var (ox, oy) = ContinuousCollision.Resolve(-5d, -1d, 6d, 2d, Radius, walls, noObstacles);

        Assert.Equal(System.BitConverter.DoubleToInt64Bits(wx), System.BitConverter.DoubleToInt64Bits(ox));
        Assert.Equal(System.BitConverter.DoubleToInt64Bits(wy), System.BitConverter.DoubleToInt64Bits(oy));
    }

    [Fact]
    public void ObstacleOverload_NoWallsNoObstacles_IsPurePassThrough()
    {
        // Open field AND no obstacle: the move is returned exactly (start + delta), the same fast path the walls-only
        // overload takes — so an entity with neither walls nor monsters nearby integrates unchanged.
        var (x, y) = ContinuousCollision.Resolve(1d, 2d, 5d, -3d, Radius, NoWalls, System.Array.Empty<ContinuousCollision.Circle>());
        Assert.Equal(6d, x, 9);
        Assert.Equal(-1d, y, 9);
    }
}
