using System.Collections.Concurrent;
using LiteNetLib;
using Mmo.Client.ConsoleApp;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;

var options = ClientOptions.FromArgs(args);
using var shutdown = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

var outgoing = new ConcurrentQueue<IProtocolMessage>();
var listener = new EventBasedNetListener();
var client = new NetManager(listener)
{
    AutoRecycle = false,
    DisconnectTimeout = 15000
};

NetPeer? serverPeer = null;
uint inputSequence = 0;
DateTimeOffset lastSnapshotPrintedAt = DateTimeOffset.MinValue;
var entityNames = new Dictionary<uint, string>();

listener.PeerConnectedEvent += peer =>
{
    serverPeer = peer;
    Console.WriteLine($"Connected to {peer.Address}:{peer.Port}.");
    Send(peer, new ClientHelloMessage("mmo-console-client"), DeliveryMethod.ReliableOrdered);
    Send(peer, new LoginRequestMessage(options.Name, options.Name), DeliveryMethod.ReliableOrdered);
};

listener.PeerDisconnectedEvent += (_, info) =>
{
    Console.WriteLine($"Disconnected: {info.Reason}");
    shutdown.Cancel();
};

listener.NetworkErrorEvent += (endpoint, error) =>
{
    Console.WriteLine($"Network error from {endpoint}: {error}");
};

listener.NetworkReceiveEvent += (_, reader, _, _) =>
{
    try
    {
        var message = ProtocolCodec.Decode(reader.GetRemainingBytes());
        if (message is WorldSnapshotMessage snapshot)
        {
            outgoing.Enqueue(new SnapshotAckMessage(snapshot.SnapshotSequence));
        }

        PrintMessage(message, options.ShowSnapshots, entityNames, ref lastSnapshotPrintedAt);
    }
    catch (Exception exception)
    {
        Console.WriteLine($"Bad packet: {exception.Message}");
    }
    finally
    {
        reader.Recycle();
    }
};

client.Start();
client.Connect(options.Host, options.Port, options.ConnectionKey);

Console.WriteLine($"Connecting to {options.Host}:{options.Port} as {options.Name}.");
Console.WriteLine("Commands: w/a/s/d, stop, /say text, /help, /role, /quit");
Console.WriteLine(options.ShowSnapshots
    ? "Snapshot logging enabled."
    : "Snapshot logging disabled. Restart with --snapshots to show world snapshots.");

_ = Task.Run(() => ReadInputLoop(outgoing, shutdown, () => ++inputSequence));

try
{
    while (!shutdown.IsCancellationRequested)
    {
        client.PollEvents();

        while (serverPeer is not null && outgoing.TryDequeue(out var message))
        {
            var deliveryMethod = message is SnapshotAckMessage ? DeliveryMethod.Sequenced : DeliveryMethod.ReliableOrdered;
            Send(serverPeer, message, deliveryMethod);
        }

        await Task.Delay(15, shutdown.Token);
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}
finally
{
    client.Stop();
}

static void ReadInputLoop(
    ConcurrentQueue<IProtocolMessage> outgoing,
    CancellationTokenSource shutdown,
    Func<uint> nextSequence)
{
    // CONTINUOUS MIGRATION (Phase 3, v36): the fixed nominal dt stamped on each continuous MoveIntent (≈ one 20 Hz
    // tick). The console REPL doesn't predict; the server's anti-speedhack budget bounds the integrated distance.
    const float NominalMoveDtSeconds = 1f / 20f;

    while (!shutdown.IsCancellationRequested)
    {
        var line = Console.ReadLine();
        if (line is null)
        {
            shutdown.Cancel();
            return;
        }

        var trimmed = line.Trim();
        if (trimmed.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        {
            shutdown.Cancel();
            return;
        }

        if (trimmed.StartsWith("/say ", StringComparison.OrdinalIgnoreCase))
        {
            outgoing.Enqueue(new ChatSendMessage(trimmed[5..]));
            continue;
        }

        if (trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            outgoing.Enqueue(new ChatSendMessage(trimmed));
            continue;
        }

        var command = trimmed.ToLowerInvariant();
        var direction = command switch
        {
            "w" => Direction8.N,
            "a" => Direction8.W,
            "s" => Direction8.S,
            "d" => Direction8.E,
            "wa" or "aw" or "nw" => Direction8.NW,
            "wd" or "dw" or "ne" => Direction8.NE,
            "sa" or "as" or "sw" => Direction8.SW,
            "sd" or "ds" or "se" => Direction8.SE,
            _ => (Direction8?)null
        };

        if (direction.HasValue)
        {
            // CONTINUOUS MIGRATION (Phase 3, v36): per-input continuous MoveIntent — the held direction's UNIT world
            // vector + a fixed nominal dt. This REPL does not predict and sends one input per typed command, so a
            // single "w" integrates ≈ one tick of motion (type repeatedly to walk; "stop" sends a (0,0) input).
            var unit = direction.Value.ToUnitVector();
            outgoing.Enqueue(new MoveIntentMessage(nextSequence(), (float)unit.X, (float)unit.Y, NominalMoveDtSeconds));
        }
        else if (command == "stop")
        {
            outgoing.Enqueue(new MoveIntentMessage(nextSequence(), 0f, 0f, NominalMoveDtSeconds));
        }
        else
        {
            Console.WriteLine("Unknown command.");
        }
    }
}

static void Send(NetPeer peer, IProtocolMessage message, DeliveryMethod deliveryMethod)
{
    peer.Send(ProtocolCodec.Encode(message), 0, deliveryMethod);
}

static void PrintMessage(
    IProtocolMessage message,
    bool showSnapshots,
    Dictionary<uint, string> entityNames,
    ref DateTimeOffset lastSnapshotPrintedAt)
{
    switch (message)
    {
        case ServerHelloMessage hello:
            Console.WriteLine($"Server: {hello.ServerName}, protocol={hello.ProtocolVersion}, tickRate={hello.TickRate}, stepCooldownMs={hello.StepCooldownMs}, interestRadiusUnits={hello.InterestRadiusUnits:0.#}");
            break;
        case LoginResultMessage login:
            Console.WriteLine(login.Accepted
                ? $"Logged in as {login.DisplayName} ({login.Role}) at tile ({login.Tile.X}, {login.Tile.Y})"
                : $"Login rejected: {login.Reason}");
            break;
        case EntitySpawnMessage spawn:
            entityNames[spawn.NetworkId] = spawn.DisplayName;
            break;
        case EntityDespawnMessage despawn:
            if (showSnapshots && entityNames.TryGetValue(despawn.NetworkId, out var despawnedName))
            {
                Console.WriteLine($"left interest: {despawnedName} #{despawn.NetworkId}");
            }
            break;
        case WorldSnapshotMessage snapshot:
            if (!showSnapshots)
            {
                break;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - lastSnapshotPrintedAt < TimeSpan.FromSeconds(1))
            {
                break;
            }

            lastSnapshotPrintedAt = now;
            var players = string.Join(", ", snapshot.Entities.Select(entity =>
            {
                var name = entityNames.TryGetValue(entity.NetworkId, out var knownName)
                    ? knownName
                    : $"#{entity.NetworkId}";
                var tile = entity.Position.ToTileRounded();
                return $"{name}@({tile.X},{tile.Y}) {entity.Facing}";
            }));
            Console.WriteLine($"tick={snapshot.ServerTick} seq={snapshot.SnapshotSequence} visible=[{players}]");
            break;
        case ChatBroadcastMessage chat:
            Console.WriteLine($"[{chat.Sender}] {chat.Text}");
            break;
        case ServerErrorMessage error:
            Console.WriteLine($"Server error {error.Code}: {error.Message}");
            break;
    }
}
