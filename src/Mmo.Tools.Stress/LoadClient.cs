using LiteNetLib;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;

namespace Mmo.Tools.Stress;

public sealed class LoadClient : IDisposable
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

    private readonly int _id;
    private readonly StressOptions _options;
    private readonly RunStats _stats;
    private readonly Random _random;
    private readonly string _name;
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _client;

    // Held-direction movement intent keepalive (protocol v15): mirror the real clients, which resend the
    // current intent ~every 500 ms so a dropped intent can't wedge the avatar. This (plus on-change
    // sends), not a per-step stream, is what the server now sees — inbound move/s drops accordingly.
    private static readonly TimeSpan MoveIntentKeepalive = TimeSpan.FromMilliseconds(500);

    private NetPeer? _serverPeer;
    private uint _inputSequence;
    private bool _authenticated;
    private bool _disposed;
    private TimeSpan _nextKeepaliveAt;
    private TimeSpan _nextDirectionAt;
    private TimeSpan _nextChatAt;
    private Direction8? _direction;
    private bool _intentMoving;
    private Direction8 _intentDirection;

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
        // Manual mode: NetManager.Start(manualMode: true) does NOT spawn a background logic/receive
        // thread. Instead the driver pumps the socket explicitly via ManualUpdate + PollEvents on the
        // single run-loop thread (see Poll). This is what decouples client count from OS-thread count:
        // N clients => N sockets but ~1 driver thread, instead of the old N background threads that
        // crashed the rig around ~500 clients (S45).
        _client.Start(System.Net.IPAddress.Any, System.Net.IPAddress.IPv6Any, 0, manualMode: true);
        _client.Connect(_options.Host, _options.Port, _options.ConnectionKey);

        var initialOffsetMs = _random.Next(0, Math.Max(1, (int)MoveIntentKeepalive.TotalMilliseconds));
        _nextKeepaliveAt = TimeSpan.FromMilliseconds(initialOffsetMs);
        _nextDirectionAt = TimeSpan.Zero;
        _nextChatAt = _options.ChatInterval > TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(_random.Next(0, Math.Max(1, (int)_options.ChatInterval.TotalMilliseconds)))
            : TimeSpan.MaxValue;
    }

    public void Poll(TimeSpan elapsed, int deltaTimeMs)
    {
        if (_disposed)
        {
            return;
        }

        // Manual mode requires the host to drive the NetManager every iteration: ManualUpdate advances
        // the library's internal timers (connection requests/handshake, reliable resends, ping/pong,
        // disconnect timeout) using the elapsed milliseconds since the last call, and PollEvents then
        // dispatches the queued events (connect, receive, error, latency) to the listener. Without the
        // background thread these two calls are the only thing keeping the connection alive.
        _client.ManualUpdate(deltaTimeMs);
        _client.PollEvents();

        if (!_authenticated || _serverPeer is null)
        {
            return;
        }

        // Periodically pick a new desired direction (null => stop). Sending happens below, only when the
        // intent actually changes (on-change), plus a low-rate keepalive — the v15 input model.
        if (elapsed >= _nextDirectionAt)
        {
            _direction = Directions[_random.Next(Directions.Length)];
            _nextDirectionAt = elapsed + _options.DirectionInterval;
        }

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
            // In manual mode the disconnect packet is only flushed by an explicit ManualUpdate; pump
            // one tick (plus PollEvents) so the server is told to drop the peer instead of waiting out
            // the DisconnectTimeout. _client.Stop() below also sends disconnects, but this is cheap and
            // makes the intent explicit.
            _client.ManualUpdate(0);
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
                if (_serverPeer is not null)
                {
                    Send(_serverPeer, new SnapshotAckMessage(snapshot.SnapshotSequence), DeliveryMethod.Sequenced);
                }

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
