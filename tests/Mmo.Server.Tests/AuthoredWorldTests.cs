using Mmo.Server.Configuration;
using Mmo.Server.Data;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Population;
using Xunit;

namespace Mmo.Server.Tests;

// AUTHORED-MAP M3: the server-side wiring for the authored town+floor-1 world — authored spawn
// anchors (D4), grass-only resource scatter (D6), and boot-time prop spawning from the map's markers.
public sealed class AuthoredWorldTests
{
    private static readonly AuthoredMap Map = AuthoredMap.Parse(AuthoredMaps.TownAndFloor1);

    private static Zone CreateAuthoredZone(SpawnDistribution spawnDistribution = SpawnDistribution.Authored)
    {
        return Zone.CreateGenerated(
            AuthoredMaps.TownAndFloor1Width,
            AuthoredMaps.TownAndFloor1Height,
            seed: 0,
            TerrainGenerator.AuthoredGenVersion,
            spawnDistribution);
    }

    [Fact]
    public void AuthoredDistributionUsesTheMapsSpawnAnchors()
    {
        // D4: the zone's spawn tiles ARE the map's `S` anchors (all six, in row-major order)...
        var zone = CreateAuthoredZone();
        Assert.Equal(Map.SpawnTiles, zone.SpawnTiles);

        // ...and login-time resolution round-robins them: a fresh/invalid persisted position (the
        // legacy default tile counts as invalid) wakes each newcomer on the next plaza anchor.
        var expected = Map.SpawnTiles.Concat(Map.SpawnTiles).ToArray();
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], zone.ResolvePlayerSpawnTile(TileGrid.DefaultSpawnTile));
        }
    }

    [Fact]
    public void ValidPersistedPositionStillBeatsTheSpawnAnchors()
    {
        // D4 leaves the relog contract alone: a walkable persisted tile (that isn't the legacy
        // default) is kept verbatim — only new/invalid positions go to the plaza.
        var zone = CreateAuthoredZone();
        var persisted = new TileCoord(150, 200); // open floor-1 grass, far from town
        Assert.True(zone.IsWalkable(persisted));
        Assert.Equal(persisted, zone.ResolvePlayerSpawnTile(persisted));
    }

    [Fact]
    public void ExplicitDistributionOverridesTheAnchors()
    {
        // The stress/dev override: an explicit MMO_SPAWN_DISTRIBUTION value must win over the
        // authored anchors entirely (Clustered = the single central tile, as ever).
        var zone = CreateAuthoredZone(SpawnDistribution.Clustered);
        var center = new TileCoord(AuthoredMaps.TownAndFloor1Width / 2, AuthoredMaps.TownAndFloor1Height / 2);
        Assert.Equal(new[] { center }, zone.SpawnTiles);
    }

    [Fact]
    public void AuthoredDistributionOnProceduralMapFallsBackToDistributedGrid()
    {
        // A genVersion-1 world booted with the new default distribution must behave exactly as the
        // historical Distributed default did (no authored anchors exist to use).
        var authored = Zone.CreateGenerated(128, 128, 0, 1, SpawnDistribution.Authored);
        var distributed = Zone.CreateGenerated(128, 128, 0, 1, SpawnDistribution.Distributed);
        Assert.Equal(distributed.SpawnTiles, authored.SpawnTiles);
    }

    // NODE-FIELD N2: ResourceScatterOnlyLandsOnGrassAndNeverOnMarkerTiles (D6) tested Zone.PlanResourceNodeScatter
    // directly, now deleted along with the entity scatter path. The equivalent (and stronger — it also covers
    // the N2 approach-room rule) invariant is pinned in NodeCatalogTests.RealMap_EveryScatterEntryIsGrassWalkableAndOffAnyMarkerTile.

    [Fact]
    public void BootSpawnsThePropsAndTheNodeFieldPinsTheMarkerNodesFirst()
    {
        // The boot wiring end-to-end (GameServer ctor, no network): `H`/`P` markers become the inert
        // "House"/"Portal" Resource-kind visuals at their anchor tiles. NODE-FIELD N2: `T`/`R` pins are NO
        // LONGER entities — they are the shared NodeCatalog's first two indices (NodeCatalog.Build's
        // pin-stability contract, D1), at exactly their authored tiles, reachable via GameServer's
        // NodeFieldForTests test seam.
        var options = new ServerOptions(
            Port: 7777,
            TickRate: 20,
            ConnectionKey: "test",
            DatabaseProvider: DatabaseProvider.Sqlite,
            ConnectionString: "Data Source=:memory:",
            MigrationsPath: "unused",
            WorldWidthTiles: AuthoredMaps.TownAndFloor1Width,
            WorldHeightTiles: AuthoredMaps.TownAndFloor1Height,
            StepCooldownMs: 250,
            PersistenceCheckpointSeconds: 15,
            InterestRadius: 18f,
            MaxVisibleEntities: 150,
            SpawnDistribution: SpawnDistribution.Authored,
            AdminNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        {
            GenVersion = TerrainGenerator.AuthoredGenVersion,
        };

        var server = new GameServer(options, new NullCharacterRepository());
        var entities = server.ZoneForTests.World.Entities;

        var houses = entities.Where(e => e.Kind == EntityKind.Resource && e.DisplayName == "House").ToArray();
        var portals = entities.Where(e => e.Kind == EntityKind.Resource && e.DisplayName == "Portal").ToArray();
        Assert.Equal(7, houses.Length);
        Assert.Equal(2, portals.Length);
        Assert.Equal(
            Map.Markers.Where(m => m.Kind == AuthoredMarkerKind.House).Select(m => m.Tile).OrderBy(t => (t.Y, t.X)),
            houses.Select(h => h.TileCoord).OrderBy(t => (t.Y, t.X)));

        // The pinned oak and quarry rock are NEVER WorldEntities anymore.
        Assert.DoesNotContain(entities, e => e.DisplayName is "Tree" or "Rock");

        // They ARE the catalogue's first two indices (D1 pin-stability), at exactly their authored tiles —
        // mirrors TownAndFloor1MapTests.MarkersAreSevenHousesTwoPortalsAndTheTwoPins' pin tiles.
        var nodeField = server.NodeFieldForTests;
        Assert.True(nodeField.Count >= 2, "Expected at least the two authored pins in the catalogue.");
        Assert.Equal(new TileCoord(188, 22), nodeField.EntryAt(0).Tile);
        Assert.Equal(NodeType.Tree, nodeField.EntryAt(0).NodeType);
        Assert.Equal(new TileCoord(204, 22), nodeField.EntryAt(1).Tile);
        Assert.Equal(NodeType.Rock, nodeField.EntryAt(1).NodeType);
    }

    // The boot-wiring test never touches persistence: GameServer's ctor only wires the repository
    // into the write-behind worker, which stays idle without sessions.
    private sealed class NullCharacterRepository : ICharacterRepository
    {
        public Task<CharacterRecord> LoadOrCreateAsync(string accountName, string displayName, CancellationToken cancellationToken)
            => throw new NotSupportedException("Boot-wiring test: no logins expected.");

        public Task SavePositionAsync(Guid characterId, WorldVector position, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<ItemStack>> LoadItemsAsync(Guid characterId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ItemStack>>([]);

        public Task SaveItemsAsync(Guid characterId, IReadOnlyList<ItemStack> changes, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
