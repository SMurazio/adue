using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using LiteNetLib;
using Mmo.Server.Configuration;
using Mmo.Server.Data;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Mmo.Server.Tests;

// COMBAT-LAG investigation harness. Drives a REAL GameServer (real UDP loopback, real tick loop) through the actual
// combat path — a player attacks a slime at the ~600ms attack cadence WHILE moving, so the slime aggros, chases a
// MOVING target, attacks back, and dies/respawns repeatedly. It reads the server's OWN per-tick metrics back over the
// wire (/metrics → FormatWindowSummary) so the per-tick cost is the ground truth from the live tick loop, NOT a
// re-implemented model. Prints idle-vs-combat tickMs avg/max + the per-category budget so a combat hot path can be
// pinned with numbers. Not an assertion suite (the budgets are environment-dependent); it surfaces deltas.
// QUARANTINED: non-asserting measurement harness — excluded from the default gate via Category=Measure (run-checks
// filters Category!=Measure). Run on demand: dotnet test --filter "Category=Measure".
[Trait("Category", "Measure")]
public sealed class CombatLagMeasureTests
{
    private readonly ITestOutputHelper _out;
    public CombatLagMeasureTests(ITestOutputHelper output) => _out = output;

    private const int TickRate = 20;
    private const int BaseStepCooldownMs = 140;

    [Fact]
    public async Task Measure_TickCost_IdleVsCombat()
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
            using var admin = new CombatClient("Admin");
            admin.Connect(port, options.ConnectionKey);
            await WaitUntilAsync(() => admin.IsLoggedIn && admin.OwnNetworkId != 0, TimeSpan.FromSeconds(6), admin);

            // Spawn a slime at the admin's own tile (clustered ⇒ instantly in AOI ⇒ the EntitySpawn arrives, and the
            // admin is adjacent so its attacks land and the slime instantly aggros it).
            admin.SendChat("/monster");
            await WaitUntilAsync(() => admin.TryGetMonsterSpawn(out _), TimeSpan.FromSeconds(6), admin);

            // ---- Phase A: IDLE baseline. No attacks, no movement; let the slime roam and the server settle. ----
            await PumpForAsync(TimeSpan.FromSeconds(3), admin); // warm up + let the slime roam.
            admin.SendChat("/metrics");
            await WaitUntilAsync(() => admin.LatestWindowMetrics(5) is not null, TimeSpan.FromSeconds(3), admin);
            var idle = admin.LatestWindowMetrics(5)!;
            admin.ClearMetrics();

            // ---- Phase B: COMBAT. Attack at ~600ms cadence while WANDERING so the slime chases a moving target,
            // attacks back, and dies/respawns repeatedly (5 hits at 20 dmg kills the 100-HP slime; respawn 5s). ----
            var combatStart = DateTimeOffset.UtcNow;
            var attackSeq = 1u;
            var inputSeq = 1u;
            var nextAttackAt = DateTimeOffset.UtcNow;
            var rng = new Random(99);
            while (DateTimeOffset.UtcNow - combatStart < TimeSpan.FromSeconds(8))
            {
                var now = DateTimeOffset.UtcNow;
                if (now >= nextAttackAt)
                {
                    // Aim in a random direction (covers the slime wherever it roams/chases around the admin).
                    var aim = AimAngle.Quantize(rng.NextDouble() * 2d * Math.PI);
                    admin.SendAttack(attackSeq++, aim);
                    nextAttackAt = now + TimeSpan.FromMilliseconds(620);
                }

                // Wander: send a continuous move-intent each pump so the slime chases a MOVING player (the prime
                // suspect — a roam harness only ever chased a stationary one).
                var ang = rng.NextDouble() * 2d * Math.PI;
                admin.SendMove(inputSeq++, (float)Math.Cos(ang), (float)Math.Sin(ang), 1f / TickRate);

                admin.Poll();
                await Task.Delay(15);
            }

            admin.SendChat("/metrics");
            await WaitUntilAsync(() => admin.LatestWindowMetrics(5) is not null, TimeSpan.FromSeconds(3), admin);
            var combat = admin.LatestWindowMetrics(5)!;

            _out.WriteLine("=== COMBAT-LAG: idle vs combat server tick cost (real GameServer, real tick loop) ===");
            _out.WriteLine($"  IDLE  : {idle.Raw}");
            _out.WriteLine($"  COMBAT: {combat.Raw}");
            _out.WriteLine("");
            _out.WriteLine($"  tickMs avg : idle {idle.TickAvgMs:0.000}  ->  combat {combat.TickAvgMs:0.000}  (x{Ratio(idle.TickAvgMs, combat.TickAvgMs):0.0})");
            _out.WriteLine($"  tickMs max : idle {idle.TickMaxMs:0.000}  ->  combat {combat.TickMaxMs:0.000}  (x{Ratio(idle.TickMaxMs, combat.TickMaxMs):0.0})");
            _out.WriteLine($"  budget move: idle {idle.BudgetMove:0.000}  ->  combat {combat.BudgetMove:0.000}");
            _out.WriteLine($"  budget aoi : idle {idle.BudgetAoi:0.000}  ->  combat {combat.BudgetAoi:0.000}");
            _out.WriteLine($"  budget ser : idle {idle.BudgetSer:0.000}  ->  combat {combat.BudgetSer:0.000}");
            _out.WriteLine($"  budget net : idle {idle.BudgetNet:0.000}  ->  combat {combat.BudgetNet:0.000}");
            _out.WriteLine($"  budget othr: idle {idle.BudgetOther:0.000}  ->  combat {combat.BudgetOther:0.000}");
            _out.WriteLine($"  damage events seen by admin (combat): {admin.DamageEventCount}");
            _out.WriteLine($"  drift max  : idle {idle.DriftMaxMs:0.00}  ->  combat {combat.DriftMaxMs:0.00}");

            // ---- Phase C: PACK. Spawn many slimes clustered on the admin and pull them ALL into chase at once, so
            // per-monster chase + per-attack-multi-victim cost compounds. This is the scale a 1-slime harness misses. ----
            admin.ClearMetrics();
            for (var i = 0; i < 40; i++)
            {
                admin.SendChat("/monster");
            }

            await PumpForAsync(TimeSpan.FromSeconds(1), admin);

            var packStart = DateTimeOffset.UtcNow;
            while (DateTimeOffset.UtcNow - packStart < TimeSpan.FromSeconds(8))
            {
                var now = DateTimeOffset.UtcNow;
                if (now >= nextAttackAt)
                {
                    var aim = AimAngle.Quantize(rng.NextDouble() * 2d * Math.PI);
                    admin.SendAttack(attackSeq++, aim);
                    nextAttackAt = now + TimeSpan.FromMilliseconds(620);
                }

                var ang = rng.NextDouble() * 2d * Math.PI;
                admin.SendMove(inputSeq++, (float)Math.Cos(ang), (float)Math.Sin(ang), 1f / TickRate);
                admin.Poll();
                await Task.Delay(15);
            }

            admin.SendChat("/metrics");
            await WaitUntilAsync(() => admin.LatestWindowMetrics(5) is not null, TimeSpan.FromSeconds(3), admin);
            var pack = admin.LatestWindowMetrics(5)!;
            _out.WriteLine("");
            _out.WriteLine($"  PACK (40 slimes): {pack.Raw}");
            _out.WriteLine($"  tickMs avg/max  : {pack.TickAvgMs:0.000}/{pack.TickMaxMs:0.000}  budget move/aoi/ser/net/other={pack.BudgetMove:0.000}/{pack.BudgetAoi:0.000}/{pack.BudgetSer:0.000}/{pack.BudgetNet:0.000}/{pack.BudgetOther:0.000}");
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    private static double Ratio(double a, double b) => a <= 0.0001 ? double.NaN : b / a;

    private static ServerOptions CreateOptions(int port, string connectionString, string[] admins)
    {
        return new ServerOptions(
            port,
            TickRate,
            "combat-lag-test",
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, params CombatClient[] clients)
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

        throw new TimeoutException("Timed out waiting for combat-lag measurement condition.");
    }

    private static async Task PumpForAsync(TimeSpan duration, params CombatClient[] clients)
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

    private sealed record WindowMetrics(
        string Raw,
        double TickAvgMs,
        double TickMaxMs,
        double DriftMaxMs,
        double BudgetMove,
        double BudgetAoi,
        double BudgetSer,
        double BudgetNet,
        double BudgetPersist,
        double BudgetOther);

    private sealed class CombatClient : IDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;
        private readonly string _name;
        private NetPeer? _serverPeer;
        private bool _disposed;

        public CombatClient(string name)
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

        public List<EntitySpawnMessage> KnownSpawns { get; } = [];
        public List<WindowMetrics> Metrics { get; } = [];
        public bool IsLoggedIn { get; private set; }
        public uint OwnNetworkId { get; private set; }
        public int DamageEventCount { get; private set; }

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

        public void SendChat(string text) => Send(new ChatSendMessage(text), DeliveryMethod.ReliableOrdered);

        public void SendAttack(uint sequence, ushort aimAngle) =>
            Send(new AttackMessage(sequence, AttackKind.MeleeCone, aimAngle, AuthoredTick: 0u), DeliveryMethod.ReliableOrdered);

        public void SendMove(uint inputSeq, float dirX, float dirY, float dt) =>
            Send(new MoveIntentMessage(inputSeq, dirX, dirY, dt), DeliveryMethod.ReliableOrdered);

        public bool TryGetMonsterSpawn(out EntitySpawnMessage spawn)
        {
            spawn = KnownSpawns.FirstOrDefault(s => s.Kind == EntityKind.Monster)!;
            return spawn is not null;
        }

        public WindowMetrics? LatestWindowMetrics(int windowSeconds)
            => Metrics.LastOrDefault(m => m.Raw.StartsWith($"metrics {windowSeconds}s:", StringComparison.Ordinal));

        public void ClearMetrics() => Metrics.Clear();

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
                    case EntitySpawnMessage spawn:
                        KnownSpawns.Add(spawn);
                        if (spawn.DisplayName == _name && spawn.Kind == EntityKind.Player)
                        {
                            OwnNetworkId = spawn.NetworkId;
                        }

                        break;
                    case DamageEventMessage:
                        DamageEventCount++;
                        break;
                    case ChatBroadcastMessage chat:
                        if (chat.Text.StartsWith("metrics ", StringComparison.Ordinal) && TryParseWindow(chat.Text, out var w))
                        {
                            Metrics.Add(w);
                        }

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

        private static bool TryParseWindow(string line, out WindowMetrics metrics)
        {
            metrics = default!;
            // tickMs avg/max=0.05/0.20 ; driftMs avg/max=.. ; budgetMs move/aoi/ser/net/persist/other=a/b/c/d/e/f
            var tick = Regex.Match(line, @"tickMs avg/max=([\d.]+)/([\d.]+)");
            var drift = Regex.Match(line, @"driftMs avg/max=([\d.]+)/([\d.]+)");
            var budget = Regex.Match(line, @"budgetMs move/aoi/ser/net/persist/other=([\d.]+)/([\d.]+)/([\d.]+)/([\d.]+)/([\d.]+)/([\d.]+)");
            if (!tick.Success || !budget.Success)
            {
                return false;
            }

            double P(Match m, int g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
            metrics = new WindowMetrics(
                line,
                P(tick, 1), P(tick, 2),
                drift.Success ? P(drift, 2) : 0,
                P(budget, 1), P(budget, 2), P(budget, 3), P(budget, 4), P(budget, 5), P(budget, 6));
            return true;
        }

        private void Send(IProtocolMessage message, DeliveryMethod deliveryMethod)
        {
            _serverPeer?.Send(ProtocolCodec.Encode(message), deliveryMethod);
        }
    }
}
