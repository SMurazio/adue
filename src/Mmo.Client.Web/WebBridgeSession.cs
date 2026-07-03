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

    // CONTINUOUS MIGRATION (Phase 3, v36): the fixed nominal dt the web bridge stamps on each continuous MoveIntent
    // (≈ one 20 Hz tick). The bridge does not predict; the server's anti-speedhack budget bounds the integrated
    // distance to real elapsed time regardless of this nominal value.
    private const float NominalMoveDtSeconds = 1f / 20f;

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
            case "moveIntent":
                // CONTINUOUS MIGRATION (Phase 3, v36): per-input continuous MoveIntent. The browser declares a held
                // Direction8 + moving flag; we convert it to the raw UNIT world vector (or (0,0) for stop) and send it
                // with a fixed NOMINAL dt (the web bridge doesn't predict). The server integrates each fresh input by
                // its dt; its anti-speedhack budget caps the integrated distance to real time regardless of cadence.
                var moving = root.TryGetProperty("moving", out var movingProperty)
                    && movingProperty.ValueKind == JsonValueKind.True;
                TryReadDirection(root, out var direction);
                var dir = moving ? direction.ToUnitVector() : WorldVector.Zero;
                _toServer.Enqueue(new MoveIntentMessage(++_inputSequence, (float)dir.X, (float)dir.Y, NominalMoveDtSeconds));
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
            // MoveIntent is reliable-ordered (a dropped "stop" must not be lost); SnapshotAck stays
            // sequenced (last-write-wins, drops are harmless).
            var delivery = message is SnapshotAckMessage ? DeliveryMethod.Sequenced : DeliveryMethod.ReliableOrdered;
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
                // Keep the browser JSON key "interestRadiusTiles" (the C# field renamed to InterestRadiusUnits, but the
                // web client app.js + its asset test still read interestRadiusTiles — the JSON wire to the browser is unchanged).
                EnqueueBrowser(new { type = "serverHello", hello.ServerName, hello.ProtocolVersion, hello.TickRate, hello.StepCooldownMs, InterestRadiusTiles = hello.InterestRadiusUnits });
                break;
            case LoginResultMessage login:
                EnqueueBrowser(new
                {
                    type = "login",
                    login.Accepted,
                    login.CharacterId,
                    login.DisplayName,
                    role = login.Role.ToString(),
                    tile = new { login.Tile.X, login.Tile.Y },
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
                        // MIGRATION (Phase 3 Pass A): the snapshot now carries a continuous Position; project it to
                        // its tile for the existing browser payload (tile-centred in Pass A, so byte-identical).
                        x = entity.Position.ToTileRounded().X,
                        y = entity.Position.ToTileRounded().Y,
                        facing = entity.Facing.ToString()
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
                    x = spawn.Tile.X,
                    y = spawn.Tile.Y,
                    facing = spawn.Facing.ToString()
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
            case ZoneInfoMessage zone:
                // Terrain is procedural content: regenerate the blocked set locally from the seed
                // descriptor via the same shared deterministic generator the server uses, instead of
                // receiving a tile payload. Verify the local hash matches the server's (drift/tamper
                // check); the server stays authoritative regardless. The browser keeps consuming a flat
                // blockedTiles list, so the bridge does the regeneration on its behalf.
                // AUTHORED-MAP M1: regenerate the FULL layout and compare ITS ContentHash — for an
                // authored genVersion the canonical hash covers categories/markers too, so re-hashing
                // only the blocked list here would false-fail against the server's layout hash.
                TerrainLayout regeneratedLayout;
                try
                {
                    regeneratedLayout = TerrainGenerator.GenerateLayout(zone.Width, zone.Height, zone.Seed, zone.GenVersion);
                }
                catch (Exception exception)
                {
                    EnqueueBrowser(new
                    {
                        type = "error",
                        code = "zone_gen_failed",
                        message = $"Could not regenerate zone '{zone.ZoneId}' (seed={zone.Seed}, genVersion={zone.GenVersion}): {exception.Message}"
                    });
                    break;
                }

                var localHash = regeneratedLayout.ContentHash;
                if (localHash != zone.ContentHash)
                {
                    EnqueueBrowser(new
                    {
                        type = "error",
                        code = "zone_hash_mismatch",
                        message = $"Zone '{zone.ZoneId}' content hash mismatch: local {localHash:X16} != server {zone.ContentHash:X16}. Generator drift or tampering."
                    });
                }

                EnqueueBrowser(new
                {
                    type = "zoneInfo",
                    zone.ZoneId,
                    zone.Width,
                    zone.Height,
                    blockedTiles = regeneratedLayout.BlockedTiles.Select(tile => new { tile.X, tile.Y })
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

    private static bool TryReadDirection(JsonElement root, out Direction8 direction)
    {
        if (root.TryGetProperty("direction", out var property))
        {
            return TryParseDirection(property.GetString() ?? "", out direction);
        }

        direction = Direction8.S;
        return false;
    }

    internal static bool TryParseDirection(string direction, out Direction8 parsed)
    {
        if (Enum.TryParse<Direction8>(direction, ignoreCase: true, out parsed)
            && Enum.IsDefined(parsed))
        {
            return true;
        }

        parsed = Direction8.S;
        return false;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
