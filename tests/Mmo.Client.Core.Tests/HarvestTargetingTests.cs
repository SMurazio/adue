using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

public sealed class HarvestTargetingTests
{
    [Fact]
    public void PicksInRangeAvailableResourceNode()
    {
        var entities = new[]
        {
            Resource(10, 6, 5, depleted: false),
        };

        var found = HarvestTargeting.TryFindNearestHarvestable(entities, Actor(5, 5), out var target);

        Assert.True(found);
        Assert.Equal(10u, target);
    }

    [Fact]
    public void IgnoresNodesBeyondInteractionRadius()
    {
        // (7,5) is 2 tiles away — outside the 1.5-tile interaction radius.
        var entities = new[]
        {
            Resource(10, 7, 5, depleted: false),
        };

        var found = HarvestTargeting.TryFindNearestHarvestable(entities, Actor(5, 5), out _);

        Assert.False(found);
    }

    // Phase 9: a diagonal node is sqrt(2) ≈ 1.414 tiles away — INSIDE the 1.5-tile Euclidean radius (it was inside
    // the old Chebyshev <= 1 box too, so reach is preserved). The tile-Chebyshev test couldn't distinguish a
    // diagonal from an orthogonal neighbour; the Euclidean radius now treats it as genuinely ~1.41 away.
    [Fact]
    public void PicksDiagonalNodeWithinRadius()
    {
        var entities = new[]
        {
            Resource(10, 6, 6, depleted: false), // sqrt(2) ≈ 1.414 < 1.5
        };

        Assert.True(HarvestTargeting.TryFindNearestHarvestable(entities, Actor(5, 5), out var target));
        Assert.Equal(10u, target);
    }

    // Phase 9 (sub-tile, the tile math couldn't express this): the actor stands OFF-GRID at (5.4, 5.0). A node at
    // tile (7,5) sits at world distance 1.6 — just OUTSIDE the 1.5 radius — so it is NOT harvestable, even though the
    // actor's ROUNDED tile (5,5) would be Chebyshev-2 away and the actor's CONTAINING tile (5,5) likewise. The
    // continuous gate is what the server applies (it reads actor.Position), so the client must agree.
    [Fact]
    public void SubTileActorOffsetPushesNodeOutOfRange()
    {
        var entities = new[]
        {
            Resource(10, 7, 5, depleted: false),
        };

        // 7 - 5.4 = 1.6 > 1.5 → out of range.
        Assert.False(HarvestTargeting.TryFindNearestHarvestable(entities, new WorldVector(5.4d, 5.0d), out _));
    }

    // Phase 9 (sub-tile): the mirror of the above — the actor leans TOWARD the node at (5.6, 5.0), bringing the
    // tile-(7,5) node to world distance 1.4 — now INSIDE the 1.5 radius — so it becomes harvestable. Tile math
    // (which rounds the actor to (6,5), a Chebyshev-1 neighbour) would also accept here, but only the continuous
    // distance is the actual server contract.
    [Fact]
    public void SubTileActorLeanBringsNodeInRange()
    {
        var entities = new[]
        {
            Resource(10, 7, 5, depleted: false),
        };

        // 7 - 5.6 = 1.4 < 1.5 → in range.
        Assert.True(HarvestTargeting.TryFindNearestHarvestable(entities, new WorldVector(5.6d, 5.0d), out var target));
        Assert.Equal(10u, target);
    }

    [Fact]
    public void IgnoresDepletedNodes()
    {
        var entities = new[]
        {
            Resource(10, 5, 5, depleted: true),
        };

        var found = HarvestTargeting.TryFindNearestHarvestable(entities, Actor(5, 5), out _);

        Assert.False(found);
    }

    [Fact]
    public void IgnoresNonResourceEntities()
    {
        var entities = new[]
        {
            new EntityRenderState(10, Guid.NewGuid(), EntityKind.Player, "P", default, new TileCoord(5, 5), Direction8.S, false),
        };

        var found = HarvestTargeting.TryFindNearestHarvestable(entities, Actor(5, 5), out _);

        Assert.False(found);
    }

    [Fact]
    public void PrefersNearerNodeThenLowerNetworkIdOnTies()
    {
        var entities = new[]
        {
            Resource(20, 6, 6, depleted: false), // diagonal: distance² 2
            Resource(10, 6, 5, depleted: false), // orthogonal: distance² 1 (nearer)
        };

        var found = HarvestTargeting.TryFindNearestHarvestable(entities, Actor(5, 5), out var target);

        Assert.True(found);
        Assert.Equal(10u, target);

        var tie = new[]
        {
            Resource(30, 4, 5, depleted: false), // distance² 1
            Resource(15, 6, 5, depleted: false), // distance² 1, lower id wins
        };

        Assert.True(HarvestTargeting.TryFindNearestHarvestable(tie, Actor(5, 5), out var tieTarget));
        Assert.Equal(15u, tieTarget);
    }

    // LOOT P4b: the interact/harvest key also targets an in-range corpse (loot it through the same path).
    [Fact]
    public void PicksInRangeCorpse()
    {
        var entities = new[]
        {
            Corpse(20, 6, 5), // in range
        };

        var found = HarvestTargeting.TryFindNearestHarvestable(entities, Actor(5, 5), out var target);

        Assert.True(found);
        Assert.Equal(20u, target);
    }

    [Fact]
    public void IgnoresOutOfRangeCorpse()
    {
        var entities = new[]
        {
            Corpse(20, 9, 5), // 4 tiles away
        };

        Assert.False(HarvestTargeting.TryFindNearestHarvestable(entities, Actor(5, 5), out _));
    }

    // Phase 9 client/server parity: the client's in-range verdict must equal the server's accept for the same
    // continuous actor + tile-placed target. We compute the server's gate directly off the SHARED constant (the
    // exact expression GameServer.IsInInteractionRange uses) and assert the client matches at radius boundaries.
    [Theory]
    [InlineData(5.0, 5.0, 6, 5, true)]   // dist 1.0 — in
    [InlineData(5.0, 5.0, 6, 6, true)]   // dist ~1.414 — in
    [InlineData(5.0, 5.0, 7, 5, false)]  // dist 2.0 — out
    [InlineData(5.4, 5.0, 7, 5, false)]  // dist 1.6 — out
    [InlineData(5.6, 5.0, 7, 5, true)]   // dist 1.4 — in
    public void ClientInRangeVerdictMatchesServerGate(double ax, double ay, int tx, int ty, bool expectedInRange)
    {
        var actor = new WorldVector(ax, ay);

        // The server's authoritative gate, computed from the SAME shared constant (resource/corpse Position is the
        // tile centre).
        var target = WorldVector.FromTile(new TileCoord(tx, ty));
        var serverAccepts = (actor - target).LengthSquared <= InteractionTuning.InteractionRadiusUnitsSquared;
        Assert.Equal(expectedInRange, serverAccepts);

        // The client's verdict via HarvestTargeting on a single candidate at that tile.
        var clientInRange = HarvestTargeting.TryFindNearestHarvestable(
            new[] { Resource(10, tx, ty, depleted: false) },
            actor,
            out _);

        Assert.Equal(serverAccepts, clientInRange);
    }

    private static WorldVector Actor(int x, int y) => WorldVector.FromTile(new TileCoord(x, y));

    private static EntityRenderState Resource(uint networkId, int x, int y, bool depleted)
    {
        return new EntityRenderState(
            networkId,
            Guid.Empty,
            EntityKind.Resource,
            "Tree",
            default,
            new TileCoord(x, y),
            Direction8.S,
            IsLocal: false,
            Depleted: depleted);
    }

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
