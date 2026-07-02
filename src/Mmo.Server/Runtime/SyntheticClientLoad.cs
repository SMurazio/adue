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

    // The LIVE server base move speed (units/s). Threaded in from GameServer as a Func so the bots' local
    // dead-reckon tracks live continuous.baseMoveSpeed changes (multiplier tweaks), not a start-time snapshot.
    private Func<double> _baseSpeedProvider = () => 1000d / 150d;

    public void Start(int clientCount, TimeSpan duration, int serverPort, string connectionKey, Func<double> baseSpeedProvider)
    {
        Stop();

        _baseSpeedProvider = baseSpeedProvider;

        _startedAt = DateTimeOffset.UtcNow;
        _endsAt = _startedAt + duration;
        _lastPollElapsed = TimeSpan.Zero;
        SnapshotsReceived = 0;
        ServerErrors = 0;
        NetworkErrors = 0;

        var prefix = $"Test{_startedAt:HHmmss}";
        for (var i = 0; i < clientCount; i++)
        {
            var client = new SyntheticClient(i, $"{prefix}{i + 1:000}", serverPort, connectionKey, this, _baseSpeedProvider);
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
        private const double WaypointReachRadius = 1.5d;
        private const double ArrivalIdleChance = 0.25d;       // ~a quarter of arrivals pause before moving on
        private const double MaxTurnRateRadPerSecond = 4d;    // rate-limit heading changes so turns EASE, not snap
        private const double MinWaypointDistanceUnits = 12d;  // keep new waypoints far enough to avoid rapid re-picking

        private static readonly TimeSpan MinIdle = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan MaxIdle = TimeSpan.FromMilliseconds(2200);

        private readonly string _name;
        private readonly int _serverPort;
        private readonly string _connectionKey;
        private readonly SyntheticClientLoad _owner;
        // The LIVE server base move speed (units/s) — the bot's ACTUAL server movement runs at this, so the local
        // dead-reckon must too or the estimate lags the true position and the wander oversteers. Live Func (not a
        // snapshot) so multiplier tweaks (continuous.baseMoveSpeed) retune the estimate the same tick they retune
        // the real movement. (Was a hardcoded 4d that never matched the ~6.667 base → the oversteer bug.)
        private readonly Func<double> _baseSpeedProvider;
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
        private double _headingAngle; // current send heading (radians); eased toward the waypoint, never snapped
        private TimeSpan _idleUntil;

        public SyntheticClient(int id, string name, int serverPort, string connectionKey, SyntheticClientLoad owner, Func<double> baseSpeedProvider)
        {
            _name = name;
            _serverPort = serverPort;
            _connectionKey = connectionKey;
            _owner = owner;
            _baseSpeedProvider = baseSpeedProvider;
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

        // A fresh random interior waypoint at least MinWaypointDistanceUnits away, so the bot doesn't reach it
        // instantly and re-pick in a tight jitter (which whipped the heading around). Falls back to the last draw
        // after a few attempts so this always terminates.
        private void PickWaypoint()
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var x = WanderMinCoord + (_random.NextDouble() * (WanderMaxCoord - WanderMinCoord));
                var y = WanderMinCoord + (_random.NextDouble() * (WanderMaxCoord - WanderMinCoord));
                var dx = x - _estX;
                var dy = y - _estY;
                if (attempt == 7 || ((dx * dx) + (dy * dy)) >= (MinWaypointDistanceUnits * MinWaypointDistanceUnits))
                {
                    _targetX = x;
                    _targetY = y;
                    return;
                }
            }
        }

        // Rotate `current` toward `target` (radians) by at most `maxDelta`, taking the short way around the circle.
        private static double RotateToward(double current, double target, double maxDelta)
        {
            var diff = target - current;
            while (diff > Math.PI)
            {
                diff -= 2d * Math.PI;
            }

            while (diff < -Math.PI)
            {
                diff += 2d * Math.PI;
            }

            return current + Math.Clamp(diff, -maxDelta, maxDelta);
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
                _headingAngle = Math.Atan2(_targetY - _estY, _targetX - _estX); // start already facing the first waypoint
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

            // Steer toward the waypoint, but TURN GRADUALLY (rate-limited heading) instead of snapping to the new
            // direction the instant a waypoint changes — so the bot curves into it like a real client rather than
            // whipping its heading (the snap read as "doesn't turn correctly"). Dead-reckon along the eased heading.
            var desiredMoving = elapsed >= _idleUntil;
            var desiredDir = WorldVector.Zero;
            if (desiredMoving)
            {
                var dx = _targetX - _estX;
                var dy = _targetY - _estY;
                if (((dx * dx) + (dy * dy)) > 1e-6d)
                {
                    var stepDt = Math.Clamp(deltaTimeMs / 1000d, 0d, 0.1d);
                    _headingAngle = RotateToward(_headingAngle, Math.Atan2(dy, dx), MaxTurnRateRadPerSecond * stepDt);
                    desiredDir = new WorldVector(Math.Cos(_headingAngle), Math.Sin(_headingAngle));
                    // Dead-reckon at the LIVE server base speed so the estimate matches the bot's real movement.
                    var baseSpeed = _baseSpeedProvider();
                    _estX += desiredDir.X * baseSpeed * stepDt;
                    _estY += desiredDir.Y * baseSpeed * stepDt;
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
