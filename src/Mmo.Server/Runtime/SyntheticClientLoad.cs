using LiteNetLib;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;

namespace Mmo.Server.Runtime;

public sealed class SyntheticClientLoad : IDisposable
{
    private readonly List<SyntheticClient> _clients = [];

    private DateTimeOffset _startedAt;
    private DateTimeOffset _endsAt;
    private TimeSpan _lastPollElapsed;
    private string _lastSummary = "stress idle.";

    public int Spawned => _clients.Count;
    public int Authenticated => _clients.Count(client => client.IsAuthenticated);
    public long SnapshotsReceived { get; private set; }
    public long ServerErrors { get; private set; }
    public long NetworkErrors { get; private set; }

    public bool IsRunning => _clients.Count > 0;

    public void Start(int clientCount, TimeSpan duration, int serverPort, string connectionKey)
    {
        Stop();

        _startedAt = DateTimeOffset.UtcNow;
        _endsAt = _startedAt + duration;
        _lastPollElapsed = TimeSpan.Zero;
        SnapshotsReceived = 0;
        ServerErrors = 0;
        NetworkErrors = 0;

        var prefix = $"Test{_startedAt:HHmmss}";
        for (var i = 0; i < clientCount; i++)
        {
            var client = new SyntheticClient(i, $"{prefix}{i + 1:000}", serverPort, connectionKey, this);
            client.Start();
            _clients.Add(client);
        }
    }

    public void Poll()
    {
        if (!IsRunning)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var elapsed = now - _startedAt;
        // Manual-mode clients are pumped from this (server-loop) thread; pass the elapsed ms since the
        // previous poll so each NetManager.ManualUpdate advances its timers correctly. This poll runs
        // every server-loop iteration (far faster than the tick rate), so deltas stay small.
        var deltaTimeMs = (int)Math.Clamp((elapsed - _lastPollElapsed).TotalMilliseconds, 0, int.MaxValue);
        _lastPollElapsed = elapsed;
        foreach (var client in _clients)
        {
            client.Poll(elapsed, deltaTimeMs);
        }

        if (now >= _endsAt)
        {
            Stop();
        }
    }

    public string Status()
    {
        if (!IsRunning)
        {
            return _lastSummary;
        }

        var now = DateTimeOffset.UtcNow;
        var remaining = _endsAt > now ? _endsAt - now : TimeSpan.Zero;
        return $"stress running: clients={Spawned}, authed={Authenticated}, snapshots={SnapshotsReceived}, errors={ServerErrors + NetworkErrors}, remaining={FormatDuration(remaining)}.";
    }

    public string Stop()
    {
        if (!IsRunning)
        {
            return _lastSummary;
        }

        var spawned = Spawned;
        var authenticated = Authenticated;
        var snapshots = SnapshotsReceived;
        var errors = ServerErrors + NetworkErrors;
        var elapsed = DateTimeOffset.UtcNow - _startedAt;

        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _clients.Clear();
        _lastSummary = $"stress stopped: clients={spawned}, authed={authenticated}, snapshots={snapshots}, errors={errors}, elapsed={FormatDuration(elapsed)}.";
        return _lastSummary;
    }

    public void Dispose()
    {
        Stop();
    }

    private void RecordSnapshot()
    {
        SnapshotsReceived++;
    }

    private void RecordServerError()
    {
        ServerErrors++;
    }

    private void RecordNetworkError()
    {
        NetworkErrors++;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalSeconds < 60
            ? $"{duration.TotalSeconds:0.#}s"
            : $"{duration.TotalMinutes:0.#}m";
    }

    private sealed class SyntheticClient : IDisposable
    {
        // REALISTIC WANDER: bots walk toward a random INTERIOR waypoint on a CONTINUOUS heading, arrive, pick a new
        // one, and occasionally pause — like players roaming a zone — instead of snapping to a random octant every
        // second (robotic 90° turns that read as janky and don't represent real clients). Position is dead-reckoned
        // locally (approximate; it only needs to keep the wander in-bounds), so no snapshot parsing is required.
        private const double WanderMinCoord = 24d;    // interior of the 128-tile map so bots don't pile at the edges
        private const double WanderMaxCoord = 104d;
        private const double NominalSpeedUnitsPerSecond = 4d; // ~1000/250ms step cooldown; dead-reckon pacing only
        private const double WaypointReachRadius = 1.5d;
        private const double ArrivalIdleChance = 0.25d;       // ~a quarter of arrivals pause before moving on

        private static readonly TimeSpan MinIdle = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan MaxIdle = TimeSpan.FromMilliseconds(2200);

        private readonly string _name;
        private readonly int _serverPort;
        private readonly string _connectionKey;
        private readonly SyntheticClientLoad _owner;
        private readonly Random _random;
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;

        // Send cadence — mirror a REAL client's per-frame input: ~60 Hz (16 ms) with the REAL elapsed dt (computed at
        // the send site), NOT a coarse fixed-dt tick. The server loop polls the load ~every 2 ms, so 16 ms is reachable.
        // Why fine + real-dt: the server integrates each MoveIntent ON RECEIVE and clamps it to a per-tick real-time dt
        // BUDGET. A coarse 50 ms cadence with a FIXED 50 ms dt lands ONE 0.05-unit jump per tick at a random phase vs the
        // 50 ms tick (the timers drift) → per-tick motion aliases (a single big step at a random time) → jerky snapshots
        // no interp buffer can smooth. Fine + real-dt = several small steps per tick that sum to real-elapsed → regular,
        // smooth motion like a real client. (Earlier bug: a 500 ms cadence made the bot CRAWL at ~1/10 speed.)
        private static readonly TimeSpan MoveIntentKeepalive = TimeSpan.FromMilliseconds(16);

        private NetPeer? _serverPeer;
        private bool _disposed;
        private uint _inputSequence;
        private TimeSpan _nextKeepaliveAt;
        private TimeSpan _lastMoveSendElapsed;
        private bool _intentMoving;

        // Dead-reckoned wander state (seeded on the first authenticated poll).
        private bool _wanderSeeded;
        private double _estX;
        private double _estY;
        private double _targetX;
        private double _targetY;
        private TimeSpan _idleUntil;

        public SyntheticClient(int id, string name, int serverPort, string connectionKey, SyntheticClientLoad owner)
        {
            _name = name;
            _serverPort = serverPort;
            _connectionKey = connectionKey;
            _owner = owner;
            _random = new Random(Environment.TickCount + id);
            _client = new NetManager(_listener)
            {
                AutoRecycle = false,
                DisconnectTimeout = 15000
            };

            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += (_, _) =>
            {
                _serverPeer = null;
                IsAuthenticated = false;
            };
            _listener.NetworkErrorEvent += (_, _) => _owner.RecordNetworkError();
            _listener.NetworkReceiveEvent += OnNetworkReceive;
        }

        public bool IsAuthenticated { get; private set; }

        public void Start()
        {
            // Manual mode: no per-client background thread. The owner pumps ManualUpdate + PollEvents
            // from the server loop (see SyntheticClientLoad.Poll), so client count no longer scales
            // OS-thread count (S45).
            _client.Start(System.Net.IPAddress.Any, System.Net.IPAddress.IPv6Any, 0, manualMode: true);
            _client.Connect("127.0.0.1", _serverPort, _connectionKey);
            _nextKeepaliveAt = TimeSpan.FromMilliseconds(_random.Next(0, 500));
        }

        // A fresh random interior waypoint for the bot to walk toward.
        private void PickWaypoint()
        {
            _targetX = WanderMinCoord + (_random.NextDouble() * (WanderMaxCoord - WanderMinCoord));
            _targetY = WanderMinCoord + (_random.NextDouble() * (WanderMaxCoord - WanderMinCoord));
        }

        public void Poll(TimeSpan elapsed, int deltaTimeMs)
        {
            if (_disposed)
            {
                return;
            }

            // Manual mode: drive the library's timers (handshake, reliable resends, ping, timeout) and
            // then dispatch queued events. Both are required every poll in the absence of a background
            // thread.
            _client.ManualUpdate(deltaTimeMs);
            _client.PollEvents();
            if (!IsAuthenticated || _serverPeer is null)
            {
                return;
            }

            if (!_wanderSeeded)
            {
                _wanderSeeded = true;
                // Seed the dead-reckoned position to the central spawn belt (bots spawn on the distributed spawn
                // tiles); it only needs to be roughly right — steering toward interior waypoints self-corrects.
                _estX = 32d + (_random.NextDouble() * 64d);
                _estY = 32d + (_random.NextDouble() * 64d);
                PickWaypoint();
            }

            // Arrived at the waypoint (and not mid-pause)? Occasionally pause like a player, then head somewhere new.
            if (elapsed >= _idleUntil)
            {
                var toTargetX = _targetX - _estX;
                var toTargetY = _targetY - _estY;
                if (((toTargetX * toTargetX) + (toTargetY * toTargetY)) < (WaypointReachRadius * WaypointReachRadius))
                {
                    if (_random.NextDouble() < ArrivalIdleChance)
                    {
                        var idleSpan = (MaxIdle - MinIdle).Ticks;
                        _idleUntil = elapsed + MinIdle + TimeSpan.FromTicks((long)(_random.NextDouble() * idleSpan));
                    }

                    PickWaypoint();
                }
            }

            // Steer on a CONTINUOUS heading toward the waypoint (idle → no heading). Dead-reckon the estimate by the
            // real poll dt so waypoints get reached at roughly the true pace.
            var desiredMoving = elapsed >= _idleUntil;
            var desiredDir = WorldVector.Zero;
            if (desiredMoving)
            {
                var dx = _targetX - _estX;
                var dy = _targetY - _estY;
                var dist = Math.Sqrt((dx * dx) + (dy * dy));
                if (dist > 1e-6d)
                {
                    var inv = 1d / dist;
                    desiredDir = new WorldVector(dx * inv, dy * inv);
                    var stepDt = Math.Clamp(deltaTimeMs / 1000d, 0d, 0.1d);
                    _estX += desiredDir.X * NominalSpeedUnitsPerSecond * stepDt;
                    _estY += desiredDir.Y * NominalSpeedUnitsPerSecond * stepDt;
                }
                else
                {
                    desiredMoving = false;
                }
            }

            // Send on a move/idle transition or the keepalive tick; the keepalive re-sends the CURRENT continuous
            // heading each tick, so a turn toward a fresh waypoint lands within one tick. Real elapsed dt keeps the
            // integrated motion proportional to real time (see the cadence note above). A stop sends (0,0).
            var changed = desiredMoving != _intentMoving;
            var keepaliveDue = _intentMoving && elapsed >= _nextKeepaliveAt;
            if (changed || keepaliveDue)
            {
                _intentMoving = desiredMoving;
                var dir = desiredMoving ? desiredDir : WorldVector.Zero;
                var moveDt = (float)Math.Clamp((elapsed - _lastMoveSendElapsed).TotalSeconds, 0d, 0.1d);
                _lastMoveSendElapsed = elapsed;
                Send(
                    _serverPeer,
                    new MoveIntentMessage(++_inputSequence, (float)dir.X, (float)dir.Y, moveDt),
                    DeliveryMethod.Unreliable);
                _nextKeepaliveAt = elapsed + MoveIntentKeepalive;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_serverPeer is not null)
            {
                _client.DisconnectPeer(_serverPeer);
                // Manual mode: flush the disconnect packet with an explicit ManualUpdate before Stop.
                _client.ManualUpdate(0);
                _client.PollEvents();
            }

            _client.Stop();
            _disposed = true;
        }

        private void OnPeerConnected(NetPeer peer)
        {
            _serverPeer = peer;
            Send(peer, new ClientHelloMessage("mmo-server-synthetic-client"), DeliveryMethod.ReliableOrdered);
            Send(peer, new LoginRequestMessage(_name, _name), DeliveryMethod.ReliableOrdered);
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            try
            {
                HandleMessage(ProtocolCodec.Decode(reader.GetRemainingBytes()));
            }
            catch
            {
                _owner.RecordServerError();
            }
            finally
            {
                reader.Recycle();
            }
        }

        private void HandleMessage(IProtocolMessage message)
        {
            switch (message)
            {
                case LoginResultMessage login:
                    IsAuthenticated = login.Accepted;
                    if (!login.Accepted)
                    {
                        _owner.RecordServerError();
                    }

                    break;
                case WorldSnapshotMessage snapshot:
                    _owner.RecordSnapshot();
                    if (_serverPeer is not null)
                    {
                        Send(_serverPeer, new SnapshotAckMessage(snapshot.SnapshotSequence), DeliveryMethod.Sequenced);
                    }

                    break;
                case ServerErrorMessage:
                    _owner.RecordServerError();
                    break;
            }
        }

        private static void Send(NetPeer peer, IProtocolMessage message, DeliveryMethod deliveryMethod)
        {
            peer.Send(ProtocolCodec.Encode(message), 0, deliveryMethod);
        }
    }
}
