using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// MONSTER-SEPARATION (todo/N-monster-monster-collision-separation.md): the server-authoritative monster↔monster
// SEPARATION pass — a pure position DE-PENETRATION step that pushes overlapping monster bodies apart so they stop
// compenetrating. There is NO physics here: it changes POSITION only, never Velocity (no momentum/bounce). It is the
// peer of the WALL collision the locomotions already do — entities collide with walls (ContinuousCollision vs the
// TileGrid) but, until this pass, never with each other, so monsters stacked.
//
// DESIGN — ACCUMULATE-THEN-APPLY (order-independent + stable):
//   1. For each participant, query the nearby participants via the spatial grid (a SUPERSET; the exact distance test
//      is applied per candidate). For every overlapping ORDERED-UNIQUE pair (a.Id < b.Id, centre distance
//      d < 2×radius) accumulate a HALF-penetration push on EACH along the unit centre→centre axis (a away from b,
//      b away from a). Pushes are summed into a scratch map FIRST — nothing moves mid-iteration — so the result is
//      independent of the iteration order (the pairwise correction does not depend on who was processed earlier).
//   2. After accumulation, each participant with a non-zero displacement is moved ONCE: the displacement is CAPPED to
//      ≤ radius (no explosions from a deep multi-body pile-up), WALL-CLAMPED through the SAME shared resolver
//      (QueryNearbyWalls + ContinuousCollision.Resolve) ordinary movement uses (so a push never shoves a body through
//      a wall), and applied via the injected apply-landing seam (Zone.ApplyMonsterLanding — migrates the spatial-grid
//      bucket on a tile cross, exactly like a hop/glide landing).
//   3. The whole pass runs a small fixed number of RELAXATION ITERATIONS per tick (re-querying each iteration), so a
//      tight cluster settles over a couple of passes rather than needing many ticks.
//
// REPLICATION (netcode care): a nudge on a MOVING monster (Velocity≠0) is force-included every tick already; a nudge
// on an IDLE one (Velocity 0) that does NOT cross a tile would be delta'd OUT (its tile-keyed StateRevision never
// bumps — ApplyResolvedMove only bumps on a rounded-tile crossing). So when an applied nudge moved a body WITHOUT
// crossing a tile, we bump StateRevision (WorldEntity.MarkRepositioned) to re-include the corrected position next
// snapshot — the SAME stop-edge / SnapToGround re-publish mechanism. Velocity is NEVER touched.
//
// DETERMINISM: no RNG, no clock — accumulation is summed and the apply reads the pre-pass positions through the
// scratch map, so the pass is reproducible (the testability contract). Exact overlap (d≈0) has no real centre→centre
// axis, so a deterministic unit axis is synthesised from the entity ids (never NaN, never a zero divide).
//
// PARTICIPATION: MONSTER↔MONSTER only today (players/dummies/resources/corpses do NOT participate). The participant
// filter is the single seam to widen later — see the PLAYER SEAM notes. The outer participant list is gathered by
// the caller (GameServer) with the matching kind filter, so a pair is only ever formed between two participants.
//
// Mirrors the HopLocomotion / GlideLocomotion shape: a standalone, dependency-injected primitive (body-radius
// provider, neighbour query, wall query, apply-landing seam) so it is unit-testable against a bare TileGrid +
// WorldState with no live Zone/GameServer, while GameServer wires it to the real seams.
public sealed class MonsterSeparation
{
    // RELAXATION passes per tick. 1 fully resolves an isolated PAIR (half + half = full de-penetration in one shot);
    // a couple of passes let a tight CLUSTER settle in a single tick instead of crawling apart over many ticks. Kept
    // small (2) — more passes cost more and the per-tick CAP already bounds how far a body can travel anyway, so the
    // cluster converges over a few ticks regardless. FIXED const (the work, and thus the result, is data-independent).
    private const int RelaxationIterations = 2;

    // The spatial-neighbourhood query radius, in TILES. Two bodies overlap only when their centres are within
    // 2×radius = 1.0 tile; a 2-tile box is a comfortable superset (covers the overlap distance plus margin) so the
    // exact per-candidate distance test never misses an overlapping neighbour. A pure perf bound — correctness is
    // independent of it (the grid returns a superset; the distance test decides).
    private const int NeighborQueryRadiusTiles = 2;

    // Below this a displacement / move is treated as zero — guards the divide in the unit-axis derivation and the
    // "did it actually move?" re-publish test against float dust.
    private const double Epsilon = 1e-6;

    private readonly Func<double> _bodyRadiusUnits;
    private readonly Action<TileCoord, int, List<WorldEntity>> _queryNeighbors;
    private readonly Action<WorldVector, WorldVector, double, List<ContinuousCollision.Wall>> _queryWalls;
    private readonly Func<WorldEntity, WorldVector, bool> _applyLanding;

    // Reused scratch — NO per-tick allocation in the hot loop (the pass runs every tick). The neighbour buffer is
    // refilled per participant (the query clears it); the wall buffer per applied move; the displacement map per
    // iteration. Single-threaded tick loop, so one shared set of buffers is safe.
    private readonly List<WorldEntity> _neighborScratch = new();
    private readonly List<ContinuousCollision.Wall> _wallScratch = new();
    private readonly Dictionary<ulong, WorldVector> _displacement = new();

    public MonsterSeparation(
        Func<double> bodyRadiusUnits,
        Action<TileCoord, int, List<WorldEntity>> queryNeighbors,
        Action<WorldVector, WorldVector, double, List<ContinuousCollision.Wall>> queryWalls,
        Func<WorldEntity, WorldVector, bool> applyLanding)
    {
        _bodyRadiusUnits = bodyRadiusUnits ?? throw new ArgumentNullException(nameof(bodyRadiusUnits));
        _queryNeighbors = queryNeighbors ?? throw new ArgumentNullException(nameof(queryNeighbors));
        _queryWalls = queryWalls ?? throw new ArgumentNullException(nameof(queryWalls));
        _applyLanding = applyLanding ?? throw new ArgumentNullException(nameof(applyLanding));
    }

    // Run the separation pass over `participants` (the caller's monster-only list — see the PLAYER SEAM). De-penetrates
    // overlapping pairs in place. No-op for <2 participants or a zero/non-finite radius.
    public void Separate(IReadOnlyList<WorldEntity> participants)
    {
        if (participants.Count < 2)
        {
            return;
        }

        var radius = _bodyRadiusUnits();
        if (!double.IsFinite(radius) || radius <= 0d)
        {
            return;
        }

        var minDist = 2d * radius;
        var minDistSq = minDist * minDist;

        for (var iteration = 0; iteration < RelaxationIterations; iteration++)
        {
            Accumulate(participants, minDist, minDistSq);
            if (_displacement.Count == 0)
            {
                // Nothing overlapped this pass — already separated, so further iterations are pure no-ops.
                break;
            }

            Apply(participants, radius);
        }
    }

    // PASS 1 — accumulate the half-penetration pushes for every overlapping ordered-unique pair into the scratch map,
    // reading the CURRENT positions and moving nothing (so the accumulation is order-independent).
    private void Accumulate(IReadOnlyList<WorldEntity> participants, double minDist, double minDistSq)
    {
        _displacement.Clear();

        for (var i = 0; i < participants.Count; i++)
        {
            var a = participants[i];
            _queryNeighbors(a.TileCoord, NeighborQueryRadiusTiles, _neighborScratch);

            for (var n = 0; n < _neighborScratch.Count; n++)
            {
                var b = _neighborScratch[n];

                // PLAYER SEAM: only MONSTERS participate today. To let players (or any other kind) collide later, widen
                // this candidate filter AND the caller's participant gather (GameServer.SeparateMonsters) to match — the
                // two MUST agree so a pair is only ever formed between two participants.
                if (b.Kind != EntityKind.Monster)
                {
                    continue;
                }

                // ORDERED-UNIQUE pair: process each pair once (when iterating its lower id), so the half+half push is
                // applied a single time. Also skips self (a.Id == b.Id).
                if (a.Id >= b.Id)
                {
                    continue;
                }

                var centreToCentre = a.Position - b.Position; // points from b toward a
                var distSq = centreToCentre.LengthSquared;
                if (distSq >= minDistSq)
                {
                    continue; // not overlapping
                }

                var dist = Math.Sqrt(distSq);
                var penetration = minDist - dist; // > 0 (they overlap)

                // Unit axis a←b. Exact overlap (d≈0) has no real direction → synthesise a deterministic one from the
                // ids so the pair still splits (never a 0/0 NaN).
                var axis = dist > Epsilon
                    ? centreToCentre * (1d / dist)
                    : DeterministicAxis(a.Id, b.Id);

                // Half each → the pair de-penetrates fully (equal-"mass" split): a moves +half, b moves -half.
                var half = axis * (penetration * 0.5d);
                AddDisplacement(a.Id, half);
                AddDisplacement(b.Id, half * -1d);
            }
        }
    }

    // PASS 2 — apply each accumulated displacement ONCE: cap, wall-clamp, land, and re-publish an idle no-tile-cross
    // nudge. Iterates the participant list (stable order) and looks the displacement up by id.
    private void Apply(IReadOnlyList<WorldEntity> participants, double radius)
    {
        for (var i = 0; i < participants.Count; i++)
        {
            var entity = participants[i];
            if (!_displacement.TryGetValue(entity.Id, out var displacement))
            {
                continue;
            }

            var lengthSq = displacement.LengthSquared;
            if (lengthSq <= Epsilon * Epsilon)
            {
                continue;
            }

            // CAP the per-tick move to ≤ radius so a deep pile-up cannot fling a body a huge distance in one tick (no
            // explosions); the relaxation across ticks still converges, just bounded.
            if (lengthSq > radius * radius)
            {
                displacement *= radius / Math.Sqrt(lengthSq);
            }

            var start = entity.Position;
            // WALL-CLAMP the push through the SAME shared swept-circle resolver ordinary movement uses, so a separation
            // nudge can never shove a body into / through a wall.
            _queryWalls(start, displacement, radius, _wallScratch);
            var resolved = ContinuousCollision.Resolve(start, displacement, radius, _wallScratch);

            // Apply via the shared landing seam (migrates the spatial-grid bucket on a tile cross). It bumps
            // StateRevision iff the rounded tile crossed (ApplyResolvedMove, R1).
            var crossedTile = _applyLanding(entity, resolved);

            // REPLICATION re-include: if the body actually moved but did NOT cross a tile, its tile-keyed StateRevision
            // did not bump — an IDLE monster (Velocity 0) would then be delta'd out and the corrected position would
            // never replicate. Bump it explicitly (the stop-edge / SnapToGround mechanism). A MOVING monster is
            // force-included regardless, but the bump is harmless and keeps the rule uniform. Velocity is NEVER touched.
            if (!crossedTile && (entity.Position - start).LengthSquared > Epsilon * Epsilon)
            {
                entity.MarkRepositioned();
            }
        }
    }

    private void AddDisplacement(ulong id, WorldVector delta)
    {
        _displacement.TryGetValue(id, out var current); // default → WorldVector.Zero
        _displacement[id] = current + delta;
    }

    // Exact-overlap (d≈0) deterministic unit axis: there is no centre→centre direction, so synthesise a reproducible
    // one from the (ordered) ids. A golden-ratio hash spreads stacked bodies around the circle so a tight pile fans
    // out in different directions instead of all splitting along one line. Pure + deterministic (no RNG/clock) — the
    // testability contract — and always a unit vector (never NaN/zero).
    private static WorldVector DeterministicAxis(ulong lowId, ulong highId)
    {
        unchecked
        {
            var hash = (lowId * 0x9E3779B97F4A7C15UL) ^ ((highId + 0x7F4A7C15UL) * 0xBF58476D1CE4E5B9UL);
            var angle = (hash % 3600000UL) / 3600000d * (2d * Math.PI);
            return new WorldVector(Math.Cos(angle), Math.Sin(angle));
        }
    }
}
