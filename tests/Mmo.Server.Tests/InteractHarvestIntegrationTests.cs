using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using Mmo.Server.Configuration;
using Mmo.Server.Data;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Domain.Population;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Server.Tests;

// NODE-FIELD N2 (docs/node-field-design.md): end-to-end coverage of the harvest loop against a live
// GameServer, now that harvestable nodes are catalogue INDICES, not WorldEntities. A player adjacent to an
// available catalogue node harvests it by index (HarvestNodeMessage): item lands in the S37 inventory, the
// node depletes and broadcasts NodeState(depleted=true) to EVERY connected client (D4 — global, NOT
// AOI-scoped, unlike the retired entity path's AOI-gated Depleted bit), then respawns and broadcasts
// NodeState(depleted=false). Invalid harvests are rejected with a reason and no state change. A late-joining
// client's login NodeStateBatch reflects an already-depleted node. A mid-session relogin (account takeover)
// keeps the harvested item.
public sealed class InteractHarvestIntegrationTests
{
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

            // NODE-FIELD determinism (D2): the client independently builds the SAME catalogue the server did
            // and the ZoneInfo.CatalogHash the server sent agrees with it.
            await WaitUntilAsync(() => client.ZoneInfo is not null, client);
            var independentCatalog = BuildCatalogLikeTheServer(options);
            Assert.Equal(independentCatalog.CatalogHash, client.ZoneInfo!.CatalogHash);

            // Nothing depleted yet: the login NodeStateBatch was empty.
            Assert.Empty(client.DepletedNodeIndices);

            client.SendHarvestNode(placement.NodeIndex);
            await WaitUntilAsync(() => client.InteractResults.Any(), client);

            var harvest = client.InteractResults.Last();
            Assert.True(harvest.Success);
            Assert.Equal("", harvest.Reason);

            await WaitUntilAsync(() => client.InventoryUpdates.Any(), client);
            var inventory = client.InventoryUpdates.Last();
            Assert.Contains(inventory.ChangedStacks, stack => stack.TemplateKey == "wood" && stack.Quantity == 1);

            // NodeState(depleted=true) replicates to the harvester (GLOBAL — see the AOI test below for the
            // stronger "reaches an observer outside AOI too" assertion).
            await WaitUntilAsync(() => client.DepletedNodeIndices.Contains(placement.NodeIndex), client);

            // Respawn restores availability (tree respawns after 100 ticks; at 20Hz that's ~5s).
            await WaitUntilAsync(() => !client.DepletedNodeIndices.Contains(placement.NodeIndex), TimeSpan.FromSeconds(12), client);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task TooFarHarvestIsRejectedWithoutDepletingNode()
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

            // We spawn in reach of the node; step away (in whichever vertical direction has room) until we
            // are 2+ tiles off — comfortably past the Phase-9 Euclidean interaction radius (1.5 tiles) — so
            // the harvest must be rejected too_far.
            var away = client.OwnTile.Y <= placement.NodeTile.Y ? Direction8.N : Direction8.S;
            await StepUntilAsync(client, away, () => Math.Abs(client.OwnTile.Y - placement.NodeTile.Y) > 1);

            client.SendHarvestNode(placement.NodeIndex);
            await WaitUntilAsync(() => client.InteractResults.Any(), client);

            var result = client.InteractResults.Last();
            Assert.False(result.Success);
            Assert.Equal("too_far", result.Reason);
            Assert.Empty(client.InventoryUpdates);
            Assert.DoesNotContain(placement.NodeIndex, client.DepletedNodeIndices);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task OutOfRangeNodeIndexIsRejectedAsNoTarget()
    {
        // NODE-FIELD N2: an index past the end of the catalogue (never valid) must be rejected the same way
        // a hostile/stale client naming a gone entity used to be — "no_target", no state change.
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var client = new IntegrationClient("OutOfRange");
            client.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => client.IsLoggedIn && client.OwnNetworkId != 0, client);

            client.SendHarvestNode(ushort.MaxValue);
            await WaitUntilAsync(() => client.InteractResults.Any(), client);

            var result = client.InteractResults.Last();
            Assert.False(result.Success);
            Assert.Equal("no_target", result.Reason);
            Assert.Empty(client.InventoryUpdates);
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
        // NODE-FIELD N2: InteractRequest is corpse-open only now — every other visible entity (here, another
        // player) is "not_resource", exactly as a non-node target always was.
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

            client.SendHarvestNode(placement.NodeIndex);
            await WaitUntilAsync(() => client.InteractResults.Any(r => r.Success), client);

            // Harvest the now-depleted node again and assert it's rejected as "depleted". The server applies
            // a 4-tick interact rate-limit (ClientSession.TryConsumeInteract, shared with HarvestNode): under
            // parallel-suite jitter an immediate retry can land inside that window and come back
            // "rate_limited" instead. So retry the second harvest, ignoring any "rate_limited" replies, until
            // we observe the rate-limit-independent verdict — which for a depleted node must be "depleted".
            var depletedRejection = await PollForInteractAsync(
                client,
                r => !r.Success && r.Reason != "rate_limited",
                () =>
                {
                    client.ClearInteractResults();
                    client.SendHarvestNode(placement.NodeIndex);
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
    public async Task HarvestBroadcastsNodeStateToBothClientsRegardlessOfAoi()
    {
        // D4: a node-state flip is GLOBAL, not AOI-scoped — the retired entity path's Depleted bit was
        // AOI-gated (an out-of-range observer never saw it); NodeStateMessage deliberately is NOT. Use a
        // small interest radius and walk the observer well outside it, so this would FAIL under the old
        // AOI-gated behavior and only passes because the new broadcast is unconditional.
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
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

            // Observer logs in then walks far away so the node is outside its small (radius 4) entity AOI —
            // irrelevant to NodeState now, but proves the broadcast really doesn't gate on it.
            observer.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => observer.IsLoggedIn && observer.OwnNetworkId != 0, observer, harvester);
            var awayFromNode = placement.NodeTile.X < AuthoredMaps.TownAndFloor1Width / 2 ? Direction8.E : Direction8.W;
            await StepUntilAsync(
                observer,
                awayFromNode,
                () => Math.Abs(observer.OwnTile.X - placement.NodeTile.X) > 8,
                harvester);

            harvester.SendHarvestNode(placement.NodeIndex);
            await WaitUntilAsync(() => harvester.InteractResults.Any(r => r.Success), harvester, observer);

            // BOTH clients see the flip — the harvester (adjacent) AND the far-away observer.
            await WaitUntilAsync(() => harvester.DepletedNodeIndices.Contains(placement.NodeIndex), harvester, observer);
            await WaitUntilAsync(() => observer.DepletedNodeIndices.Contains(placement.NodeIndex), harvester, observer);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task LateJoinerSeesAnAlreadyDepletedNodeViaTheLoginBatch()
    {
        // D4: NodeStateBatchMessage on login carries the field's CURRENT exceptions — a client that never
        // witnessed the harvest (it wasn't even connected yet) must still see the node as depleted.
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString);
        var repository = new SqliteCharacterRepository(database.ConnectionString);
        var placement = DeterministicTreePlacement(options);
        await SeedSpawnAdjacentToNodeAsync(repository, "FirstHarvester", placement);
        var server = new GameServer(options, repository);
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var first = new IntegrationClient("FirstHarvester");
            first.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => first.IsLoggedIn && first.OwnNetworkId != 0, first);

            first.SendHarvestNode(placement.NodeIndex);
            await WaitUntilAsync(() => first.InteractResults.Any(r => r.Success), first);
            await WaitUntilAsync(() => first.DepletedNodeIndices.Contains(placement.NodeIndex), first);

            // A SECOND, unrelated client logs in AFTER the harvest — never sees the flip live, only the login
            // batch.
            using var joiner = new IntegrationClient("LateJoiner");
            joiner.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => joiner.IsLoggedIn && joiner.OwnNetworkId != 0, joiner, first);

            await WaitUntilAsync(() => joiner.DepletedNodeIndices.Contains(placement.NodeIndex), joiner, first);
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

            first.SendHarvestNode(placement.NodeIndex);
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

            // The taking-over session must carry the harvested wood, AND its login NodeStateBatch must show
            // the node still depleted (the field's live state, unaffected by the session takeover). Wait for
            // it to respawn, harvest again: the InventoryUpdate now reports a total of 2 wood, proving the
            // first session's not-yet-flushed harvest was handed off, not lost.
            Assert.Contains(placement.NodeIndex, second.DepletedNodeIndices);
            await WaitUntilAsync(() => !second.DepletedNodeIndices.Contains(placement.NodeIndex), TimeSpan.FromSeconds(12), second);
            second.SendHarvestNode(placement.NodeIndex);
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

    // NODE-FIELD N2: the AUTHORED town+floor-1 map (384x384) so a real NodeCatalog exists — a procedural
    // (genVersion 1) map has no authored data to scatter from and would carry the trivial empty catalogue,
    // giving nothing to harvest. Mirrors TelegraphWireIntegrationTests.CreateAuthoredOptions.
    private static ServerOptions CreateOptions(int port, string connectionString, float interestRadius = 30f)
    {
        return new ServerOptions(
            port,
            20,
            "interact-harvest-test",
            DatabaseProvider.Sqlite,
            connectionString,
            TestSqliteDatabase.MigrationsPath,
            AuthoredMaps.TownAndFloor1Width,
            AuthoredMaps.TownAndFloor1Height,
            50,
            15,
            interestRadius,
            150,
            SpawnDistribution.Clustered,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        {
            GenVersion = TerrainGenerator.AuthoredGenVersion,
        };
    }

    // Where a real, deterministic catalogue Tree node sits and the walkable tile the player should spawn on
    // to be in interaction reach of it (no in-sim movement, no wall pathing). The chosen stand tile is a
    // Chebyshev-1 neighbour, so it is within the Phase-9 Euclidean radius (<= sqrt(2) ~= 1.414 < 1.5).
    private readonly record struct TreePlacement(ushort NodeIndex, TileCoord NodeTile, TileCoord SpawnTile);

    // Node placement is deterministic (docs/node-field-design.md D1/D2): instead of walking the player
    // across the authored map to find a node — slow and flaky, because the naive step-toward helper can't
    // path around interior walls — we reconstruct the exact same NodeCatalog the GameServer builds and ask
    // it where the nodes are. We then pick a Tree whose tile has a walkable Chebyshev-neighbour and pre-seed
    // the player's persisted tile to that neighbour, so login spawns the player already adjacent to a real,
    // reachable node. This touches no production placement code; it only reads the public, deterministic
    // NodeCatalog.Build (the same call GameServer's ctor makes).
    private static TreePlacement DeterministicTreePlacement(ServerOptions options)
    {
        var catalog = BuildCatalogLikeTheServer(options);
        var zone = Zone.CreateGenerated(
            options.WorldWidthTiles,
            options.WorldHeightTiles,
            options.MapSeed,
            options.GenVersion,
            options.SpawnDistribution);

        foreach (var entry in catalog.Entries)
        {
            if (entry.NodeType != NodeType.Tree)
            {
                continue;
            }

            if (TryFindAdjacentStandTile(zone, entry.Tile, out var stand))
            {
                return new TreePlacement(checked((ushort)entry.Index), entry.Tile, stand);
            }
        }

        throw new InvalidOperationException(
            "No Tree node with a walkable adjacent tile was found in the catalogue; " +
            "check the map seed/dims/genVersion used by CreateOptions.");
    }

    // Mirrors GameServer's own ctor build (_zone.Authored is {} map ? NodeCatalog.Build(_zone.Seed, map) :
    // NodeCatalog.Empty()) exactly, so the test's placement/hash reasoning matches production byte-for-byte.
    private static NodeCatalog BuildCatalogLikeTheServer(ServerOptions options)
    {
        var zone = Zone.CreateGenerated(
            options.WorldWidthTiles,
            options.WorldHeightTiles,
            options.MapSeed,
            options.GenVersion,
            options.SpawnDistribution);

        return zone.Authored is { } authoredMap ? NodeCatalog.Build(zone.Seed, authoredMap) : NodeCatalog.Empty();
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
    // no in-sim walking across the map. Uses only the production persistence path (LoadOrCreate + SaveTile);
    // no production code is modified.
    private static async Task SeedSpawnAdjacentToNodeAsync(
        SqliteCharacterRepository repository,
        string accountName,
        TreePlacement placement)
    {
        var character = await repository.LoadOrCreateAsync(accountName, accountName, CancellationToken.None);
        await repository.SavePositionAsync(character.CharacterId, WorldVector.FromTile(placement.SpawnTile), CancellationToken.None);
    }

    // Repeatedly triggers a harvest attempt and polls until a result satisfying the predicate arrives,
    // re-issuing on each poll cycle. Used to retry past transient "rate_limited" replies (the 4-tick
    // interact cooldown, shared by HarvestNode) so the test asserts the rate-limit-independent verdict
    // deterministically.
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
        private readonly HashSet<ushort> _depletedNodeIndices = new();
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
        public ZoneInfoMessage? ZoneInfo { get; private set; }
        public bool IsLoggedIn { get; private set; }
        public bool IsDisconnected { get; private set; }
        public Guid CharacterId { get; private set; }
        public uint OwnNetworkId { get; private set; }
        public TileCoord OwnTile { get; private set; } = TileGrid.DefaultSpawnTile;

        // NODE-FIELD N2: the field's live exceptions as replicated to THIS client (upserted by NodeState,
        // replaced wholesale by NodeStateBatch on login) — the client-side mirror MmoClient itself keeps.
        public IReadOnlySet<ushort> DepletedNodeIndices => _depletedNodeIndices;

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

        // CONTINUOUS MIGRATION (Phase 3, v36): per-input continuous MoveIntent — unit dir + nominal dt; (0,0) = stop.
        public void SendMove(Direction8 direction)
        {
            var dir = direction.ToUnitVector();
            Send(new MoveIntentMessage(++_moveSequence, (float)dir.X, (float)dir.Y, 1f / 20f), DeliveryMethod.Unreliable);
        }

        public void StopMove()
        {
            Send(new MoveIntentMessage(++_moveSequence, 0f, 0f, 1f / 20f), DeliveryMethod.Unreliable);
        }

        public void SendInteract(uint targetNetworkId)
        {
            Send(new InteractRequestMessage(targetNetworkId), DeliveryMethod.ReliableOrdered);
        }

        // NODE-FIELD N2: the index-keyed harvest request replacing the old entity-targeted SendInteract for nodes.
        public void SendHarvestNode(ushort nodeIndex)
        {
            Send(new HarvestNodeMessage(nodeIndex), DeliveryMethod.ReliableOrdered);
        }

        public void ClearMessages()
        {
            Messages.Clear();
        }

        public void ClearInteractResults()
        {
            InteractResults.Clear();
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
                    case ZoneInfoMessage zoneInfo:
                        ZoneInfo = zoneInfo;
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
                    case NodeStateMessage nodeState:
                        // NODE-FIELD N2: one node's flip (harvest or respawn), GLOBAL — mirrors MmoClient's own
                        // upsert exactly.
                        if (nodeState.Depleted)
                        {
                            _depletedNodeIndices.Add(nodeState.NodeIndex);
                        }
                        else
                        {
                            _depletedNodeIndices.Remove(nodeState.NodeIndex);
                        }

                        break;
                    case NodeStateBatchMessage nodeBatch:
                        // NODE-FIELD N2: the login snapshot of current exceptions — REPLACES the set (it IS the
                        // full truth at that instant), mirrors MmoClient's own handling.
                        _depletedNodeIndices.Clear();
                        foreach (var index in nodeBatch.DepletedIndices)
                        {
                            _depletedNodeIndices.Add(index);
                        }

                        break;
                    case WorldSnapshotMessage snapshot:
                        Send(new SnapshotAckMessage(snapshot.SnapshotSequence), DeliveryMethod.Sequenced);
                        foreach (var entity in snapshot.Entities)
                        {
                            if (entity.NetworkId == OwnNetworkId)
                            {
                                OwnTile = entity.Position.ToTileRounded();
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
