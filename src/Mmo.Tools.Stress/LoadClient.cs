using LiteNetLib;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;

namespace Mmo.Tools.Stress;

public sealed class LoadClient : IDisposable
{
    private static readonly WorldVector[] Directions =
    [
        new(0, -1),
        new(1, -1),
        new(1, 0),
        new(1, 1),
        new(0, 1),
        new(-1, 1),
        new(-1, 0),
        new(-1, -1),
        WorldVector.Zero
    ];

    private readonly int _id;
    private readonly StressOptions _options;
    private readonly RunStats _stats;
    private readonly Random _random;
    private readonly string _name;
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _client;

    private NetPeer? _serverPeer;
    private uint _inputSequence;
    private bool _authenticated;
    private bool _disposed;
    private TimeSpan _nextMoveAt;
    private TimeSpan _nextDirectionAt;
    private TimeSpan _nextChatAt;
    private WorldVector _direction = WorldVector.Zero;

    public LoadClient(int id, StressOptions options, RunStats stats, Random random)
    {
        _id = id;
        _options = options;
        _stats = stats;
        _random = random;
        _name = $"{options.NamePrefix}{id + 1:0000}";
        _client = new NetManager(_listener)
        {
            AutoRecycle = false,
            DisconnectTimeout = 15000
        };

        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += (_, _) =>
        {
            _serverPeer = null;
            _authenticated = false;
            _stats.RecordPeerDisconnected();
        };
        _listener.NetworkErrorEvent += (_, _) => _stats.RecordNetworkError();
        _listener.NetworkLatencyUpdateEvent += (_, latency) => _stats.RecordLatency(latency);
        _listener.NetworkReceiveEvent += OnNetworkReceive;
    }

    public bool IsAuthenticated => _authenticated;

    public void Start()
    {
        _client.Start();
        _client.Connect(_options.Host, _options.Port, _options.ConnectionKey);

        var initialOffsetMs = _random.Next(0, Math.Max(1, (int)_options.MoveInterval.TotalMilliseconds));
        _nextMoveAt = TimeSpan.FromMilliseconds(initialOffsetMs);
        _nextDirectionAt = TimeSpan.Zero;
        _nextChatAt = _options.ChatInterval > TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(_random.Next(0, Math.Max(1, (int)_options.ChatInterval.TotalMilliseconds)))
            : TimeSpan.MaxValue;
    }

    public void Poll(TimeSpan elapsed)
    {
        if (_disposed)
        {
            return;
        }

        _client.PollEvents();

        if (!_authenticated || _serverPeer is null)
        {
            return;
        }

        if (elapsed >= _nextDirectionAt)
        {
            _direction = Directions[_random.Next(Directions.Length)].NormalizeOrZero();
            _nextDirectionAt = elapsed + _options.DirectionInterval;
        }

        if (elapsed >= _nextMoveAt)
        {
            Send(_serverPeer, new MoveInputMessage(++_inputSequence, _direction), DeliveryMethod.Sequenced);
            _nextMoveAt = elapsed + _options.MoveInterval;
        }

        if (elapsed >= _nextChatAt)
        {
            Send(_serverPeer, new ChatSendMessage($"stress ping {_id + 1} #{_inputSequence}"), DeliveryMethod.ReliableOrdered);
            _nextChatAt = elapsed + _options.ChatInterval;
        }
    }

    public void Stop()
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

    public void Dispose()
    {
        Stop();
    }

    private void OnPeerConnected(NetPeer peer)
    {
        _serverPeer = peer;
        _stats.RecordPeerConnected();
        Send(peer, new ClientHelloMessage("mmo-stress-client"), DeliveryMethod.ReliableOrdered);
        Send(peer, new LoginRequestMessage(_name, _name), DeliveryMethod.ReliableOrdered);
    }

    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        byte[] bytes;
        try
        {
            bytes = reader.GetRemainingBytes();
            _stats.RecordReceived(bytes.Length);
            HandleMessage(ProtocolCodec.Decode(bytes));
        }
        catch
        {
            _stats.RecordServerError();
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
            case ServerHelloMessage:
                _stats.RecordServerHello();
                break;
            case LoginResultMessage login:
                if (login.Accepted)
                {
                    _authenticated = true;
                    _stats.RecordLoginAccepted();
                }
                else
                {
                    _stats.RecordLoginRejected();
                }

                break;
            case WorldSnapshotMessage snapshot:
                _stats.RecordSnapshot(snapshot.Entities.Count);
                break;
            case ChatBroadcastMessage:
                _stats.RecordChatBroadcast();
                break;
            case ServerErrorMessage:
                _stats.RecordServerError();
                break;
        }
    }

    private void Send(NetPeer peer, IProtocolMessage message, DeliveryMethod deliveryMethod)
    {
        var bytes = ProtocolCodec.Encode(message);
        peer.Send(bytes, 0, deliveryMethod);
        _stats.RecordSent(bytes.Length);
    }
}
