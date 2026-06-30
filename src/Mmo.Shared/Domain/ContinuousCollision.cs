namespace Mmo.Shared.Domain;

// CONTINUOUS MIGRATION (Phase 2): the SHARED, DETERMINISTIC swept-circle collision resolver — a direct port of the
// proven exp/continuous-movement spike (exp:Mmo.Client.Core.Continuous.ContinuousCollision) with the established
// Z->Y rename (the spike's X/Z ground plane -> this game's X/Y plane). Lives in Mmo.Shared.Domain (not the server,
// not Client.Core) because BOTH the server integrator (Phase 2, now) AND the Phase-4 client predictor must call the
// BYTE-IDENTICAL resolver on the SAME walls + radius. That byte-identity is the determinism contract that keeps
// prediction honest at a wall: when the client predicts a slide along a wall and the server integrates the same
// inputs, they land on the same position, so the reconcile opens NO correction.
//
// THE BODY is a CIRCLE of radius `radius` (top-down). Walls are axis-aligned solid boxes (AABBs). A circle-vs-AABB
// test is the classic "expand the AABB by the radius and clamp" Minkowski form: the circle CENTER must stay out of
// the AABB grown by `radius` on every side (with rounded corners). We resolve by pushing the center out to the
// nearest surface of that expanded shape.
//
// APPROACH — SUB-STEPPED (not a single analytic sweep):
//   * ANTI-TUNNELING: a fast move (several radii in one frame) could pass straight THROUGH a thin wall if we just
//     teleported start->end and tested the endpoint. So we SPLIT the move delta into N pieces each no longer than
//     ~radius (SubStepMaxFraction * radius), and resolve collision after EACH piece. A piece can never skip over a
//     wall thicker than ~radius; the derived tile walls are 1x1 (>= one radius for radius < 0.5), so nothing tunnels.
//     Chosen over an analytic swept-circle-vs-AABB test because sub-stepping is dramatically simpler to make
//     BYTE-identical across client/server (fewer branches, no quadratic-root edge cases) and the per-tick deltas
//     here are sub-tile (speed * 1/TickRate), so the cost is ~1 sub-step in the common case.
//   * WALL-SLIDE: within a sub-step we resolve each wall by pushing the circle center out along the axis of
//     MINIMUM penetration (the shortest way out). Pushing out along only the penetration normal REMOVES the motion
//     component INTO the wall while PRESERVING the tangential component — i.e. you slide ALONG the wall instead of
//     stopping dead. Resolving all overlapping walls in a sub-step (a couple of fixpoint passes for corners where
//     two walls overlap the circle at once) settles the center to a non-penetrating spot.
//
// DETERMINISM notes (the whole point — do NOT weaken any of these): iteration over walls is in array order (stable
// row-major, as TileWalls/the server query emit them); all comparisons and among-ties resolve the SAME way every
// call (the deterministic tie-break order — X axis wins an exact tie — is part of the byte-identical contract);
// everything is `double` (no float, no SIMD); there is NO RNG, no time, no platform calls. Any divergence here
// desyncs Phase-4 prediction at every wall.
public static class ContinuousCollision
{
    // A solid axis-aligned box obstacle, by its min/max corner in world XY. A wall is immovable; the moving body
    // (a circle) is pushed out of it.
    public readonly record struct Wall(double MinX, double MinY, double MaxX, double MaxY)
    {
        // Construct from a center + half-extents (convenient for laying out tile AABBs — see TileWalls.ForTile).
        public static Wall FromCenter(double centerX, double centerY, double halfX, double halfY) =>
            new(centerX - halfX, centerY - halfY, centerX + halfX, centerY + halfY);
    }

    // PLAYER↔MONSTER COLLISION: a circular body obstacle — another entity (a monster, from the player's POV; a player,
    // from a monster's POV) the moving circle must not penetrate. Unlike a Wall (an immovable AABB), a Circle is just a
    // centre + radius; the moving body is de-penetrated out of it to exactly (movingRadius + obstacle.Radius) along the
    // centre→centre axis. The obstacle is treated as STATIC for the duration of one Resolve (a snapshot of where it is
    // this step) — it is never itself moved here. Determinism: the caller passes the obstacles in a STABLE order (the
    // server/client gather emit them in a fixed order); the de-penetration is all-double, RNG-free, and an exact-overlap
    // (centres coincident) falls back to a deterministic axis derived from the obstacle's list INDEX (never a 0/0 NaN).
    public readonly record struct Circle(double X, double Y, double Radius);

    // The longest a single sub-step's move may be, as a fraction of the body radius. <= 1 guarantees no sub-step
    // moves more than one radius, so the circle cannot pass through a wall at least ~one-radius thick without the
    // intermediate sub-step landing inside it and being resolved. 0.5 is comfortably conservative. PINNED — part of
    // the byte-identical contract (Phase 4 must use the same value).
    private const double SubStepMaxFraction = 0.5d;

    // How many penetration-resolution passes per sub-step. One pass resolves a single wall; at an inside corner the
    // circle can overlap two walls at once, so a second pass cleans up the residual from the first push. Two passes
    // settle every layout here (axis-aligned tile boxes); the count is FIXED (not data-dependent) so the work — and
    // thus the result — is identical on client and server. PINNED.
    private const int ResolvePasses = 2;

    // Resolve a desired move: from (startX, startY) apply (deltaX, deltaY) for a body of `radius` against `walls`,
    // returning the collided end position (slid along walls, never penetrating, never tunneling). PURE: no state,
    // no allocation. Deterministic for a given (start, delta, walls, radius). This is the primitive hot path; the
    // WorldVector overload below is a thin readability wrapper over it.
    //
    // `walls` is IReadOnlyList<Wall> so the server can hand its REUSED List<Wall> scratch buffer straight in (zero
    // per-tick alloc) and the tests can hand a Wall[] — BOTH iterate by index in the SAME order, so the result is
    // byte-identical regardless of the concrete list type (the determinism contract is about wall ORDER + the
    // all-double math, not the container). Iterated by index (not foreach) to avoid an enumerator allocation.
    public static (double X, double Y) Resolve(
        double startX, double startY, double deltaX, double deltaY, double radius, IReadOnlyList<Wall> walls)
    {
        var x = startX;
        var y = startY;

        if (walls is null || walls.Count == 0)
        {
            // Open move: unaffected (the no-collision fast path — identical on both sides).
            return (x + deltaX, y + deltaY);
        }

        var moveLen = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (moveLen <= 1e-12)
        {
            // No motion this step, but the start could already be penetrating (e.g. a wall spawned on top, or a
            // prior step left a hair of overlap) — resolve in place so the body is always pushed clear.
            return ResolvePenetration(x, y, radius, walls);
        }

        // Split into sub-steps each no longer than SubStepMaxFraction * radius (anti-tunneling). At least 1.
        var maxStep = SubStepMaxFraction * radius;
        var subSteps = (int)Math.Ceiling(moveLen / maxStep);
        if (subSteps < 1)
        {
            subSteps = 1;
        }

        var stepX = deltaX / subSteps;
        var stepY = deltaY / subSteps;

        for (var i = 0; i < subSteps; i++)
        {
            x += stepX;
            y += stepY;
            (x, y) = ResolvePenetration(x, y, radius, walls);
        }

        return (x, y);
    }

    // WorldVector overload (call-site readability for the server integrator / future client predictor). A thin
    // wrapper over the primitive — same math, same determinism. `walls` is the row-major Wall list the query /
    // TileWalls produced (the server passes its reused List<Wall> scratch directly).
    public static WorldVector Resolve(WorldVector start, WorldVector delta, double radius, IReadOnlyList<Wall> walls)
    {
        var (x, y) = Resolve(start.X, start.Y, delta.X, delta.Y, radius, walls);
        return new WorldVector(x, y);
    }

    // PLAYER↔MONSTER COLLISION: the swept-circle resolver EXTENDED with circular body obstacles. Identical sub-stepping
    // + wall handling to the walls-only Resolve above (same anti-tunnelling, same slide, same byte-identical math), but
    // after the wall resolution in EACH sub-step's pass it ALSO de-penetrates the body out of every `obstacles` Circle.
    // Iterating walls AND circles together across ResolvePasses means a body pinned between a wall and a monster settles
    // against BOTH (it stops, never penetrating either), exactly like an inside wall corner. This is the SHARED resolver
    // the server player integrator AND the client predictor call with the SAME monster-obstacle set, so a predicted
    // slide along a monster lands where the server integrates it (the determinism contract, now WITH dynamic obstacles —
    // approximate only to the extent the obstacle MOVED between the client's stale snapshot and the server's authoritative
    // position; vs a stationary obstacle it is exact). The walls-only Resolve above is UNCHANGED (back-compat). PURE: no
    // alloc — the caller hands in its reused walls + obstacles scratch lists. Deterministic for a given (start, delta,
    // walls, obstacles, radius).
    public static (double X, double Y) Resolve(
        double startX, double startY, double deltaX, double deltaY, double radius,
        IReadOnlyList<Wall> walls, IReadOnlyList<Circle> obstacles)
    {
        var x = startX;
        var y = startY;

        var wallCount = walls?.Count ?? 0;
        var obstacleCount = obstacles?.Count ?? 0;
        if (wallCount == 0 && obstacleCount == 0)
        {
            // Open move: unaffected (the no-collision fast path — identical to the walls-only Resolve's open path).
            return (x + deltaX, y + deltaY);
        }

        var moveLen = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (moveLen <= 1e-12)
        {
            // No motion this step, but the start could already be penetrating a wall OR overlapping an obstacle that
            // moved onto us — resolve in place against both so the body is always pushed clear.
            return ResolvePenetration(x, y, radius, walls, obstacles);
        }

        // Anti-tunnelling sub-steps, identical to the walls-only path (a circle obstacle is at most one body across, so
        // the same SubStepMaxFraction*radius cap that prevents wall tunnelling prevents tunnelling through a body too).
        var maxStep = SubStepMaxFraction * radius;
        var subSteps = (int)Math.Ceiling(moveLen / maxStep);
        if (subSteps < 1)
        {
            subSteps = 1;
        }

        var stepX = deltaX / subSteps;
        var stepY = deltaY / subSteps;

        for (var i = 0; i < subSteps; i++)
        {
            x += stepX;
            y += stepY;
            (x, y) = ResolvePenetration(x, y, radius, walls, obstacles);
        }

        return (x, y);
    }

    // WorldVector overload of the walls+obstacles resolver (call-site readability for the server integrator / client
    // predictor / monster locomotions). A thin wrapper over the primitive — same math, same determinism.
    public static WorldVector Resolve(
        WorldVector start, WorldVector delta, double radius,
        IReadOnlyList<Wall> walls, IReadOnlyList<Circle> obstacles)
    {
        var (x, y) = Resolve(start.X, start.Y, delta.X, delta.Y, radius, walls, obstacles);
        return new WorldVector(x, y);
    }

    // Push the circle center out of every wall AND every obstacle it penetrates, over a FIXED ResolvePasses passes (so
    // a body wedged against a wall + a monster at once settles, like an inside wall corner). Walls are resolved FIRST in
    // each pass (preserving the walls-only push order/result), then the circle obstacles, in their stable list order.
    private static (double X, double Y) ResolvePenetration(
        double x, double y, double radius, IReadOnlyList<Wall>? walls, IReadOnlyList<Circle>? obstacles)
    {
        var wallCount = walls?.Count ?? 0;
        var obstacleCount = obstacles?.Count ?? 0;

        for (var pass = 0; pass < ResolvePasses; pass++)
        {
            var movedAny = false;

            for (var w = 0; w < wallCount; w++)
            {
                if (TryResolveOne(ref x, ref y, radius, walls![w]))
                {
                    movedAny = true;
                }
            }

            for (var o = 0; o < obstacleCount; o++)
            {
                if (TryResolveCircle(ref x, ref y, radius, obstacles![o], o))
                {
                    movedAny = true;
                }
            }

            // Early-out once a pass changed nothing — already clear of every wall + obstacle. (Pure optimisation; a
            // no-move pass leaves x/y untouched, so the deterministic result is unaffected.)
            if (!movedAny)
            {
                break;
            }
        }

        return (x, y);
    }

    // De-penetrate the moving circle (centre x,y, radius) out of ONE obstacle Circle: if the centres are closer than
    // (radius + obstacle.Radius), push the moving centre OUT along the centre→centre axis to exactly that sum distance.
    // This removes only the INTO-obstacle motion component, preserving the tangential component => the body SLIDES
    // around the obstacle (same slide property the wall face gives). `index` (the obstacle's stable position in the
    // caller's list) seeds a deterministic ejection axis for the degenerate exact-overlap case. Returns true if it moved
    // the centre.
    private static bool TryResolveCircle(ref double x, ref double y, double radius, Circle obstacle, int index)
    {
        var dx = x - obstacle.X;
        var dy = y - obstacle.Y;
        var sumRadius = radius + obstacle.Radius;
        var distSq = (dx * dx) + (dy * dy);

        if (distSq >= sumRadius * sumRadius)
        {
            return false; // not overlapping this obstacle
        }

        if (distSq > 1e-18)
        {
            // Overlapping but centres distinct: push out along the centre→centre normal to exactly sumRadius.
            var dist = Math.Sqrt(distSq);
            var push = sumRadius - dist;
            var inv = 1d / dist;
            x += dx * inv * push;
            y += dy * inv * push;
            return true;
        }

        // EXACT OVERLAP (centres coincident): there is no real centre→centre axis, so eject along a DETERMINISTIC unit
        // axis derived from the obstacle's list index (a golden-angle spread so multiple coincident obstacles fan the
        // body out in different directions instead of all along one line). Never a 0/0 NaN, reproducible every call.
        var angle = index * GoldenAngleRadians;
        x = obstacle.X + (Math.Cos(angle) * sumRadius);
        y = obstacle.Y + (Math.Sin(angle) * sumRadius);
        return true;
    }

    // The golden angle (~137.5°) in radians — an irrational turn so successive index-derived ejection axes never repeat
    // a direction, fanning coincident-overlap bodies evenly around the circle (the same spread MonsterSeparation uses).
    private const double GoldenAngleRadians = 2.39996322972865332d;

    // Push the circle center (x, y) out of every wall it penetrates, along each wall's minimum-penetration axis
    // (which preserves tangential motion => slide). Runs a FIXED number of passes so overlapping walls at an inside
    // corner settle. Deterministic: walls visited in list-index order, ties broken the same way every call.
    private static (double X, double Y) ResolvePenetration(double x, double y, double radius, IReadOnlyList<Wall> walls)
    {
        for (var pass = 0; pass < ResolvePasses; pass++)
        {
            var movedAny = false;
            for (var w = 0; w < walls.Count; w++)
            {
                if (TryResolveOne(ref x, ref y, radius, walls[w]))
                {
                    movedAny = true;
                }
            }

            // Early-out once a pass changed nothing — already clear. (Pure optimization; does not change the result,
            // since a no-move pass would leave x/y untouched anyway, so the byte-identical guarantee holds.)
            if (!movedAny)
            {
                break;
            }
        }

        return (x, y);
    }

    // Circle-vs-AABB resolution for ONE wall. The circle center must stay outside the AABB expanded by `radius`
    // (with rounded corners). Computes the closest point on the AABB to the center; if the center is nearer than
    // `radius`, pushes it out to exactly `radius` along the contact normal. Returns true if it moved the center.
    private static bool TryResolveOne(ref double x, ref double y, double radius, Wall wall)
    {
        // Closest point on the AABB to the circle center (clamp the center into the box).
        var closestX = Clamp(x, wall.MinX, wall.MaxX);
        var closestY = Clamp(y, wall.MinY, wall.MaxY);

        var dx = x - closestX;
        var dy = y - closestY;
        var distSq = (dx * dx) + (dy * dy);

        if (distSq > radius * radius)
        {
            return false; // not touching this wall
        }

        if (distSq > 1e-18)
        {
            // Center is OUTSIDE the box but within `radius` of it (a face or rounded corner). Push out along the
            // vector from the closest point to the center (the contact normal) to exactly `radius`. This removes
            // only the INTO-wall component, leaving tangential motion => slide along the face.
            var dist = Math.Sqrt(distSq);
            var push = radius - dist;
            var inv = 1d / dist;
            x += dx * inv * push;
            y += dy * inv * push;
            return true;
        }

        // Center is INSIDE the box (deep penetration / a tunneling near-miss the sub-step caught). There is no
        // outward normal from the closest-point method, so eject along the axis of MINIMUM penetration to the
        // nearest face, then out by the radius. Deterministic tie-break: X axis wins an exact tie.
        var penLeft = (x - wall.MinX) + radius;   // distance to exit out the -X face
        var penRight = (wall.MaxX - x) + radius;  // distance to exit out the +X face
        var penDown = (y - wall.MinY) + radius;   // distance to exit out the -Y face
        var penUp = (wall.MaxY - y) + radius;     // distance to exit out the +Y face

        var minPen = penLeft;
        var axis = 0; // 0:-X 1:+X 2:-Y 3:+Y
        if (penRight < minPen) { minPen = penRight; axis = 1; }
        if (penDown < minPen) { minPen = penDown; axis = 2; }
        if (penUp < minPen) { axis = 3; }

        switch (axis)
        {
            case 0: x = wall.MinX - radius; break;
            case 1: x = wall.MaxX + radius; break;
            case 2: y = wall.MinY - radius; break;
            default: y = wall.MaxY + radius; break;
        }

        return true;
    }

    private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
}
