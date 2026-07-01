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
        private static readonly Direction8?[] Directions =
        [
            Direction8.N,
            Direction8.NE,
            Direction8.E,
            Direction8.SE,
            Direction8.S,
            Direction8.SW,
            Direction8.W,
            Direction8.NW,
            null
        ];

        private readonly string _name;
        private readonly int _serverPort;
        private readonly string _connectionKey;
        private readonly SyntheticClientLoad _owner;
        private readonly Random _random;
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _client;

        // Keepalive cadence for the per-input continuous move intent — MUST match NominalMoveDtSeconds (≈ one 20 Hz
        // tick), like a real client sending ~per-frame. If the keepalive is LONGER than the nominal dt (it was 500 ms
        // vs a 50 ms dt), the bot integrates only 0.05 s of motion per keepalive → it CRAWLS at ~1/10 speed, while its
        // replicated Velocity is the full dir×speed. The remote render (extrapolate-to-now) then projects it forward at
        // full speed and SNAPS BACK each sparse update = jitter in place. Matching the cadence to the dt makes the
        // per-interval motion equal velocity×interval, so the extrapolation lands exactly right → smooth roaming.
        private static readonly TimeSpan MoveIntentKeepalive = TimeSpan.FromMilliseconds(50);

        // CONTINUOUS MIGRATION (Phase 3, v36): the synthetic load client does not predict — it sends one continuous
        // MoveIntent on change + keepalive with a fixed NOMINAL dt (≈ one 20 Hz tick) and a UNIT direction. The
        // server's anti-speedhack budget caps the integrated distance to real elapsed regardless of this nominal dt.
        private const float NominalMoveDtSeconds = 1f / 20f;

        private NetPeer? _serverPeer;
        private bool _disposed;
        private uint _inputSequence;
        private TimeSpan _nextKeepaliveAt;
        private TimeSpan _nextDirectionAt;
        private Direction8? _direction;
        private bool _intentMoving;
        private Direction8 _intentDirection;

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
            _nextDirectionAt = TimeSpan.Zero;
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

            if (elapsed >= _nextDirectionAt)
            {
                _direction = Directions[_random.Next(Directions.Length)];
                _nextDirectionAt = elapsed + TimeSpan.FromSeconds(1);
            }

            // CONTINUOUS MIGRATION (Phase 3, v36): per-input continuous MoveIntent — send on change plus a 500 ms
            // keepalive. The server integrates each fresh input by its dt on the receive path. A held direction sends
            // its UNIT world vector + a nominal dt; a stop sends (0,0). Sent unreliable (latest input wins), matching
            // the real client's per-frame model.
            var desiredMoving = _direction.HasValue;
            var desiredDirection = _direction ?? _intentDirection;
            var changed = desiredMoving != _intentMoving || (desiredMoving && desiredDirection != _intentDirection);
            var keepaliveDue = _intentMoving && elapsed >= _nextKeepaliveAt;
            if (changed || keepaliveDue)
            {
                _intentMoving = desiredMoving;
                _intentDirection = desiredDirection;
                var dir = desiredMoving ? _intentDirection.ToUnitVector() : WorldVector.Zero;
                Send(
                    _serverPeer,
                    new MoveIntentMessage(++_inputSequence, (float)dir.X, (float)dir.Y, NominalMoveDtSeconds),
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
