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

// ECOLOGY E4 (docs/ecology-v1-design.md §3/§8 E4, §5.4): the wire event, pinned against a LIVE server end-to-end
// (mirroring TelegraphWireIntegrationTests' harness):
//   (1) LOGIN receives the FULL authored region set (one RegionEcologyMessage per region) PLUS exactly one login
//       rumor (the single most-extreme region, D6c);
//   (2) a forced state flip (via the REAL /ecology set admin command — which calls the SAME EcologyState.
//       TrySetStock production method the E1 test seam exposes, just through the network path rather than a
//       direct call, so it is safe to drive while GameServer's own tick thread is live) re-sends EXACTLY ONE
//       RegionEcologyMessage for the changed region — no duplicates, and unaffected regions are NOT re-sent;
//   (3) /rumors emits one flavored line per authored region, matching the SAME EcologyRumors table the login
//       rumor uses.
public sealed class RegionEcologyWireIntegrationTests
{
    private const int TickRate = 20;
    private const int BaseStepCooldownMs = 140;

    [Fact]
    public async Task LoginReceivesTheFullRegionSetAndExactlyOneLoginRumor()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString, admins: ["Admin"]);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var admin = new EcologyClient("Admin");
            admin.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => admin.IsLoggedIn, admin);

            // The three authored starter regions (Content/ecology.json), one RegionEcologyMessage each.
            await WaitUntilAsync(() => admin.Regions.Count >= 3, admin);
            await PollForAsync(TimeSpan.FromMilliseconds(200), admin); // let any (unexpected) extra sends settle

            Assert.True(admin.Regions.TryGetValue("slime_hollow", out var slimeHollow));
            Assert.Equal("Slime Hollow", slimeHollow!.DisplayName);
            Assert.Equal(20, slimeHollow.MinTileX);
            Assert.Equal(120, slimeHollow.MinTileY);
            Assert.Equal(140, slimeHollow.MaxTileX);
            Assert.Equal(220, slimeHollow.MaxTileY);
            Assert.Single(slimeHollow.Types);
            Assert.Equal("slime", slimeHollow.Types[0].TypeId);
            // Fresh server: every region seeds at S=K exactly (D1), which is the RICH band [1.0,1.25), not Healthy
            // (EcologyState.StateOf's boundaries: "< 1.0" is false at ratio 1.0).
            Assert.Equal(EcologyPopulationState.Rich, slimeHollow.Types[0].State);

            Assert.True(admin.Regions.TryGetValue("eastern_scrubland", out var eastern));
            Assert.Single(eastern!.Types);
            Assert.Equal("gnoll", eastern.Types[0].TypeId);

            Assert.True(admin.Regions.TryGetValue("the_verge", out var verge));
            Assert.Equal(2, verge!.Types.Count);

            // D6c: EVERY starter region ties at distance 1 from Healthy (all RICH) at boot, so "ties -> first"
            // picks the FIRST authored region — Slime Hollow (Content/ecology.json's manifest order).
            Assert.Single(admin.SystemChatLines);
            Assert.Equal("Slime Hollow flourishes.", admin.SystemChatLines[0]);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task ForcedStateFlipEmitsExactlyOneRegionUpdate_AndLeavesOtherRegionsAlone()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString, admins: ["Admin"]);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            using var admin = new EcologyClient("Admin");
            admin.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => admin.IsLoggedIn, admin);
            await WaitUntilAsync(() => admin.Regions.Count >= 3, admin);

            // Baseline: exactly one message per region so far (the login full set).
            Assert.Equal(1, admin.RegionMessageCount("slime_hollow"));
            Assert.Equal(1, admin.RegionMessageCount("eastern_scrubland"));
            Assert.Equal(1, admin.RegionMessageCount("the_verge"));

            // Force slime_hollow/slime from its initial RICH (S=K=10) down to a value that lands DEPLETED
            // (ratio < 0.25) — a genuine flip. This calls the SAME EcologyState.TrySetStock the E1 test seam
            // exposes, just through the real /ecology admin command (network path — safe against the live tick
            // thread, unlike calling the test seam directly while RunAsync is running).
            admin.SendChat("/ecology set slime_hollow slime 1.0");

            await WaitUntilAsync(
                () => admin.Regions.TryGetValue("slime_hollow", out var updated)
                    && updated.Types[0].State == EcologyPopulationState.Depleted,
                admin);

            // No duplicates, and the OTHER two regions must not have been touched by this change.
            await PollForAsync(TimeSpan.FromMilliseconds(500), admin);
            Assert.Equal(2, admin.RegionMessageCount("slime_hollow")); // 1 login + exactly 1 update
            Assert.Equal(1, admin.RegionMessageCount("eastern_scrubland"));
            Assert.Equal(1, admin.RegionMessageCount("the_verge"));
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task RumorsCommand_EmitsOneLinePerAuthoredRegion()
    {
        using var database = await TestSqliteDatabase.CreateMigratedAsync();
        var port = GetFreeUdpPort();
        var options = CreateOptions(port, database.ConnectionString, admins: []);
        var server = new GameServer(options, new SqliteCharacterRepository(database.ConnectionString));
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            await Task.Delay(100);
            // A NON-admin client — D6b: /rumors is available to ALL players, not just admins.
            using var player = new EcologyClient("Player2");
            player.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => player.IsLoggedIn, player);

            // The login rumor already sent one system line; clear it so the wait below can only be satisfied by
            // the /rumors command's OWN lines.
            player.ClearSystemChat();

            player.SendChat("/rumors");
            await WaitUntilAsync(() => player.SystemChatLines.Count >= 3, player);

            Assert.Contains("Slime Hollow flourishes.", player.SystemChatLines);
            Assert.Contains("Eastern Scrubland flourishes.", player.SystemChatLines);
            Assert.Contains("The Verge flourishes.", player.SystemChatLines);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    private static ServerOptions CreateOptions(int port, string connectionString, string[] admins)
    {
        return new ServerOptions(
            port,
            TickRate,
            "region-ecology-wire-test",
            DatabaseProvider.Sqlite,
            connectionString,
            TestSqliteDatabase.MigrationsPath,
            64,
            64,
            BaseStepCooldownMs,
            15,
            30f,
            150,
            SpawnDistribution.Clustered,
            new HashSet<string>(admins, StringComparer.OrdinalIgnoreCase));
    }

    private static int GetFreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, params EcologyClient[] clients)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6);
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

        throw new TimeoutException("Timed out waiting for region-ecology wire integration condition.");
    }

    private static async Task PollForAsync(TimeSpan duration, params EcologyClient[] clients)
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

    // A minimal client tracking exactly what E4 replicates: the RegionEcologyMessage stream (keyed by region id,
    // last-write-wins — mirrors MmoClient.EcologyRegions) plus every "server" ChatBroadcastMessage (the login
    // rumor + /rumors lines). Mirrors TelegraphWireIntegrationTests.TelegraphClient.
    private sealed class EcologyClient : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly string _name;
        private NetPeer? _serverPeer;
        private bool _disposed;

        public EcologyClient(string name)
        {
            _name = name;
            _client = new NetManager(_listener) { AutoRecycle = false };
            _listener.PeerConnectedEvent += peer =>
            {
                _serverPeer = peer;
                Send(new ClientHelloMessage(_name), DeliveryMethod.ReliableOrdered);
                Send(new LoginRequestMessage(_name, _name), DeliveryMethod.ReliableOrdered);
            };
            _listener.NetworkReceiveEvent += OnNetworkReceive;
        }

        public Dictionary<string, RegionEcologyMessage> Regions { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Every arrival counted (not just the latest) so "exactly one update" is observable — Regions above only
        // keeps the LAST value per id, which can't distinguish "sent once" from "sent five times identically".
        private readonly Dictionary<string, int> _regionMessageCounts = new(StringComparer.OrdinalIgnoreCase);

        public int RegionMessageCount(string regionId) =>
            _regionMessageCounts.TryGetValue(regionId, out var count) ? count : 0;

        public List<string> SystemChatLines { get; } = [];

        public void ClearSystemChat() => SystemChatLines.Clear();

        public bool IsLoggedIn { get; private set; }

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

        public void SendChat(string text) =>
            Send(new ChatSendMessage(text), DeliveryMethod.ReliableOrdered);

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
                switch (message)
                {
                    case LoginResultMessage login:
                        IsLoggedIn = login.Accepted;
                        break;
                    case RegionEcologyMessage region:
                        Regions[region.RegionId] = region;
                        _regionMessageCounts[region.RegionId] = RegionMessageCount(region.RegionId) + 1;
                        break;
                    case ChatBroadcastMessage chat when chat.Sender == "server":
                        SystemChatLines.Add(chat.Text);
                        break;
                    case WorldSnapshotMessage snapshot:
                        Send(new SnapshotAckMessage(snapshot.SnapshotSequence), DeliveryMethod.Sequenced);
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
