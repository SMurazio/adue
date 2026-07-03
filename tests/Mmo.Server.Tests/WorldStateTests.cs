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

        var inventory = new Inventory(ItemRegistry.Default);
        var entity = state.AddPlayer(12, characterId, "Player", new TileCoord(4, 5), session, inventory);

        Assert.Equal(12u, entity.NetworkId);
        Assert.Equal(EntityKind.Player, entity.Kind);
        Assert.True(entity.IsDurable);
        Assert.Equal(characterId, entity.CharacterId);
        Assert.Same(session, entity.OwnerSession);
        Assert.Same(inventory, entity.Inventory);
        Assert.True(state.TryGet(entity.Id, out var found));
        Assert.Same(entity, found);
    }

    [Fact]
    public void RemoveDeletesEntityFromTable()
    {
        var state = new WorldState();
        var entity = state.AddPlayer(12, Guid.NewGuid(), "Player", new TileCoord(4, 5), new ClientSession(null!), new Inventory(ItemRegistry.Default));

        Assert.True(state.Remove(entity.Id, out var removed));
        Assert.Same(entity, removed);
        Assert.False(state.TryGet(entity.Id, out _));
    }

    [Fact]
    public void CopyEntitiesToReusesCallerOwnedBuffer()
    {
        var state = new WorldState();
        var first = state.AddPlayer(12, Guid.NewGuid(), "First", new TileCoord(4, 5), new ClientSession(null!), new Inventory(ItemRegistry.Default));
        var second = state.AddPlayer(13, Guid.NewGuid(), "Second", new TileCoord(5, 5), new ClientSession(null!), new Inventory(ItemRegistry.Default));
        var buffer = new List<WorldEntity> { first };

        buffer.Clear();
        state.CopyEntitiesTo(buffer);

        Assert.Equal([first, second], buffer);
    }

    [Fact]
    public void AddTransientCreatesNonDurableEntityWithoutSessionOrCharacter()
    {
        var state = new WorldState();

        var entity = state.AddTransient(44, EntityKind.Resource, "Marker", new TileCoord(10, 8), Direction8.S);

        Assert.Equal(EntityKind.Resource, entity.Kind);
        Assert.False(entity.IsDurable);
        Assert.Null(entity.CharacterId);
        Assert.Null(entity.OwnerSession);
    }

    // MONSTER-AI-DORMANCY (todo/monster-ai-dormancy.md, ecology-v1-design.md §8 E0): the monster-only index
    // (WorldState.Monsters / CopyMonstersTo) must contain EXACTLY the live monsters — no players/resources/corpses,
    // and no stale entries after a monster is removed — since GameServer.StepMonsterAi now iterates it directly
    // instead of scanning + filtering every entity in the zone.
    [Fact]
    public void MonstersIndexContainsOnlyLiveMonsters_NotOtherKinds()
    {
        var state = new WorldState();
        var player = state.AddPlayer(1, Guid.NewGuid(), "Hero", new TileCoord(1, 1), new ClientSession(null!), new Inventory(ItemRegistry.Default));
        var monster = state.AddTransient(2, EntityKind.Monster, "Slime", new TileCoord(2, 2), Direction8.S);
        var resource = state.AddTransient(3, EntityKind.Resource, "Rock", new TileCoord(3, 3), Direction8.S);

        Assert.Equal([monster], state.Monsters);

        var buffer = new List<WorldEntity>();
        state.CopyMonstersTo(buffer);
        Assert.Equal([monster], buffer);

        // Sanity: the other kinds are still in the general index, just not the monster-only one.
        Assert.Contains(player, state.Entities);
        Assert.Contains(resource, state.Entities);
    }

    [Fact]
    public void MonstersIndexDropsAnEntityOnRemove_NoStaleEntry()
    {
        var state = new WorldState();
        var monster = state.AddTransient(2, EntityKind.Monster, "Slime", new TileCoord(2, 2), Direction8.S);
        Assert.Equal([monster], state.Monsters);

        Assert.True(state.Remove(monster.Id, out _));

        Assert.Empty(state.Monsters);
        var buffer = new List<WorldEntity>();
        state.CopyMonstersTo(buffer);
        Assert.Empty(buffer);
    }
}
