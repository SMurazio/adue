using LiteNetLib;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;

namespace Mmo.Server.Runtime;

public sealed class SyntheticClientLoad : IDisposable
{
    private readonly List<SyntheticClient> _clients = [];

    private DateTimeOffset _startedAt;
    private DateTimeOffset _endsAt;
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
        foreach (var client in _clients)
        {
            client.Poll(elapsed);
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

        // Keepalive cadence for held-direction movement intent (protocol v15), mirroring the real clients.
        private static readonly TimeSpan MoveIntentKeepalive = TimeSpan.FromMilliseconds(500);

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
            _client.Start();
            _client.Connect("127.0.0.1", _serverPort, _connectionKey);
            _nextKeepaliveAt = TimeSpan.FromMilliseconds(_random.Next(0, 500));
            _nextDirectionAt = TimeSpan.Zero;
        }

        public void Poll(TimeSpan elapsed)
        {
            if (_disposed)
            {
                return;
            }

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

            // Held-direction movement intent (v15): send only on change plus a 500 ms keepalive, not a
            // step every 250 ms. The server steps the avatar from the held intent on its own cooldown.
            var desiredMoving = _direction.HasValue;
            var desiredDirection = _direction ?? _intentDirection;
            var changed = desiredMoving != _intentMoving || (desiredMoving && desiredDirection != _intentDirection);
            var keepaliveDue = _intentMoving && elapsed >= _nextKeepaliveAt;
            if (changed || keepaliveDue)
            {
                _intentMoving = desiredMoving;
                _intentDirection = desiredDirection;
                Send(_serverPeer, new MoveIntentMessage(++_inputSequence, _intentMoving, _intentDirection), DeliveryMethod.ReliableOrdered);
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
