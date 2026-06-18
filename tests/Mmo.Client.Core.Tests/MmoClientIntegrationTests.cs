using System.Net;
using System.Net.Sockets;
using Mmo.Server.Configuration;
using Mmo.Server.Data;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

public sealed class MmoClientIntegrationTests
{
    [Fact]
    public async Task ClientLogsInAndReceivesServerAndZoneInfo()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateServerOptions(port, database.ConnectionString);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var client = CreateClient("Alice", port, options.ConnectionKey);
            client.Connect();

            await WaitUntilAsync(
                () => client.IsLoggedIn
                    && client.Server is not null
                    && client.Zone is not null
                    && client.LocalNetworkId.HasValue,
                client);

            Assert.Equal(20, client.Server!.TickRate);
            Assert.Equal(50, client.Server.StepCooldownMs);
            Assert.Equal(30f, client.Server.InterestRadiusTiles);
            Assert.Equal(50d, client.Server.EffectiveStepCadenceMs);
            Assert.Equal(64, client.Zone!.Width);
            Assert.Equal(64, client.Zone.Height);
            Assert.Contains(new TileCoord(16, 8), client.Zone.BlockedTiles);
            Assert.True(client.LocalCharacterId != Guid.Empty);
            Assert.True(client.TryGetEntity(client.LocalNetworkId!.Value, out var local));
            Assert.True(local.IsLocal);
            Assert.Equal("Alice", local.DisplayName);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task ClientReplicatesRemoteMovementAndChatAgainstServer()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateServerOptions(port, database.ConnectionString);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var alice = CreateClient("Alice", port, options.ConnectionKey);
            using var bob = CreateClient("Bob", port, options.ConnectionKey);
            alice.Connect();
            bob.Connect();

            await WaitUntilAsync(
                () => alice.IsLoggedIn
                    && bob.IsLoggedIn
                    && alice.LocalNetworkId.HasValue
                    && bob.LocalNetworkId.HasValue,
                alice,
                bob);

            await WaitUntilAsync(
                () => alice.TryGetEntity(bob.LocalNetworkId!.Value, out var seenBob)
                    && bob.TryGetEntity(alice.LocalNetworkId!.Value, out var seenAlice)
                    && !seenBob.IsLocal
                    && !seenAlice.IsLocal,
                alice,
                bob);

            var bobStart = bob.LocalTile!.Value;
            bob.SendMoveStep(Direction8.E);
            await WaitUntilAsync(
                () => bob.LocalTile!.Value.X > bobStart.X
                    && alice.TryGetEntity(bob.LocalNetworkId!.Value, out var seenBob)
                    && seenBob.Tile == bob.LocalTile,
                alice,
                bob);

            var aliceStart = alice.LocalTile!.Value;
            alice.SendMoveStep(Direction8.S);
            await WaitUntilAsync(
                () => alice.LocalTile!.Value.Y > aliceStart.Y,
                alice,
                bob);

            alice.SendChat("hello from core");
            await WaitUntilAsync(
                () => bob.ChatLog.Any(line => line.Sender == "Alice" && line.Text == "hello from core"),
                alice,
                bob);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    private static MmoClient CreateClient(string name, int port, string connectionKey)
    {
        return new MmoClient(new ClientConnectionOptions("127.0.0.1", port, connectionKey, name, name));
    }

    private static ServerOptions CreateServerOptions(int port, string connectionString)
    {
        return new ServerOptions(
            port,
            20,
            "client-core-test",
            DatabaseProvider.Sqlite,
            connectionString,
            TestSqliteDatabase.MigrationsPath,
            64,
            64,
            50,
            30,
            150,
            SpawnDistribution.Clustered,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static int GetFreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, params MmoClient[] clients)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var deadline = startedAt + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            foreach (var client in clients)
            {
                client.Poll(elapsed);
            }

            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for client core integration condition.");
    }
}
