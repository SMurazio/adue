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
            var deliveryMethod = message is MoveInputMessage or SnapshotAckMessage ? DeliveryMethod.Sequenced : DeliveryMethod.ReliableOrdered;
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
            "w" => new WorldVector(0, -1),
            "a" => new WorldVector(-1, 0),
            "s" => new WorldVector(0, 1),
            "d" => new WorldVector(1, 0),
            "wa" or "aw" or "nw" => new WorldVector(-1, -1),
            "wd" or "dw" or "ne" => new WorldVector(1, -1),
            "sa" or "as" or "sw" => new WorldVector(-1, 1),
            "sd" or "ds" or "se" => new WorldVector(1, 1),
            "stop" => WorldVector.Zero,
            _ => WorldVector.Zero
        };

        if (command is "w" or "a" or "s" or "d" or "wa" or "aw" or "nw" or "wd" or "dw" or "ne" or "sa" or "as" or "sw" or "sd" or "ds" or "se" or "stop")
        {
            outgoing.Enqueue(new MoveInputMessage(nextSequence(), direction));
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
            Console.WriteLine($"Server: {hello.ServerName}, protocol={hello.ProtocolVersion}, tickRate={hello.TickRate}");
            break;
        case LoginResultMessage login:
            Console.WriteLine(login.Accepted
                ? $"Logged in as {login.DisplayName} ({login.Role}) at ({login.Position.X:0.00}, {login.Position.Y:0.00})"
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
                return $"{name}@({entity.Position.X:0.0},{entity.Position.Y:0.0})";
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
