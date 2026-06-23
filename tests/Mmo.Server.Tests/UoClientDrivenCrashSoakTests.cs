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

// CRASH1 regression: the live server "seems to be crashing" during UoClientDriven play. The 120c/30s stress gate
// is clean but runs the DEFAULT (held-intent) movement path and NEVER exercises the UO-mode server code (the per-step
// StepCommitRequest stream, the MovementMode toggle, the client-driven held-intent skip). This soak drives a REAL
// GameServer over loopback through exactly the UO-mode surfaces the prime suspects flag, and asserts the server
// loop never faults (RunAsync stays running, the runtime-fault count stays 0) and that a fresh client can still
// log in after the burst — i.e. the server did not crash, hang, or wedge under UO load.
//
// Covered UO-mode edge cases (per the CRASH1 prime-suspect list):
//   * a per-step StepCommitRequest stream AT/ABOVE cadence (S103 one-shot code hit ~7+/s),
//   * 180 deg reversals / settle changing cadence mid-stream,
//   * release (Moving=false) interleaved with banked commits,
//   * rapid MovementMode toggle on/off while moving,
//   * a client-driven session DISCONNECTING mid-burst (the disconnect handler runs OUTSIDE the runtime guard),
//   * commits arriving same-tick (sequence dedupe under a tight stream).
public sealed class UoClientDrivenCrashSoakTests
{
    private const int TickRate = 20;
    private const int BaseStepCooldownMs = 140;

    [Fact]
    public async Task UoMode_RapidCommitStream_ReversalsReleaseToggleAndDisconnect_DoesNotFaultServer()
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

            // --- Burst client: drive the full UO-mode surface, then disconnect MID-BURST. ---
            using (var driver = new RawClient("UoDriver"))
            {
                driver.Connect(port, options.ConnectionKey);
                await WaitUntilAsync(() => driver.IsLoggedIn && driver.OwnNetworkId != 0, driver);

                // Enter UO mode (server stops auto-pacing; entity advances ONLY on commits).
                driver.Send(new MovementModeMessage(ClientDriven: true));

                uint seq = 0;
                var directions = new[]
                {
                    Direction8.E, Direction8.E, Direction8.W, Direction8.W, // 180 reversals
                    Direction8.N, Direction8.S, Direction8.NE, Direction8.SW,
                };

                // Per-step commit stream at/above cadence, interleaved with MoveIntents, reversals,
                // releases, same-tick duplicate sequences, and rapid mode toggles.
                for (var round = 0; round < 60; round++)
                {
                    var dir = directions[round % directions.Length];

                    // Held intent (facing/keepalive) — shares the move sequence cursor with commits.
                    driver.Send(new MoveIntentMessage(++seq, Moving: true, dir));

                    // A burst of per-step commits (above cadence — many will be rejected by the floor; the
                    // server must reject them cleanly, never throw on the borrow math).
                    driver.Send(new StepCommitRequestMessage(++seq, dir));
                    driver.Send(new StepCommitRequestMessage(++seq, dir));
                    driver.Send(new StepCommitRequestMessage(++seq, dir));

                    // Same-tick duplicate/stale sequences (cursor dedupe under a tight stream).
                    driver.Send(new StepCommitRequestMessage(seq, dir));
                    driver.Send(new StepCommitRequestMessage(seq - 1, dir));

                    if (round % 7 == 0)
                    {
                        // Release (banked commits) mid-stream.
                        driver.Send(new MoveIntentMessage(++seq, Moving: false, dir));
                    }

                    if (round % 5 == 0)
                    {
                        // Rapid mode toggle off/on while moving.
                        driver.Send(new MovementModeMessage(ClientDriven: false));
                        driver.Send(new MovementModeMessage(ClientDriven: true));
                    }

                    driver.Poll();
                    await Task.Delay(5); // sub-cadence: commits arrive faster than the step cooldown
                }

                // Disconnect MID-BURST (the prime-suspect disconnect-handler path, which runs outside the
                // tick runtime guard) without a graceful stop sequence.
                driver.HardDisconnect();
            }

            // Give the server several ticks to process the disconnect + drain.
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(10);
            }

            // --- The server must still be alive: it did not crash, hang, or fault. ---
            Assert.False(serverTask.IsFaulted, FaultMessage(serverTask));
            Assert.False(serverTask.IsCompleted, "Server loop exited unexpectedly during/after the UO burst.");

            // And a fresh client can still log in and move — proves the loop is still ticking, not wedged.
            using var probe = new RawClient("Probe");
            probe.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => probe.IsLoggedIn && probe.OwnNetworkId != 0 && probe.OwnTile.HasValue, probe);
            var startTile = probe.OwnTile!.Value;
            probe.Send(new MoveIntentMessage(1, Moving: true, Direction8.E)); // server-paced: must advance
            await WaitUntilAsync(() => probe.OwnTile!.Value != startTile, probe);
            Assert.NotEqual(startTile, probe.OwnTile!.Value);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    private static string FaultMessage(Task serverTask)
    {
        return serverTask.IsFaulted
            ? $"Server loop faulted under UO load: {serverTask.Exception}"
            : "Server loop faulted under UO load.";
    }

    private static ServerOptions CreateOptions(int port, string connectionString)
    {
        return new ServerOptions(
            port,
            TickRate,
            "uo-crash-soak-test",
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

    private static async Task WaitUntilAsync(Func<bool> condition, params RawClient[] clients)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
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

        throw new TimeoutException("Timed out waiting for UO crash-soak condition.");
    }

    // Minimal raw protocol client: tracks login + own tile from the snapshot stream, acks snapshots, exposes a
    // raw Send and a HARD (non-graceful) disconnect to model a client dropping mid-burst.
    private sealed class RawClient : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly string _name;
        private NetPeer? _serverPeer;
        private bool _disposed;

        public RawClient(string name)
        {
            _name = name;
            _client = new NetManager(_listener) { AutoRecycle = false };
            _listener.PeerConnectedEvent += peer =>
            {
                _serverPeer = peer;
                Send(new ClientHelloMessage(_name));
                Send(new LoginRequestMessage(_name, _name));
            };
            _listener.NetworkReceiveEvent += OnNetworkReceive;
        }

        public bool IsLoggedIn { get; private set; }
        public uint OwnNetworkId { get; private set; }
        public TileCoord? OwnTile { get; private set; }

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

        public void Send(IProtocolMessage message)
        {
            _serverPeer?.Send(ProtocolCodec.Encode(message), DeliveryMethod.ReliableOrdered);
        }

        // Drop the transport without a graceful Disconnect handshake, so the server observes a mid-burst
        // disconnect (timeout/reset) while commits are still in flight.
        public void HardDisconnect()
        {
            if (_disposed)
            {
                return;
            }

            _client.Stop();
            _disposed = true;
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
                switch (message)
                {
                    case LoginResultMessage login:
                        IsLoggedIn = login.Accepted;
                        OwnTile = login.Tile;
                        break;
                    case EntitySpawnMessage spawn:
                        if (spawn.DisplayName == _name)
                        {
                            OwnNetworkId = spawn.NetworkId;
                            OwnTile = spawn.Tile;
                        }

                        break;
                    case WorldSnapshotMessage snapshot:
                        foreach (var state in snapshot.Entities)
                        {
                            if (state.NetworkId == OwnNetworkId)
                            {
                                OwnTile = state.Tile;
                            }
                        }

                        _serverPeer?.Send(ProtocolCodec.Encode(new SnapshotAckMessage(snapshot.SnapshotSequence)), DeliveryMethod.Sequenced);
                        break;
                }
            }
            finally
            {
                reader.Recycle();
            }
        }
    }
}
