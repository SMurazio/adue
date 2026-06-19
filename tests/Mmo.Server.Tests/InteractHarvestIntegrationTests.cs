using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using Mmo.Server.Configuration;
using Mmo.Server.Data;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Server.Tests;

// End-to-end coverage of the S38 gather loop against a live GameServer: a player adjacent to an
// Available resource node harvests it (item lands in the S37 inventory, node depletes and replicates as
// Depleted by AOI, then respawns), invalid interactions are rejected with a reason and no state change,
// the node-depleted bit never reaches a client that can't see the node, and a mid-session relogin
// (account takeover) keeps the harvested item.
public sealed class InteractHarvestIntegrationTests
{
    // "tree" is the first scattered node type (yields wood).
    private const string TreeNodeName = "Tree";

    [Fact]
    public async Task AdjacentPlayerHarvestsNodeDepletesItAndRespawns()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString);
        var repository = new SqliteCharacterRepository(database.ConnectionString);
        var placement = DeterministicTreePlacement(options);
        await SeedSpawnAdjacentToNodeAsync(repository, "Harvester", placement);
        var server = new GameServer(options, repository);
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var client = new IntegrationClient("Harvester");
            client.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => client.IsLoggedIn && client.OwnNetworkId != 0, client);

            var node = await ResolveSpawnedNodeAsync(client, placement);

            client.SendInteract(node.NetworkId);
            await WaitUntilAsync(() => client.InteractResults.Any(), client);

            var harvest = client.InteractResults.Last();
            Assert.True(harvest.Success);
            Assert.Equal("", harvest.Reason);

            await WaitUntilAsync(() => client.InventoryUpdates.Any(), client);
            var inventory = client.InventoryUpdates.Last();
            Assert.Contains(inventory.ChangedStacks, stack => stack.TemplateKey == "wood" && stack.Quantity == 1);

            // Node-depleted replicates by AOI: the observer (us) sees Depleted=true for this node.
            await WaitUntilAsync(() => client.LatestDepletedState(node.NetworkId) == true, client);

            // Respawn restores availability (tree respawns after 100 ticks; at 20Hz that's ~5s).
            await WaitUntilAsync(() => client.LatestDepletedState(node.NetworkId) == false, TimeSpan.FromSeconds(12), client);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task TooFarInteractIsRejectedWithoutDepletingNode()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString);
        var repository = new SqliteCharacterRepository(database.ConnectionString);
        var placement = DeterministicTreePlacement(options);
        await SeedSpawnAdjacentToNodeAsync(repository, "FarAway", placement);
        var server = new GameServer(options, repository);
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var client = new IntegrationClient("FarAway");
            client.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => client.IsLoggedIn && client.OwnNetworkId != 0, client);

            var node = await ResolveSpawnedNodeAsync(client, placement);
            // We spawn adjacent to the scattered node; step away (in whichever vertical direction has room)
            // until we are no longer adjacent, so the interact must be rejected too_far.
            var away = client.OwnTile.Y <= node.Tile.Y ? Direction8.N : Direction8.S;
            await StepUntilAsync(client, away, () => Math.Abs(client.OwnTile.Y - node.Tile.Y) > 1);

            client.SendInteract(node.NetworkId);
            await WaitUntilAsync(() => client.InteractResults.Any(), client);

            var result = client.InteractResults.Last();
            Assert.False(result.Success);
            Assert.Equal("too_far", result.Reason);
            Assert.Empty(client.InventoryUpdates);
            Assert.NotEqual(true, client.LatestDepletedState(node.NetworkId));
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task InteractingWithNonResourceIsRejected()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var actor = new IntegrationClient("Actor");
            using var target = new IntegrationClient("Target");
            actor.Connect(port, options.ConnectionKey);
            target.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(
                () => actor.IsLoggedIn && actor.OwnNetworkId != 0 && target.IsLoggedIn && target.OwnNetworkId != 0,
                actor,
                target);

            // Both spawn clustered at the same tile, so the actor sees the target player entity adjacent.
            await WaitUntilAsync(() => actor.KnownSpawns.Any(s => s.NetworkId == target.OwnNetworkId), actor, target);

            actor.SendInteract(target.OwnNetworkId);
            await WaitUntilAsync(() => actor.InteractResults.Any(), actor, target);

            var result = actor.InteractResults.Last();
            Assert.False(result.Success);
            Assert.Equal("not_resource", result.Reason);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task DepletedNodeRejectsSecondHarvest()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString);
        var repository = new SqliteCharacterRepository(database.ConnectionString);
        var placement = DeterministicTreePlacement(options);
        await SeedSpawnAdjacentToNodeAsync(repository, "DoubleHarvest", placement);
        var server = new GameServer(options, repository);
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var client = new IntegrationClient("DoubleHarvest");
            client.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => client.IsLoggedIn && client.OwnNetworkId != 0, client);

            var node = await ResolveSpawnedNodeAsync(client, placement);

            client.SendInteract(node.NetworkId);
            await WaitUntilAsync(() => client.InteractResults.Any(r => r.Success), client);

            // Harvest the now-depleted node again and assert it's rejected as "depleted". The server applies
            // a 4-tick interact rate-limit (ClientSession.TryConsumeInteract): under parallel-suite jitter an
            // immediate retry can land inside that window and come back "rate_limited" instead (todo/N22). So
            // retry the second interact, ignoring any "rate_limited" replies, until we observe the
            // rate-limit-independent verdict — which for a depleted node must be "depleted".
            var depletedRejection = await PollForInteractAsync(
                client,
                r => !r.Success && r.Reason != "rate_limited",
                () =>
                {
                    client.ClearInteractResults();
                    client.SendInteract(node.NetworkId);
                });

            Assert.False(depletedRejection.Success);
            Assert.Equal("depleted", depletedRejection.Reason);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task NodeDepletedStateIsNeverSerializedToAClientThatCannotSeeIt()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        // Small interest radius so a player who steps away genuinely loses sight of the node.
        var options = CreateOptions(port, database.ConnectionString, interestRadius: 4f);
        var repository = new SqliteCharacterRepository(database.ConnectionString);
        var placement = DeterministicTreePlacement(options);
        await SeedSpawnAdjacentToNodeAsync(repository, "Harvester", placement);
        var server = new GameServer(options, repository);
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var harvester = new IntegrationClient("Harvester");
            using var observer = new IntegrationClient("Observer");
            harvester.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => harvester.IsLoggedIn && harvester.OwnNetworkId != 0, harvester);

            var node = await ResolveSpawnedNodeAsync(harvester, placement);

            // Observer logs in then walks far away so the node is outside its small (radius 4) AOI. Walk
            // toward the horizontal map edge farthest from the node so we reliably clear the AOI window
            // wherever the scattered node landed.
            observer.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => observer.IsLoggedIn && observer.OwnNetworkId != 0, observer, harvester);
            var awayFromNode = node.Tile.X < 32 ? Direction8.E : Direction8.W;
            await StepUntilAsync(
                observer,
                awayFromNode,
                () => Math.Abs(observer.OwnTile.X - node.Tile.X) > 8,
                harvester);

            observer.ClearMessages();
            harvester.SendInteract(node.NetworkId);
            await WaitUntilAsync(() => harvester.InteractResults.Any(r => r.Success), harvester, observer);

            // Let snapshots flow; the observer must never receive an entity state for this node at all.
            await PollForAsync(TimeSpan.FromMilliseconds(600), observer, harvester);

            Assert.DoesNotContain(
                observer.Messages.OfType<WorldSnapshotMessage>().SelectMany(m => m.Entities),
                entity => entity.NetworkId == node.NetworkId);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task HarvestSurvivesSameAccountTakeoverRelogin()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString);
        var repository = new SqliteCharacterRepository(database.ConnectionString);
        var placement = DeterministicTreePlacement(options);
        await SeedSpawnAdjacentToNodeAsync(repository, "Relogin", placement);
        var server = new GameServer(options, repository);
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var first = new IntegrationClient("Relogin");
            first.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => first.IsLoggedIn && first.OwnNetworkId != 0, first);

            var node = await ResolveSpawnedNodeAsync(first, placement);

            first.SendInteract(node.NetworkId);
            await WaitUntilAsync(() => first.InventoryUpdates.Any(), first);
            Assert.Contains(first.InventoryUpdates.Last().ChangedStacks, s => s.TemplateKey == "wood" && s.Quantity == 1);

            // Same account logs in again, taking over the session before any DB flush of the harvest.
            using var second = new IntegrationClient("Relogin");
            second.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(
                () => second.IsLoggedIn && second.OwnNetworkId != 0 && second.CharacterId == first.CharacterId,
                second,
                first);
            await WaitUntilAsync(() => first.IsDisconnected, second, first);

            // The taking-over session must carry the harvested wood. Wait for the same tree to respawn,
            // step adjacent, and harvest it again: the InventoryUpdate now reports a total of 2 wood,
            // proving the first session's not-yet-flushed harvest was handed off, not lost. (If the
            // takeover had reloaded the DB-stale inventory, this would report 1.)
            var node2 = await ResolveSpawnedNodeAsync(second, placement);
            await WaitUntilAsync(() => second.LatestDepletedState(node2.NetworkId) == false, TimeSpan.FromSeconds(12), second);
            second.SendInteract(node2.NetworkId);
            // With S49, the takeover session also receives a login snapshot carrying the handed-off
            // wood:1, so we cannot stop at the first wood update — wait for the post-harvest total to
            // actually reach 2 (the handed-off 1 + the re-harvested 1).
            await WaitUntilAsync(
                () => second.InventoryUpdates
                    .SelectMany(u => u.ChangedStacks)
                    .Where(s => s.TemplateKey == "wood")
                    .Any(s => s.Quantity == 2),
                second);

            var woodTotal = second.InventoryUpdates
                .SelectMany(u => u.ChangedStacks)
                .Where(s => s.TemplateKey == "wood")
                .Select(s => s.Quantity)
                .Max();
            Assert.Equal(2, woodTotal);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task LoginSendsPersistedInventorySnapshotBeforeAnyHarvest()
    {
        // S49: a character with persisted items must receive a full InventoryUpdate snapshot on login
        // (fresh-login path), so the client panel reflects the persisted contents immediately — without
        // any harvest delta. Pre-seed two stacks via the production persistence path, then assert they
        // arrive on the InventoryUpdate the client gets at login.
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString);
        var repository = new SqliteCharacterRepository(database.ConnectionString);
        var character = await repository.LoadOrCreateAsync("Stocked", "Stocked", CancellationToken.None);
        await repository.SaveItemsAsync(
            character.CharacterId,
            [new ItemStack("wood", 12), new ItemStack("stone", 5)],
            CancellationToken.None);

        var server = new GameServer(options, repository);
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var client = new IntegrationClient("Stocked");
            client.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => client.IsLoggedIn && client.OwnNetworkId != 0, client);

            // The inventory snapshot must arrive at login, before the client has issued any interact.
            await WaitUntilAsync(() => client.InventoryUpdates.Any(), client);
            Assert.Empty(client.InteractResults);

            var snapshot = client.InventoryUpdates.First();
            Assert.Contains(snapshot.ChangedStacks, s => s.TemplateKey == "wood" && s.Quantity == 12);
            Assert.Contains(snapshot.ChangedStacks, s => s.TemplateKey == "stone" && s.Quantity == 5);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task TakeoverLoginSendsTheHandedOffInventorySnapshot()
    {
        // S49 (takeover path): when a second session takes over an existing character, it must also receive
        // a full inventory snapshot on login. Pre-seed items, log a first session in, then take it over and
        // assert the taking-over session is sent the inventory the entity ends up with — before any harvest.
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString);
        var repository = new SqliteCharacterRepository(database.ConnectionString);
        var character = await repository.LoadOrCreateAsync("Takeover", "Takeover", CancellationToken.None);
        await repository.SaveItemsAsync(
            character.CharacterId,
            [new ItemStack("wood", 7)],
            CancellationToken.None);

        var server = new GameServer(options, repository);
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var first = new IntegrationClient("Takeover");
            first.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => first.IsLoggedIn && first.OwnNetworkId != 0, first);
            await WaitUntilAsync(() => first.InventoryUpdates.Any(), first);

            using var second = new IntegrationClient("Takeover");
            second.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(
                () => second.IsLoggedIn && second.OwnNetworkId != 0 && second.CharacterId == first.CharacterId,
                second,
                first);

            // The taking-over session receives the inventory snapshot on login, no harvest involved.
            await WaitUntilAsync(() => second.InventoryUpdates.Any(), second, first);
            Assert.Empty(second.InteractResults);
            Assert.Contains(second.InventoryUpdates.First().ChangedStacks, s => s.TemplateKey == "wood" && s.Quantity == 7);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    private static ServerOptions CreateOptions(int port, string connectionString, float interestRadius = 30f)
    {
        return new ServerOptions(
            port,
            20,
            "interact-harvest-test",
            DatabaseProvider.Sqlite,
            connectionString,
            TestSqliteDatabase.MigrationsPath,
            64,
            64,
            50,
            15,
            interestRadius,
            150,
            SpawnDistribution.Clustered,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        {
            // Dense scatter on the small 64² test map so a harvestable node is reliably near the clustered
            // spawn (and quickly reachable under the small-radius test's moving AOI window). ~64 nodes.
            ResourceNodeDensityTilesPerNode = 8,
        };
    }

    // Where the deterministically-scattered Tree node sits and the walkable tile the player should spawn
    // on to be Chebyshev-adjacent to it (no in-sim movement, no wall pathing).
    private readonly record struct TreePlacement(TileCoord NodeTile, TileCoord SpawnTile);

    // Resource-node placement is deterministic and seeded (S44): identical (seed, size, density, registry)
    // inputs yield a byte-identical layout. So instead of walking the player across a walled map to find a
    // node — slow and flaky, because the naive step-toward helper can't path around interior walls — we
    // reconstruct the exact same Zone the GameServer builds and ask it where the nodes are. We then pick a
    // Tree whose tile has a walkable Chebyshev-neighbour and pre-seed the player's persisted tile to that
    // neighbour, so login spawns the player already adjacent to a real, reachable node. This touches no
    // production placement code; it only reads the public, deterministic PlanResourceNodeScatter.
    private static TreePlacement DeterministicTreePlacement(ServerOptions options)
    {
        // Mirror GameServer's world construction exactly (same size, seed, gen version, distribution and
        // registries) so the computed layout matches the server's spawned nodes tile-for-tile.
        var zone = Zone.CreateGenerated(
            options.WorldWidthTiles,
            options.WorldHeightTiles,
            options.MapSeed,
            TerrainGenerator.CurrentGenVersion,
            options.SpawnDistribution);
        var registry = ResourceNodeRegistry.CreateDefault(ItemRegistry.Default);

        var placements = zone.PlanResourceNodeScatter(registry, options.ResourceNodeDensityTilesPerNode);
        var spawnCenter = zone.SpawnTiles[0];

        // Among Tree nodes that have a walkable, non-default neighbour to stand on, prefer the one whose
        // chosen stand-tile is nearest the clustered spawn centre (keeps observers/walks geometrically close
        // to the original test, and keeps coordinates well away from the blocked border).
        TreePlacement? best = null;
        var bestDistance = int.MaxValue;
        foreach (var (definition, tile) in placements)
        {
            if (definition.DisplayName != TreeNodeName)
            {
                continue;
            }

            if (!TryFindAdjacentStandTile(zone, tile, out var stand))
            {
                continue;
            }

            var distance = Math.Max(Math.Abs(stand.X - spawnCenter.X), Math.Abs(stand.Y - spawnCenter.Y));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = new TreePlacement(tile, stand);
            }
        }

        return best ?? throw new InvalidOperationException(
            "No Tree node with a walkable adjacent tile was placed for the test map; " +
            "check the map seed/size/density used by CreateOptions.");
    }

    // Finds a walkable tile that is Chebyshev-adjacent (<= 1, excluding the node tile itself) to a node and
    // is not the legacy DefaultSpawnTile — so ResolvePlayerSpawnTile honours it as a persisted spawn tile.
    private static bool TryFindAdjacentStandTile(Zone zone, TileCoord node, out TileCoord stand)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                var candidate = new TileCoord(node.X + dx, node.Y + dy);
                if (candidate != TileGrid.DefaultSpawnTile && zone.IsWalkable(candidate))
                {
                    stand = candidate;
                    return true;
                }
            }
        }

        stand = default;
        return false;
    }

    // Pre-seeds the account's character with a persisted tile adjacent to the chosen node, so that when the
    // client logs in the server's ResolvePlayerSpawnTile honours it and the player spawns already adjacent —
    // no in-sim walking across the walled map. Uses only the production persistence path (LoadOrCreate +
    // SaveTile); no production code is modified.
    private static async Task SeedSpawnAdjacentToNodeAsync(
        SqliteCharacterRepository repository,
        string accountName,
        TreePlacement placement)
    {
        var character = await repository.LoadOrCreateAsync(accountName, accountName, CancellationToken.None);
        await repository.SaveTileAsync(character.CharacterId, placement.SpawnTile, CancellationToken.None);
    }

    // After login, resolves the network id of the Tree node at the expected (deterministic) tile from the
    // spawns the client has received. The player was seeded adjacent to it, so it is inside AOI and arrives
    // as an EntitySpawn promptly.
    private static async Task<(uint NetworkId, TileCoord Tile)> ResolveSpawnedNodeAsync(
        IntegrationClient client,
        TreePlacement placement)
    {
        await WaitUntilAsync(
            () => client.KnownSpawns.Any(s => s.DisplayName == TreeNodeName && s.Tile == placement.NodeTile),
            client);

        var spawn = client.KnownSpawns.Last(s => s.DisplayName == TreeNodeName && s.Tile == placement.NodeTile);
        return (spawn.NetworkId, spawn.Tile);
    }

    // Repeatedly triggers an interact attempt and polls until a result satisfying the predicate arrives,
    // re-issuing on each poll cycle. Used to retry past transient "rate_limited" replies (the 4-tick
    // interact cooldown) so the test asserts the rate-limit-independent verdict deterministically.
    private static async Task<InteractResultMessage> PollForInteractAsync(
        IntegrationClient client,
        Func<InteractResultMessage, bool> predicate,
        Action attempt)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            attempt();
            var pollUntil = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(250);
            while (DateTimeOffset.UtcNow < pollUntil)
            {
                client.Poll();
                var match = client.InteractResults.FirstOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(10);
            }
        }

        throw new TimeoutException("Timed out waiting for a matching interact result.");
    }

    private static int GetFreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, params IntegrationClient[] clients)
    {
        await WaitUntilAsync(condition, TimeSpan.FromSeconds(6), clients);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, params IntegrationClient[] clients)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var client in clients)
            {
                client.Poll();
            }

            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for interact-harvest integration condition.");
    }

    private static async Task PollForAsync(TimeSpan duration, params IntegrationClient[] clients)
    {
        var stopAt = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < stopAt)
        {
            foreach (var client in clients)
            {
                client.Poll();
            }

            await Task.Delay(10);
        }
    }

    private static async Task StepUntilAsync(IntegrationClient mover, Direction8 direction, Func<bool> condition, params IntegrationClient[] observers)
    {
        var clients = observers.Prepend(mover).Distinct().ToArray();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            mover.SendMove(direction);
            await PollForAsync(TimeSpan.FromMilliseconds(75), clients);
            if (condition())
            {
                mover.StopMove();
                await PollForAsync(TimeSpan.FromMilliseconds(75), clients);
                return;
            }
        }

        throw new TimeoutException("Timed out waiting for step movement condition.");
    }

    private sealed class IntegrationClient : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly string _name;
        private readonly Dictionary<uint, bool> _depletedByNetworkId = new();
        private NetPeer? _serverPeer;
        private uint _moveSequence;
        private bool _disposed;

        public IntegrationClient(string name)
        {
            _name = name;
            _client = new NetManager(_listener)
            {
                AutoRecycle = false
            };

            _listener.PeerConnectedEvent += peer =>
            {
                _serverPeer = peer;
                Send(new ClientHelloMessage(_name), DeliveryMethod.ReliableOrdered);
                Send(new LoginRequestMessage(_name, _name), DeliveryMethod.ReliableOrdered);
            };
            _listener.PeerDisconnectedEvent += (_, _) => IsDisconnected = true;
            _listener.NetworkReceiveEvent += OnNetworkReceive;
        }

        public List<IProtocolMessage> Messages { get; } = [];
        public List<EntitySpawnMessage> KnownSpawns { get; } = [];
        public List<InteractResultMessage> InteractResults { get; } = [];
        public List<InventoryUpdateMessage> InventoryUpdates { get; } = [];
        public bool IsLoggedIn { get; private set; }
        public bool IsDisconnected { get; private set; }
        public Guid CharacterId { get; private set; }
        public uint OwnNetworkId { get; private set; }
        public TileCoord OwnTile { get; private set; } = TileGrid.DefaultSpawnTile;

        public void Connect(int port, string key)
        {
            _client.Start();
            _client.Connect("127.0.0.1", port, key);
        }

        public void Poll()
        {
            if (!_disposed)
            {
                _client.PollEvents();
            }
        }

        // Held-direction intent (protocol v15): starts/redirects continuous movement. The server steps at
        // its own cooldown while the intent stands; call StopMove to halt at the current tile.
        public void SendMove(Direction8 direction)
        {
            Send(new MoveIntentMessage(++_moveSequence, true, direction), DeliveryMethod.ReliableOrdered);
        }

        public void StopMove()
        {
            Send(new MoveIntentMessage(++_moveSequence, false, Direction8.S), DeliveryMethod.ReliableOrdered);
        }

        public void SendInteract(uint targetNetworkId)
        {
            Send(new InteractRequestMessage(targetNetworkId), DeliveryMethod.ReliableOrdered);
        }

        public void ClearMessages()
        {
            Messages.Clear();
        }

        public void ClearInteractResults()
        {
            InteractResults.Clear();
        }

        // null = no state seen yet for this node; true/false = last replicated depleted bit.
        public bool? LatestDepletedState(uint networkId)
        {
            return _depletedByNetworkId.TryGetValue(networkId, out var depleted) ? depleted : null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _serverPeer?.Disconnect();
            _client.PollEvents();
            _client.Stop();
            _disposed = true;
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            try
            {
                var message = ProtocolCodec.Decode(reader.GetRemainingBytes());
                Messages.Add(message);
                switch (message)
                {
                    case LoginResultMessage login:
                        IsLoggedIn = login.Accepted;
                        CharacterId = login.CharacterId;
                        break;
                    case EntitySpawnMessage spawn:
                        KnownSpawns.Add(spawn);
                        if (spawn.DisplayName == _name)
                        {
                            OwnNetworkId = spawn.NetworkId;
                            OwnTile = spawn.Tile;
                        }

                        break;
                    case InteractResultMessage result:
                        InteractResults.Add(result);
                        break;
                    case InventoryUpdateMessage update:
                        InventoryUpdates.Add(update);
                        break;
                    case WorldSnapshotMessage snapshot:
                        Send(new SnapshotAckMessage(snapshot.SnapshotSequence), DeliveryMethod.Sequenced);
                        foreach (var entity in snapshot.Entities)
                        {
                            _depletedByNetworkId[entity.NetworkId] = entity.Depleted;
                            if (entity.NetworkId == OwnNetworkId)
                            {
                                OwnTile = entity.Tile;
                            }
                        }

                        break;
                }
            }
            finally
            {
                reader.Recycle();
            }
        }

        private void Send(IProtocolMessage message, DeliveryMethod deliveryMethod)
        {
            _serverPeer?.Send(ProtocolCodec.Encode(message), deliveryMethod);
        }
    }
}
