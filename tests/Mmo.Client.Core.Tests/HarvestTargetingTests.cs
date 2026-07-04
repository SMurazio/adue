using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// NODE-FIELD N3 (docs/node-field-design.md D5/D6): HarvestTargeting is now CORPSE-ONLY — harvestable resource
// nodes are no longer WorldEntities, so the old Resource-kind coverage this file used to pin moved to
// NodeFieldTargetingTests (catalogue-indexed, see that file). These tests are the same shape as before,
// just re-pointed at corpses only.
public sealed class HarvestTargetingTests
{
    [Fact]
    public void PicksInRangeCorpse()
    {
        var entities = new[]
        {
            Corpse(20, 6, 5),
        };

        var found = HarvestTargeting.TryFindNearestCorpse(entities, Actor(5, 5), out var target, out _);

        Assert.True(found);
        Assert.Equal(20u, target);
    }

    [Fact]
    public void IgnoresOutOfRangeCorpse()
    {
        // (9,5) is 4 tiles away — outside the 1.5-tile interaction radius.
        var entities = new[]
        {
            Corpse(20, 9, 5),
        };

        Assert.False(HarvestTargeting.TryFindNearestCorpse(entities, Actor(5, 5), out _, out _));
    }

    // Phase 9: a diagonal corpse is sqrt(2) ~= 1.414 tiles away — INSIDE the 1.5-tile Euclidean radius.
    [Fact]
    public void PicksDiagonalCorpseWithinRadius()
    {
        var entities = new[]
        {
            Corpse(20, 6, 6),
        };

        Assert.True(HarvestTargeting.TryFindNearestCorpse(entities, Actor(5, 5), out var target, out _));
        Assert.Equal(20u, target);
    }

    [Fact]
    public void IgnoresNonCorpseEntities()
    {
        var entities = new[]
        {
            new EntityRenderState(10, Guid.NewGuid(), EntityKind.Player, "P", default, new TileCoord(5, 5), Direction8.S, false),
        };

        var found = HarvestTargeting.TryFindNearestCorpse(entities, Actor(5, 5), out _, out _);

        Assert.False(found);
    }

    [Fact]
    public void PrefersNearerCorpseThenLowerNetworkIdOnTies()
    {
        var entities = new[]
        {
            Corpse(20, 6, 6), // diagonal: distance^2 2
            Corpse(10, 6, 5), // orthogonal: distance^2 1 (nearer)
        };

        var found = HarvestTargeting.TryFindNearestCorpse(entities, Actor(5, 5), out var target, out _);

        Assert.True(found);
        Assert.Equal(10u, target);

        var tie = new[]
        {
            Corpse(30, 4, 5), // distance^2 1
            Corpse(15, 6, 5), // distance^2 1, lower id wins
        };

        Assert.True(HarvestTargeting.TryFindNearestCorpse(tie, Actor(5, 5), out var tieTarget, out _));
        Assert.Equal(15u, tieTarget);
    }

    // Phase 9 client/server parity: the client's in-range verdict must equal the server's accept for the same
    // continuous actor + tile-placed target.
    [Theory]
    [InlineData(5.0, 5.0, 6, 5, true)]   // dist 1.0 — in
    [InlineData(5.0, 5.0, 6, 6, true)]   // dist ~1.414 — in
    [InlineData(5.0, 5.0, 7, 5, false)]  // dist 2.0 — out
    [InlineData(5.4, 5.0, 7, 5, false)]  // dist 1.6 — out
    [InlineData(5.6, 5.0, 7, 5, true)]   // dist 1.4 — in
    public void ClientInRangeVerdictMatchesServerGate(double ax, double ay, int tx, int ty, bool expectedInRange)
    {
        var actor = new WorldVector(ax, ay);

        var target = WorldVector.FromTile(new TileCoord(tx, ty));
        var serverAccepts = (actor - target).LengthSquared <= InteractionTuning.InteractionRadiusUnitsSquared;
        Assert.Equal(expectedInRange, serverAccepts);

        var clientInRange = HarvestTargeting.TryFindNearestCorpse(
            new[] { Corpse(10, tx, ty) },
            actor,
            out _,
            out _);

        Assert.Equal(serverAccepts, clientInRange);
    }

    [Fact]
    public void ReportsDistanceSquaredOfThePickedCorpse()
    {
        var entities = new[]
        {
            Corpse(10, 6, 5), // distance^2 1
        };

        Assert.True(HarvestTargeting.TryFindNearestCorpse(entities, Actor(5, 5), out _, out var distanceSquared));
        Assert.Equal(1.0, distanceSquared);
    }

    private static WorldVector Actor(int x, int y) => WorldVector.FromTile(new TileCoord(x, y));

    private static EntityRenderState Corpse(uint networkId, int x, int y)
    {
        return new EntityRenderState(
            networkId,
            Guid.Empty,
            EntityKind.Corpse,
            "Corpse",
            default,
            new TileCoord(x, y),
            Direction8.S,
            IsLocal: false);
    }
}
