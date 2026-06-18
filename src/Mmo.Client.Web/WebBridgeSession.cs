using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LiteNetLib;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;

namespace Mmo.Client.Web;

public sealed class WebBridgeSession
{
    private readonly WebSocket _socket;
    private readonly BridgeOptions _options;
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _client;
    private readonly ConcurrentQueue<IProtocolMessage> _toServer = new();
    private readonly ConcurrentQueue<string> _toBrowser = new();

    private NetPeer? _serverPeer;
    private uint _inputSequence;
    private volatile bool _closing;

    public WebBridgeSession(WebSocket socket, BridgeOptions options)
    {
        _socket = socket;
        _options = options;
        _client = new NetManager(_listener)
        {
            AutoRecycle = false,
            DisconnectTimeout = 15000
        };

        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += (_, info) =>
        {
            _serverPeer = null;
            EnqueueBrowser(new { type = "status", text = $"Disconnected: {info.Reason}" });
        };
        _listener.NetworkErrorEvent += (endpoint, error) =>
            EnqueueBrowser(new { type = "error", code = "network_error", message = $"{endpoint}: {error}" });
        _listener.NetworkReceiveEvent += OnNetworkReceive;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _client.Start();
        _client.Connect(_options.GameHost, _options.GamePort, _options.ConnectionKey);
        EnqueueBrowser(new { type = "status", text = $"Connecting to {_options.GameHost}:{_options.GamePort} as {_options.Name}" });

        var receiveTask = ReceiveBrowserLoopAsync(cancellationToken);

        try
        {
            while (!_closing && _socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                _client.PollEvents();
                FlushServerMessages();
                await FlushBrowserMessagesAsync(cancellationToken);
                await Task.Delay(15, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _closing = true;
            _client.Stop();
        }

        await receiveTask.ConfigureAwait(false);
    }

    private void OnPeerConnected(NetPeer peer)
    {
        _serverPeer = peer;
        EnqueueBrowser(new { type = "status", text = $"Connected to {peer.Address}:{peer.Port}" });
        Send(peer, new ClientHelloMessage("mmo-web-debug-client"), DeliveryMethod.ReliableOrdered);
        Send(peer, new LoginRequestMessage(_options.Name, _options.Name), DeliveryMethod.ReliableOrdered);
    }

    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        try
        {
            EnqueueProtocolMessage(ProtocolCodec.Decode(reader.GetRemainingBytes()));
        }
        catch (Exception exception)
        {
            EnqueueBrowser(new { type = "error", code = "bad_packet", message = exception.Message });
        }
        finally
        {
            reader.Recycle();
        }
    }

    private async Task ReceiveBrowserLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        try
        {
            while (!_closing && _socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _closing = true;
                        return;
                    }

                    stream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                HandleBrowserMessage(Encoding.UTF8.GetString(stream.ToArray()));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
            _closing = true;
        }
    }

    private void HandleBrowserMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString();

        switch (type)
        {
            case "move":
                _toServer.Enqueue(new MoveInputMessage(++_inputSequence, ReadDirection(root)));
                break;
            case "chat":
                var text = root.GetProperty("text").GetString() ?? "";
                _toServer.Enqueue(new ChatSendMessage(text));
                break;
            case "quit":
                _closing = true;
                break;
        }
    }

    private void FlushServerMessages()
    {
        while (_serverPeer is not null && _toServer.TryDequeue(out var message))
        {
            var delivery = message is MoveInputMessage or SnapshotAckMessage ? DeliveryMethod.Sequenced : DeliveryMethod.ReliableOrdered;
            Send(_serverPeer, message, delivery);
        }
    }

    private async Task FlushBrowserMessagesAsync(CancellationToken cancellationToken)
    {
        while (_socket.State == WebSocketState.Open && _toBrowser.TryDequeue(out var json))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
    }

    private void EnqueueProtocolMessage(IProtocolMessage message)
    {
        switch (message)
        {
            case ServerHelloMessage hello:
                EnqueueBrowser(new { type = "serverHello", hello.ServerName, hello.ProtocolVersion, hello.TickRate });
                break;
            case LoginResultMessage login:
                EnqueueBrowser(new
                {
                    type = "login",
                    login.Accepted,
                    login.CharacterId,
                    login.DisplayName,
                    role = login.Role.ToString(),
                    position = new { login.Position.X, login.Position.Y },
                    login.Reason
                });
                break;
            case WorldSnapshotMessage snapshot:
                _toServer.Enqueue(new SnapshotAckMessage(snapshot.SnapshotSequence));
                EnqueueBrowser(new
                {
                    type = "snapshot",
                    tick = snapshot.ServerTick,
                    sequence = snapshot.SnapshotSequence,
                    totalEntities = snapshot.TotalEntities,
                    isComplete = snapshot.IsComplete,
                    chunkIndex = snapshot.ChunkIndex,
                    chunkCount = snapshot.ChunkCount,
                    entities = snapshot.Entities.Select(entity => new
                    {
                        id = entity.NetworkId,
                        x = entity.Position.X,
                        y = entity.Position.Y
                    })
                });
                break;
            case EntitySpawnMessage spawn:
                EnqueueBrowser(new
                {
                    type = "entitySpawn",
                    spawn.NetworkId,
                    spawn.CharacterId,
                    kind = spawn.Kind.ToString(),
                    name = spawn.DisplayName,
                    x = spawn.Position.X,
                    y = spawn.Position.Y
                });
                break;
            case EntityDespawnMessage despawn:
                EnqueueBrowser(new
                {
                    type = "entityDespawn",
                    tick = despawn.ServerTick,
                    despawn.NetworkId
                });
                break;
            case ChatBroadcastMessage chat:
                EnqueueBrowser(new { type = "chat", chat.Sender, chat.Text });
                break;
            case ServerErrorMessage error:
                EnqueueBrowser(new { type = "error", error.Code, error.Message });
                break;
        }
    }

    private void EnqueueBrowser(object value)
    {
        _toBrowser.Enqueue(JsonSerializer.Serialize(value, JsonOptions));
    }

    private static void Send(NetPeer peer, IProtocolMessage message, DeliveryMethod deliveryMethod)
    {
        peer.Send(ProtocolCodec.Encode(message), 0, deliveryMethod);
    }

    private static WorldVector ReadDirection(JsonElement root)
    {
        if (root.TryGetProperty("x", out var x) && root.TryGetProperty("y", out var y))
        {
            return new WorldVector(x.GetSingle(), y.GetSingle());
        }

        var direction = root.TryGetProperty("direction", out var property)
            ? property.GetString() ?? "stop"
            : "stop";

        return ToDirection(direction);
    }

    private static WorldVector ToDirection(string direction)
    {
        return direction.ToLowerInvariant() switch
        {
            "w" or "up" => new WorldVector(0, -1),
            "a" or "left" => new WorldVector(-1, 0),
            "s" or "down" => new WorldVector(0, 1),
            "d" or "right" => new WorldVector(1, 0),
            "nw" => new WorldVector(-1, -1),
            "ne" => new WorldVector(1, -1),
            "sw" => new WorldVector(-1, 1),
            "se" => new WorldVector(1, 1),
            _ => WorldVector.Zero
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
