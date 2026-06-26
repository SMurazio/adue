using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// FREEAIM: the geometric free-aim sector resolution against a real WorldState (spatial occupancy + the radius/
// angle test + the friendly-fire gate + damage). The cursor dedup / cooldown gate are tested separately; these
// pin the "who is in the sector and who takes damage" half that GameServer.HandleAttack delegates here.
//
// World mapping: TileCoord (X,Y) -> world (X,0,Y); bearing is atan2(dz, dx) with +X east, +Z south. The attacker
// sits at (10,10). Aim EAST = 0 rad. Defaults mirror the server knobs: 45° half-angle (90° arc), 1.6-tile radius.
public sealed class FreeAimSectorResolverTests
{
    private const int Damage = 20;
    private const double HalfAngle = System.Math.PI / 4d; // 45°
    private const double Radius = 1.6d;
    private const double AimEast = 0d;

    private static WorldEntity Attacker(WorldState world)
        => world.AddPlayer(1, System.Guid.NewGuid(), "Attacker", new TileCoord(10, 10), new ClientSession(null!), new Inventory(ItemRegistry.Default));

    // Places an already-spawned entity at a FRACTIONAL continuous Position (off tile-centre). Goes through the
    // entity's own continuous mutator (ApplyResolvedMove), which writes Position directly; if the rounded tile
    // changes it migrates the spatial-grid bucket via WorldState.OnEntityMoved so the gather still finds it. This is
    // how the sub-tile / parity tests express positions the integer tile API could not.
    private static void PlaceAt(WorldState world, WorldEntity entity, double x, double y)
    {
        var previousTile = entity.TileCoord;
        entity.ApplyResolvedMove(new WorldVector(x, y));
        if (entity.TileCoord != previousTile)
        {
            world.OnEntityMoved(entity, previousTile);
        }
    }

    [Fact]
    public void HitsDummyInsideArcAndRadius()
    {
        var world = new WorldState();
        var attacker = Attacker(world);
        // Directly east, 1 tile out: bearing 0 (= aim), distance 1.0 (< 1.6). In-sector.
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(11, 10), Direction8.S);

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(1, hits);
        Assert.Equal(80, dummy.Stats.Health);
    }

    [Fact]
    public void MissesDummyOutsideArc()
    {
        var world = new WorldState();
        var attacker = Attacker(world);
        // Due SOUTH, 1 tile out: bearing +π/2 (90°) from the east aim — outside the ±45° arc. Within radius, but the
        // angular gate rejects it.
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(10, 11), Direction8.S);

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(0, hits);
        Assert.Equal(100, dummy.Stats.Health);
    }

    [Fact]
    public void MissesDummyBeyondRadius()
    {
        var world = new WorldState();
        var attacker = Attacker(world);
        // Due east, 3 tiles out: bearing 0 (on-aim) but distance 3.0 > radius 1.6 + body 0.5 = 2.1 — even the
        // target's body circle can't reach the sector, so it's a clean miss.
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(13, 10), Direction8.S);

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(0, hits);
        Assert.Equal(100, dummy.Stats.Health);
    }

    [Fact]
    public void HitsDiagonalDummyOnTheArcEdge()
    {
        var world = new WorldState();
        var attacker = Attacker(world);
        // NE (11,9): bearing atan2(-1,1) = -45° — exactly at the -halfAngle edge (<= passes); distance √2 ≈ 1.414 < 1.6.
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(11, 9), Direction8.S);

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(1, hits);
        Assert.Equal(80, dummy.Stats.Health);
    }

    [Fact]
    public void NoFriendlyFireAgainstOtherPlayers()
    {
        var world = new WorldState();
        var attacker = Attacker(world);
        // A Player squarely in the sector (1 tile east) must be untouched (no friendly fire).
        var ally = world.AddPlayer(2, System.Guid.NewGuid(), "Ally", new TileCoord(11, 10), new ClientSession(null!), new Inventory(ItemRegistry.Default));

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(0, hits);
        Assert.Equal(100, ally.Stats.Health);
    }

    [Fact]
    public void AttackerDoesNotDamageItself()
    {
        var world = new WorldState();
        // A Dummy attacker (an attackable kind) on its own tile must never hit itself, even at point-blank distance 0.
        var attacker = world.AddTransient(1, EntityKind.Dummy, "Self", new TileCoord(10, 10), Direction8.E);

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(0, hits);
        Assert.Equal(100, attacker.Stats.Health);
    }

    [Fact]
    public void HitsEveryEnemyInTheSector()
    {
        var world = new WorldState();
        var attacker = Attacker(world);
        // Three dummies all within the east 90° arc + 1.6 radius: E (11,10), NE (11,9), SE (11,11).
        var d1 = world.AddTransient(2, EntityKind.Dummy, "D1", new TileCoord(11, 10), Direction8.S);
        var d2 = world.AddTransient(3, EntityKind.Dummy, "D2", new TileCoord(11, 9), Direction8.S);
        var d3 = world.AddTransient(4, EntityKind.Dummy, "D3", new TileCoord(11, 11), Direction8.S);

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(3, hits);
        Assert.Equal(80, d1.Stats.Health);
        Assert.Equal(80, d2.Stats.Health);
        Assert.Equal(80, d3.Stats.Health);
    }

    [Fact]
    public void AimRotatesTheSectorContinuously()
    {
        var world = new WorldState();
        var attacker = Attacker(world);
        // The south dummy is OUT of an east-aimed sector but IN a south-aimed one — proving the aim (not a fixed
        // facing) rotates the arc. Aim south = +π/2.
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(10, 11), Direction8.S);

        var missEast = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);
        Assert.Equal(0, missEast);
        Assert.Equal(100, dummy.Stats.Health);

        var hitSouth = FreeAimSectorResolver.ResolveAndDamage(world, attacker, System.Math.PI / 2d, HalfAngle, Radius, Damage, []);
        Assert.Equal(1, hitSouth);
        Assert.Equal(80, dummy.Stats.Health);
    }

    [Fact]
    public void DamagedScratchCollectsEachVictimAndAmount()
    {
        // COMBAT-QOL: the overload appends each victim whose HP actually changed (entity + amount) so HandleAttack can
        // emit one cosmetic damage event per real hit. Two dummies in the sector, one Player outside it (no event).
        var world = new WorldState();
        var attacker = Attacker(world);
        var d1 = world.AddTransient(2, EntityKind.Dummy, "D1", new TileCoord(11, 10), Direction8.S);
        var d2 = world.AddTransient(3, EntityKind.Dummy, "D2", new TileCoord(11, 9), Direction8.S);

        var damaged = new System.Collections.Generic.List<FreeAimSectorResolver.DamagedVictim>();
        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, [], damaged);

        Assert.Equal(2, hits);
        Assert.Equal(2, damaged.Count);
        Assert.All(damaged, v => Assert.Equal(Damage, v.Amount));
        Assert.Contains(damaged, v => v.Victim.NetworkId == d1.NetworkId);
        Assert.Contains(damaged, v => v.Victim.NetworkId == d2.NetworkId);
    }

    [Fact]
    public void DamagedScratchIsClearedAndEmptyOnAMiss()
    {
        // A resolve that hits nothing must leave the damaged scratch empty (it is cleared up front), so HandleAttack
        // emits no spurious damage events. Pre-fill the list to prove it is cleared.
        var world = new WorldState();
        var attacker = Attacker(world);
        world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(10, 11), Direction8.S); // due south, outside east arc.

        var damaged = new System.Collections.Generic.List<FreeAimSectorResolver.DamagedVictim>
        {
            new(attacker, 999),
        };
        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, [], damaged);

        Assert.Equal(0, hits);
        Assert.Empty(damaged);
    }

    // FREEAIM-PREDICT: the SHARED FreeAimSector.IsHit (which the Godot client uses to PREDICT its own swing) must
    // reproduce the SERVER resolver's hit/miss for every existing scenario. We run the resolver against one dummy at
    // a given tile + aim, observe whether its HP changed (the authoritative hit), and assert the shared geometry
    // helper agrees for the SAME attacker pos (10,10), aim, tuning, body radius, and target pos. If these ever
    // diverge the client would predict a number the server never deals (or miss one it does) — exactly what this pins.
    [Theory]
    [InlineData(11, 10, AimEast, true)]              // due east, in arc + radius.
    [InlineData(10, 11, AimEast, false)]             // due south, outside east arc.
    [InlineData(13, 10, AimEast, false)]             // due east but beyond radius+body.
    [InlineData(11, 9, AimEast, true)]               // NE, on the -45° arc edge.
    [InlineData(11, 11, AimEast, true)]              // SE, on the +45° arc edge.
    [InlineData(10, 11, System.Math.PI / 2d, true)]  // due south, hit by a south-aimed sector.
    [InlineData(10, 11, 0d, false)]                  // due south, missed by the east-aimed sector (aim rotates arc).
    public void SharedHelperReproducesResolverHitMiss(int targetX, int targetY, double aim, bool expectedHit)
    {
        var world = new WorldState();
        var attacker = Attacker(world);
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(targetX, targetY), Direction8.S);

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, aim, HalfAngle, Radius, Damage, []);
        var resolverHit = hits == 1 && dummy.Stats.Health < 100;

        var sharedHit = FreeAimSector.IsHit(
            10d, 10d, aim, HalfAngle, Radius, FreeAimSectorResolver.EntityHitRadiusTiles, targetX, targetY);

        // Both must agree with each other AND with the documented expectation.
        Assert.Equal(expectedHit, resolverHit);
        Assert.Equal(resolverHit, sharedHit);
    }

    [Fact]
    public void SharedHelperPointBlankAlwaysHitsRegardlessOfAim()
    {
        // A target overlapping the attacker (dist <= body radius) is always in-sector, even aimed the opposite way —
        // the resolver's point-blank always-hit, captured by the shared helper. Target half a body radius off-centre,
        // aimed due WEST while the target is to the east.
        var pointBlank = FreeAimSector.IsHit(
            10d, 10d, System.Math.PI, HalfAngle, Radius, FreeAimSectorResolver.EntityHitRadiusTiles, 10.25d, 10d);
        Assert.True(pointBlank);
    }

    // ---- Phase 7: sub-tile / continuous-position cases the integer tile API could not express ----

    [Fact]
    public void HitsTargetOneTenthInsideTheRadius()
    {
        // Due east, just INSIDE the range gate: the body circle reaches radius + body = 1.6 + 0.5 = 2.1, so a target
        // centre at 2.0 (0.1 inside) is a hit. Tile rounding could never place a target at x=12.0 distinct from 12.0,
        // but it could never place the SUB-tile boundary either — this pins the fractional in-range edge.
        var world = new WorldState();
        var attacker = Attacker(world);
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(12, 10), Direction8.S);
        PlaceAt(world, dummy, 12.0d, 10d); // distance 2.0 < reach 2.1

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(1, hits);
        Assert.Equal(80, dummy.Stats.Health);
    }

    [Fact]
    public void MissesTargetOneTenthOutsideTheRadius()
    {
        // The mirror of the above: a target centre at 2.2 (0.1 OUTSIDE reach 2.1) is a clean miss. The hit/miss pair
        // brackets the continuous range gate the tile math straddled in a single tile step.
        var world = new WorldState();
        var attacker = Attacker(world);
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(12, 10), Direction8.S);
        PlaceAt(world, dummy, 12.2d, 10d); // distance 2.2 > reach 2.1

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(0, hits);
        Assert.Equal(100, dummy.Stats.Health);
    }

    [Fact]
    public void AttackerAtAFractionalPositionResolvesAgainstThatPositionNotTheRoundedTile()
    {
        // THE REGRESSION GUARD: this verdict FLIPS between the pre-Stage-A (rounded) and post-Stage-A (continuous)
        // resolver. Attacker at (10.0, 10.45) aiming SOUTH (+π/2), target due south at (10.0, 12.5): the TRUE distance
        // is 2.05 < reach 2.1, so the continuous resolver (and the client's FreeAimSector.IsHit, below) HIT. The OLD
        // resolver rounded the attacker to tile (10,10) and the target to (10,13) → distance 3.0 > 2.1 → it would have
        // MISSED a hit the client predicted (the "hit on client, miss on server" bug). Post-Stage-A both agree on HIT.
        const double aimSouth = System.Math.PI / 2d;
        var world = new WorldState();
        var attacker = Attacker(world);
        PlaceAt(world, attacker, 10.0d, 10.45d);
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(10, 13), Direction8.S);
        PlaceAt(world, dummy, 10.0d, 12.5d); // distance from 10.45 = 2.05 < reach 2.1 (rounded → 3.0 > 2.1, a miss)

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, aimSouth, HalfAngle, Radius, Damage, []);

        Assert.Equal(1, hits);
        Assert.Equal(80, dummy.Stats.Health);

        // And the SHARED helper (the client's exact call) agrees against the same continuous positions.
        var sharedHit = FreeAimSector.IsHit(
            10.0d, 10.45d, aimSouth, HalfAngle, Radius, FreeAimSectorResolver.EntityHitRadiusTiles, 10.0d, 12.5d);
        Assert.True(sharedHit);
    }

    [Fact]
    public void AngularEdgeAtAFractionalBearingHitsAndMisses()
    {
        // A fractional bearing the tile lattice could not express: a target offset (1.0, dy) east. The aim is EAST (0);
        // a target at dy just inside the ±45° wedge (plus the body's angular widen) hits, one further out misses. At
        // dx=1.0 the body half-width is asin(0.5/dist); the bracket below straddles the widened edge.
        var world = new WorldState();
        var attacker = Attacker(world);

        var inside = world.AddTransient(2, EntityKind.Dummy, "In", new TileCoord(11, 11), Direction8.S);
        PlaceAt(world, inside, 11.0d, 10.9d); // dist≈1.345, bearing≈42°, widen≈21.8° → edge≈66.8° → hit
        var outside = world.AddTransient(3, EntityKind.Dummy, "Out", new TileCoord(11, 12), Direction8.S);
        PlaceAt(world, outside, 10.6d, 11.6d); // dist≈1.709, bearing≈69.4°, widen≈17° → edge≈62° → miss

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(1, hits);
        Assert.Equal(80, inside.Stats.Health);
        Assert.Equal(100, outside.Stats.Health);
    }

    [Theory]
    [InlineData(Direction8.N)]
    [InlineData(Direction8.NE)]
    [InlineData(Direction8.E)]
    [InlineData(Direction8.SE)]
    [InlineData(Direction8.S)]
    [InlineData(Direction8.SW)]
    [InlineData(Direction8.W)]
    [InlineData(Direction8.NW)]
    public void HitSetIsIndependentOfAttackerFacing(Direction8 facing)
    {
        // The resolver reads the continuous AIM, never the Direction8 facing (which is animation/fallback only). The
        // SAME positions + aim must produce the SAME hit set regardless of how the attacker is facing — proving no
        // tile-direction leaked into the hit test. We fix attacker (10.3,10.1) + a dummy in the east sector, vary the
        // facing across all eight, and assert an identical 1-hit verdict every time.
        var world = new WorldState();
        var attacker = Attacker(world);
        PlaceAt(world, attacker, 10.3d, 10.1d);
        attacker.TrySetFacing(facing);
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(11, 10), Direction8.S);
        PlaceAt(world, dummy, 11.3d, 10.1d); // 1.0 east of the attacker, on-aim

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(1, hits);
        Assert.Equal(80, dummy.Stats.Health);
    }

    // FREEAIM-PARITY (the crux): over a SUB-TILE grid of attacker/target positions + aims, the shared geometry
    // FreeAimSector.IsHit(attacker.Position, …, target.Position) — the client's EXACT predict call — must equal the
    // server's authoritative ResolveAndDamage verdict with entities placed at those same continuous positions. The
    // pre-existing parity theory only sampled tile centres (where rounding is a no-op); this generalizes it to the
    // FRACTIONAL positions where the old server rounding diverged from the client. If these ever disagree the client
    // predicts a number the server never deals (or misses one it does) — the "hit on client, miss on server" bug.
    [Theory]
    [InlineData(10.0, 10.0, 11.0, 10.0, AimEast)]      // tile-centre baseline (rounding a no-op).
    [InlineData(10.4, 10.0, 12.0, 10.0, AimEast)]      // fractional attacker, in range (dist 1.6 < reach 2.1) → hit.
    [InlineData(9.85, 10.0, 12.0, 10.0, AimEast)]      // fractional attacker pulled back (dist 2.15 > reach 2.1) → miss.
    [InlineData(10.0, 10.0, 11.0, 10.9, AimEast)]      // fractional target near the angular edge.
    [InlineData(10.0, 10.0, 10.6, 11.6, AimEast)]      // fractional target past the widened angular edge.
    [InlineData(10.25, 10.25, 11.4, 10.3, 0.4)]        // both fractional, off-axis aim.
    [InlineData(10.0, 10.0, 10.0, 12.0, 1.5707963)]    // due south aim (π/2), in range.
    [InlineData(10.7, 10.2, 9.1, 10.4, 3.1415926)]     // west aim, both fractional behind/around.
    public void SharedHelperReproducesResolverAtFractionalPositions(
        double ax, double ay, double tx, double ty, double aim)
    {
        var world = new WorldState();
        var attacker = Attacker(world);
        PlaceAt(world, attacker, ax, ay);
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(11, 10), Direction8.S);
        PlaceAt(world, dummy, tx, ty);

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, aim, HalfAngle, Radius, Damage, []);
        var resolverHit = hits == 1 && dummy.Stats.Health < 100;

        var sharedHit = FreeAimSector.IsHit(
            ax, ay, aim, HalfAngle, Radius, FreeAimSectorResolver.EntityHitRadiusTiles, tx, ty);

        Assert.Equal(resolverHit, sharedHit);
    }

    [Fact]
    public void GatherIsASupersetForABodyClippingTargetAtTheBoxEdge()
    {
        // The gather box (keyed on the attacker's ROUNDED tile) must never drop a real hit. The minimal case that
        // would defeat the OLD ceil(radius)=2 box: a body-clipping target whose ROUNDED tile is >2 tiles from the
        // attacker's rounded tile, reachable only because the attacker's own sub-tile offset extends the reach.
        // Attacker (10.45,10) → tile 10; target continuous (12.5,10) → rounded tile 13 (3 tiles out, OUTSIDE the old
        // box). True distance 2.05 is between radius 1.6 and reach 2.1, so the body clips the sector → a real hit. The
        // widened gather (radius ceil(1.6+0.5)+1 = 4, x∈[6,14]) keeps tile 13; the HIT landing proves it was gathered.
        var world = new WorldState();
        var attacker = Attacker(world);
        PlaceAt(world, attacker, 10.45d, 10.0d);
        var dummy = world.AddTransient(2, EntityKind.Dummy, "Dummy", new TileCoord(13, 10), Direction8.S);
        PlaceAt(world, dummy, 12.5d, 10.0d); // rounds to tile 13; true dist from 10.45 = 2.05 (radius 1.6 < 2.05 < 2.1)

        Assert.Equal(new TileCoord(13, 10), dummy.TileCoord); // confirms the rounded tile is the box-edge case.

        var hits = FreeAimSectorResolver.ResolveAndDamage(world, attacker, AimEast, HalfAngle, Radius, Damage, []);

        Assert.Equal(1, hits);
        Assert.Equal(80, dummy.Stats.Health);

        // The shared helper agrees this is a hit at these continuous positions (so the gather, not the geometry, was
        // the only thing that could have dropped it).
        Assert.True(FreeAimSector.IsHit(
            10.45d, 10.0d, AimEast, HalfAngle, Radius, FreeAimSectorResolver.EntityHitRadiusTiles, 12.5d, 10.0d));
    }
}
