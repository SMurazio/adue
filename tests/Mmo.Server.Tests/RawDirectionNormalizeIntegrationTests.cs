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

// CONTINUOUS MIGRATION — Phase 3 Followup A (todo/N-phase3-followups.md): a SERVER-PATH guard for
// GameServer.HandleMoveIntent's `rawDir.Normalized()`. That single line stops a hostile raw MoveIntent — a
// diagonal `(1,1)` (√2 magnitude) or a `(10,0)` (10× magnitude) — from becoming a SPEED EXPLOIT: the continuous
// integrator scales the passed vector by SpeedUnitsPerSecond WITHOUT normalizing, so the server MUST normalize the
// client's raw direction first. No existing test exercised it on the real receive path (the integrator-level tests
// feed already-unit vectors), so a refactor dropping the normalize would leave every test green while reopening the
// exploit. This drives RAW (non-unit) MoveIntents over the real socket and asserts the integrated displacement
// equals the cardinal `(1,0)` case — magnitude neither boosts (√2 / 10×) nor throttles the speed.
//
// Robustness: the three runs use IDENTICAL inputs/dt/timing against a fresh server each, so the anti-speedhack
// dt-budget throttling and the Q12.4 snapshot quantization apply identically to all three — the comparison is
// apples-to-apples (we compare the raw cases' displacement to the cardinal case's, not to an absolute distance).
public sealed class RawDirectionNormalizeIntegrationTests
{
    [Fact]
    public async Task RawNonUnitEastMoveIntent_IntegratesSameDistanceAsCardinalEast()
    {
        // The core SPEED-EXPLOIT guard: a raw (10,0) MoveIntent (10× magnitude) must integrate the SAME distance as
        // the cardinal (1,0) unit — i.e. `rawDir.Normalized()` strips the magnitude so it is neither boosted nor
        // throttled. Both cases drive purely EAST along the IDENTICAL terrain path from the same spawn under identical
        // timing, so the comparison is robust to terrain and the budget throttling (which apply equally to both); a
        // dropped normalize would make the (10,0) case travel ~10× as far and blow the tolerance.
        var cardinalEast = await MeasureDisplacementAsync(1f, 0f);
        var bigEast = await MeasureDisplacementAsync(10f, 0f);

        // The cardinal must actually have moved (so the comparison is meaningful, not "all zero").
        Assert.True(cardinalEast > 0.5d, $"cardinal east did not move enough to compare: {cardinalEast}");

        // The two runs are throttled by the anti-speedhack wall-clock dt-BUDGET to ≈ real-elapsed-time distance, and the
        // budget refill is timing-sensitive under concurrent test load — so we assert a ROBUST BAND rather than exact
        // equality: the raw (10,0) must NOT be magnitude-boosted (a dropped normalize gives ≈10× ⇒ ~76 u, blowing the
        // upper bound by an order of magnitude) and must NOT be throttled to a crawl. A normalized (10,0) walks at the
        // SAME speed as cardinal (1,0), so it lands well inside this band; only a real magnitude exploit escapes it.
        Assert.True(bigEast <= (cardinalEast * 1.5d) + 1.0d, $"raw (10,0) was magnitude-BOOSTED vs cardinal: bigEast={bigEast}, cardinalEast={cardinalEast}");
        Assert.True(bigEast >= (cardinalEast * 0.5d), $"raw (10,0) was magnitude-THROTTLED vs cardinal: bigEast={bigEast}, cardinalEast={cardinalEast}");
    }

    [Fact]
    public async Task RawDiagonalMoveIntent_IsNotFasterThanCardinal_NoSqrt2Boost()
    {
        // The diagonal half of the guard: a raw (1,1) MoveIntent (√2 magnitude) must NOT travel faster than the
        // cardinal (1,0) — `rawDir.Normalized()` stops the √2 boost (a diagonal would otherwise cover √2× the ground).
        // Measured as the PER-AXIS east displacement: a normalized diagonal advances east at speed/√2, so its east
        // displacement is ~cardinal/√2 — and crucially NOT >= cardinal (which an un-normalized √2 boost would produce,
        // since (1,1) un-normalized scales BOTH axes by the full speed). Per-axis east is terrain-robust (both runs
        // start at the same spawn and the +Y drift only diverges the paths slightly over a short burst).
        var cardinalEast = await MeasureEastDisplacementAsync(1f, 0f);
        var diagonalEast = await MeasureEastDisplacementAsync(1f, 1f);

        Assert.True(cardinalEast > 0.5d, $"cardinal east did not move enough to compare: {cardinalEast}");
        // The diagonal's EAST component must be strictly LESS than the cardinal's (no √2 boost). An un-normalized
        // (1,1) would make diagonalEast ≈ cardinalEast (full speed on X too) or more — this catches that regression.
        Assert.True(
            diagonalEast < cardinalEast - 0.2d,
            $"raw diagonal was not slower per-axis than cardinal (√2 boost not normalized): diagonalEast={diagonalEast}, cardinalEast={cardinalEast}");
    }

    // The per-AXIS east (X) displacement of the player from spawn after the fixed raw-input run — used by the diagonal
    // guard (a normalized diagonal advances east at speed/√2; an un-normalized √2 boost would not slow the east axis).
    private static async Task<double> MeasureEastDisplacementAsync(float dirX, float dirY)
    {
        var (spawnX, endX) = await MeasureAsync(dirX, dirY, (spawn, end) => (spawn.X, end.X));
        return Math.Abs(endX - spawnX);
    }

    // Spin up a fresh server, log in one client, drive a fixed run of raw MoveIntents in the given raw direction, and
    // return the total displacement magnitude of the player from its spawn (read off the continuous snapshot Position).
    private static async Task<double> MeasureDisplacementAsync(float dirX, float dirY)
    {
        var (zero, dist) = await MeasureAsync(dirX, dirY, (spawn, end) =>
        {
            var dx = end.X - spawn.X;
            var dy = end.Y - spawn.Y;
            return (0d, Math.Sqrt((dx * dx) + (dy * dy)));
        });
        _ = zero;
        return dist;
    }

    // Shared run: spin up a server, log in, drive the fixed raw-input burst, and project (spawn, end) positions.
    private static async Task<(double A, double B)> MeasureAsync(
        float dirX, float dirY, Func<WorldVector, WorldVector, (double A, double B)> project)
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
            using var client = new MoveClient("Mover");
            client.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => client.IsLoggedIn && client.OwnNetworkId != 0 && client.HasPosition, client);

            var spawn = client.OwnPosition;

            // Drive ~1.5s of held raw input (one input every ~25ms), letting the anti-speedhack budget refill in real
            // time so the displacement is budget-limited but IDENTICAL across the three runs.
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(1500);
            while (DateTimeOffset.UtcNow < deadline)
            {
                client.SendRawMove(dirX, dirY, 1f / 20f);
                client.Poll();
                await Task.Delay(25);
            }

            // Let the final snapshots land.
            await PollForAsync(TimeSpan.FromMilliseconds(150), client);

            var end = client.OwnPosition;
            return project(spawn, end);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    private static ServerOptions CreateOptions(int port, string connectionString)
    {
        return new ServerOptions(
            port,
            20,
            "raw-dir-normalize-test",
            DatabaseProvider.Sqlite,
            connectionString,
            TestSqliteDatabase.MigrationsPath,
            64,
            64,
            250,
            15,
            30f,
            150,
            SpawnDistribution.Clustered,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        {
            ResourceNodeDensityTilesPerNode = 0,
        };
    }

    private static int GetFreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, params MoveClient[] clients)
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

        throw new TimeoutException("Timed out waiting for raw-dir-normalize integration condition.");
    }

    private static async Task PollForAsync(TimeSpan duration, params MoveClient[] clients)
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

    private sealed class MoveClient : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly string _name;
        private NetPeer? _serverPeer;
        private uint _moveSequence;

        public MoveClient(string name)
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

        public bool IsLoggedIn { get; private set; }
        public uint OwnNetworkId { get; private set; }
        public bool HasPosition { get; private set; }
        public WorldVector OwnPosition { get; private set; }

        public void Connect(int port, string key)
        {
            _client.Start();
            _client.Connect("127.0.0.1", port, key);
        }

        public void Poll() => _client.PollEvents();

        // Send a RAW (un-normalized) MoveIntent exactly as a tampering client could — the server must normalize it.
        public void SendRawMove(float dirX, float dirY, float dtSeconds)
        {
            Send(new MoveIntentMessage(++_moveSequence, dirX, dirY, dtSeconds), DeliveryMethod.Unreliable);
        }

        public void Dispose() => _client.Stop();

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
                    case EntitySpawnMessage spawn when spawn.DisplayName == _name:
                        OwnNetworkId = spawn.NetworkId;
                        OwnPosition = WorldVector.FromTile(spawn.Tile);
                        HasPosition = true;
                        break;
                    case WorldSnapshotMessage snapshot:
                        Send(new SnapshotAckMessage(snapshot.SnapshotSequence), DeliveryMethod.Sequenced);
                        foreach (var entity in snapshot.Entities)
                        {
                            if (entity.NetworkId == OwnNetworkId)
                            {
                                OwnPosition = entity.Position;
                                HasPosition = true;
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
            if (_serverPeer is null)
            {
                return;
            }

            _serverPeer.Send(ProtocolCodec.Encode(message), deliveryMethod);
        }
    }
}
