using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

public sealed class WorldStateTests
{
    [Fact]
    public void AddPlayerCreatesDurablePlayerEntityLinkedToSession()
    {
        var session = new ClientSession(null!);
        var state = new WorldState();
        var characterId = Guid.NewGuid();

        var entity = state.AddPlayer(12, characterId, "Player", new TileCoord(4, 5), session);

        Assert.Equal(12u, entity.NetworkId);
        Assert.Equal(EntityKind.Player, entity.Kind);
        Assert.True(entity.IsDurable);
        Assert.Equal(characterId, entity.CharacterId);
        Assert.Same(session, entity.OwnerSession);
        Assert.True(state.TryGet(entity.Id, out var found));
        Assert.Same(entity, found);
    }

    [Fact]
    public void RemoveDeletesEntityFromTable()
    {
        var state = new WorldState();
        var entity = state.AddPlayer(12, Guid.NewGuid(), "Player", new TileCoord(4, 5), new ClientSession(null!));

        Assert.True(state.Remove(entity.Id, out var removed));
        Assert.Same(entity, removed);
        Assert.False(state.TryGet(entity.Id, out _));
    }
}
