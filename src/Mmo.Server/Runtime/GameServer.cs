using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using LiteNetLib;
using Mmo.Server.Configuration;
using Mmo.Server.Data;
using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;

namespace Mmo.Server.Runtime;

public sealed class GameServer
{
    private const string ServerName = "mmo-learning-server";
    private const int MaxSequencedSnapshotBytes = 1000;
    private const int ProtocolHeaderBytes = 7;
    private const int SnapshotHeaderBytes = 13;
    private const int EntityStateFixedBytes = 6;
    private const int MaxBadPacketsBeforeDisconnect = 5;
    private const int DefaultStressClientCount = 120;
    private static readonly TimeSpan DefaultStressDuration = TimeSpan.FromSeconds(60);
    private const float SnapshotRetentionBonusDistanceSquared = 144f;

    private readonly ServerOptions _options;
    private readonly ICharacterRepository _characters;
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _netManager;
    private readonly Dictionary<NetPeer, ClientSession> _sessions = new();
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();
    private readonly SyntheticClientLoad _syntheticLoad = new();
    private readonly ServerMetrics _metrics = new();
    private readonly ServerRuntimeGuard _runtimeGuard;
    private readonly NetworkIdPool _networkIds = new();

    private uint _serverTick;

    public GameServer(ServerOptions options, ICharacterRepository characters)
    {
        _options = options;
        _characters = characters;
        _runtimeGuard = new ServerRuntimeGuard(_metrics);
        _netManager = new NetManager(_listener)
        {
            AutoRecycle = false,
            DisconnectTimeout = 15000
        };

        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnNetworkReceive;
        _listener.NetworkLatencyUpdateEvent += OnNetworkLatencyUpdate;
        _listener.NetworkErrorEvent += (endpoint, error) =>
        {
            _metrics.RecordNetworkError();
            Log.Warn($"Network error from {endpoint}: {error}.");
        };
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _netManager.Start(_options.Port);
        Log.Info($"Server listening on UDP {_options.Port}.");

        var tickInterval = TimeSpan.FromSeconds(1d / _options.TickRate);
        var nextTickAt = DateTimeOffset.UtcNow;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _netManager.PollEvents();
                _syntheticLoad.Poll();
                DrainMainThreadActions();

                var now = DateTimeOffset.UtcNow;
                while (now >= nextTickAt)
                {
                    var tickStartedAt = Stopwatch.GetTimestamp();
                    var tickBudget = new TickBudgetRecorder();
                    var scheduleDrift = now - nextTickAt;
                    Tick((float)tickInterval.TotalSeconds, tickBudget);
                    _metrics.RecordTick(Stopwatch.GetElapsedTime(tickStartedAt), scheduleDrift, tickBudget.ToSample());
                    nextTickAt += tickInterval;
                }

                await Task.Delay(1, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            PersistConnectedPlayers();
            _syntheticLoad.Stop();
            _netManager.Stop();
            Log.Info("Server stopped.");
        }
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        request.AcceptIfKey(_options.ConnectionKey);
    }

    private void OnPeerConnected(NetPeer peer)
    {
        _sessions[peer] = new ClientSession(peer);
        _metrics.RecordPeerConnected();
        TrySend(peer, new ServerHelloMessage(ServerName, ProtocolCodec.Version, _options.TickRate), DeliveryMethod.ReliableOrdered);
        Log.Info($"Peer connected: {FormatPeer(peer)}.");
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        if (!_sessions.Remove(peer, out var session))
        {
            return;
        }

        if (session.IsAuthenticated)
        {
            _networkIds.Return(session.NetworkId);
            SavePositionBestEffort(session);
        }

        _metrics.RecordPeerDisconnected();
        Log.Info($"Peer disconnected: {FormatPeer(peer)}; reason={disconnectInfo.Reason}.");
    }

    private void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        if (_sessions.TryGetValue(peer, out var session))
        {
            session.LastLatencyMs = latency;
        }
    }

    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        try
        {
            var bytes = reader.GetRemainingBytes();
            var message = ProtocolCodec.Decode(bytes);
            _metrics.RecordReceived(message, bytes.Length);
            HandleMessage(peer, message);
        }
        catch (Exception exception)
        {
            _metrics.RecordBadPacket();
            var count = _sessions.TryGetValue(peer, out var session)
                ? session.RecordBadPacket()
                : MaxBadPacketsBeforeDisconnect;

            Log.Warn($"Failed to process packet from {FormatPeer(peer)}: {exception.Message}");
            if (count >= MaxBadPacketsBeforeDisconnect)
            {
                Log.Warn($"Disconnecting {FormatPeer(peer)} after {count} bad packets.");
                _netManager.DisconnectPeer(peer);
            }
            else
            {
                TrySend(peer, new ServerErrorMessage("bad_packet", "Bad packet."), DeliveryMethod.ReliableOrdered);
            }
        }
        finally
        {
            reader.Recycle();
        }
    }

    private void HandleMessage(NetPeer peer, IProtocolMessage message)
    {
        if (!_sessions.TryGetValue(peer, out var session))
        {
            return;
        }

        switch (message)
        {
            case ClientHelloMessage hello:
                Log.Info($"Client hello from {FormatPeer(peer)}: {hello.ClientName}.");
                break;
            case LoginRequestMessage login:
                BeginLogin(peer, session, login);
                break;
            case MoveInputMessage move:
                if (session.IsAuthenticated)
                {
                    session.SetDirection(move.Direction);
                }
                break;
            case ChatSendMessage chat:
                if (session.IsAuthenticated)
                {
                    HandleChat(session, chat.Text);
                }
                break;
            default:
                TrySend(peer, new ServerErrorMessage("unsupported_message", $"Unsupported {message.Type}."), DeliveryMethod.ReliableOrdered);
                break;
        }
    }

    private void BeginLogin(NetPeer peer, ClientSession session, LoginRequestMessage login)
    {
        if (session.IsAuthenticated || session.LoginInProgress)
        {
            return;
        }

        session.LoginInProgress = true;
        var loginStartedAt = Stopwatch.GetTimestamp();
        _ = Task.Run(async () =>
        {
            try
            {
                var character = await _characters.LoadOrCreateAsync(login.AccountName, login.DisplayName, CancellationToken.None);
                _mainThreadActions.Enqueue(() =>
                {
                    if (!_sessions.TryGetValue(peer, out var current))
                    {
                        return;
                    }

                    try
                    {
                        var role = ResolveRole(login.AccountName, character.DisplayName);
                        current.Authenticate(_networkIds.Rent(), character.CharacterId, character.DisplayName, role, character.ZoneId, character.Position);
                        _metrics.RecordLogin(true, Stopwatch.GetElapsedTime(loginStartedAt));
                        TrySend(peer, new LoginResultMessage(true, character.CharacterId, character.DisplayName, role, character.Position, ""), DeliveryMethod.ReliableOrdered);
                        Log.Info($"Authenticated {character.DisplayName} ({character.CharacterId}) as {role}.");
                    }
                    catch (Exception exception)
                    {
                        current.LoginInProgress = false;
                        _metrics.RecordLogin(false, Stopwatch.GetElapsedTime(loginStartedAt));
                        TrySend(peer, new LoginResultMessage(false, Guid.Empty, login.DisplayName, ClientRole.Player, WorldVector.Zero, "No network id available."), DeliveryMethod.ReliableOrdered);
                        Log.Error("Login failed", exception);
                    }
                });
            }
            catch (Exception exception)
            {
                _mainThreadActions.Enqueue(() =>
                {
                    if (_sessions.TryGetValue(peer, out var current))
                    {
                        current.LoginInProgress = false;
                    }

                    _metrics.RecordLogin(false, Stopwatch.GetElapsedTime(loginStartedAt));
                    TrySend(peer, new LoginResultMessage(false, Guid.Empty, login.DisplayName, ClientRole.Player, WorldVector.Zero, exception.Message), DeliveryMethod.ReliableOrdered);
                    Log.Error("Login failed", exception);
                });
            }
        });
    }

    private void Tick(float deltaSeconds, TickBudgetRecorder tickBudget)
    {
        _runtimeGuard.TryRun("tick", () => TickCore(deltaSeconds, tickBudget));
    }

    private void TickCore(float deltaSeconds, TickBudgetRecorder tickBudget)
    {
        _serverTick++;

        using (tickBudget.Measure(TickBudgetCategory.Movement))
        {
            foreach (var session in _sessions.Values)
            {
                if (session.IsAuthenticated)
                {
                    session.Advance(deltaSeconds, _options.MovementUnitsPerSecond, _options.WorldBounds);
                }
            }
        }

        BroadcastSnapshot(tickBudget);

        if (_serverTick % (uint)(_options.TickRate * 10) == 0)
        {
            using (tickBudget.Measure(TickBudgetCategory.Other))
            {
                Log.Info($"tick={_serverTick} peers={_sessions.Count} players={_sessions.Values.Count(x => x.IsAuthenticated)}");
            }
        }
    }

    private void BroadcastSnapshot(TickBudgetRecorder tickBudget)
    {
        var authenticated = _sessions.Values
            .Where(session => session.IsAuthenticated)
            .ToArray();

        foreach (var session in authenticated)
        {
            _runtimeGuard.TryRun($"snapshot for {session.DisplayName} #{session.NetworkId}", () => BroadcastSnapshotToSession(session, authenticated, tickBudget));
        }
    }

    private void BroadcastSnapshotToSession(ClientSession session, IReadOnlyCollection<ClientSession> authenticated, TickBudgetRecorder tickBudget)
    {
        IReadOnlyList<ClientSession> visible;
        HashSet<uint> visibleIds;
        using (tickBudget.Measure(TickBudgetCategory.Aoi))
        {
            visible = SelectVisibleSessions(session, authenticated);
            visibleIds = visible.Select(entity => entity.NetworkId).ToHashSet();
        }

        using (tickBudget.Measure(TickBudgetCategory.Network))
        {
            SendEntityDespawns(session, visibleIds);
            EnsureEntitySpawns(session, visible);
        }

        IReadOnlyList<byte[]> packets;
        int visibleCount;
        using (tickBudget.Measure(TickBudgetCategory.Serialize))
        {
            packets = BuildSnapshotPackets(session, visible, out visibleCount);
        }

        var sentBytes = 0;
        var sentPackets = 0;
        using (tickBudget.Measure(TickBudgetCategory.Network))
        {
            foreach (var packet in packets)
            {
                if (TrySend(session.Peer, packet, DeliveryMethod.Unreliable))
                {
                    sentBytes += packet.Length;
                    sentPackets++;
                }
            }
        }

        if (sentPackets > 0)
        {
            _metrics.RecordSnapshotSent(sentBytes, visibleCount, authenticated.Count);
        }

        if ((visibleCount < authenticated.Count || sentPackets > 1) && _serverTick % (uint)(_options.TickRate * 5) == 0)
        {
            Log.Info($"snapshot for {session.DisplayName}: visible={visibleCount}/{authenticated.Count}, radius={_options.InterestRadius:0.#}, chunks={sentPackets}/{packets.Count}, bytes={sentBytes}");
        }
    }

    private void SendEntityDespawns(ClientSession recipient, IReadOnlySet<uint> visibleIds)
    {
        foreach (var networkId in recipient.SnapshotEntitiesMissingFrom(visibleIds))
        {
            TrySend(recipient.Peer, new EntityDespawnMessage(_serverTick, networkId), DeliveryMethod.ReliableOrdered);
        }
    }

    private IReadOnlyList<ClientSession> SelectVisibleSessions(
        ClientSession recipient,
        IReadOnlyCollection<ClientSession> authenticated)
    {
        var radiusSquared = _options.InterestRadius * _options.InterestRadius;
        return authenticated
            .Where(candidate => candidate.ZoneId == recipient.ZoneId)
            .Select(candidate => new
            {
                Session = candidate,
                DistanceSquared = DistanceSquared(recipient, candidate)
            })
            .Where(candidate => candidate.Session.CharacterId == recipient.CharacterId || candidate.DistanceSquared <= radiusSquared)
            .OrderBy(candidate => SnapshotSortKey(recipient, candidate.Session, candidate.DistanceSquared))
            .Take(_options.MaxVisibleEntities)
            .Select(candidate => candidate.Session)
            .ToArray();
    }

    private IReadOnlyList<byte[]> BuildSnapshotPackets(
        ClientSession recipient,
        IReadOnlyCollection<ClientSession> visible,
        out int visibleCount)
    {
        var ordered = visible
            .OrderBy(session => SnapshotSortKey(recipient, session, DistanceSquared(recipient, session)))
            .Select(ToEntityStateSnapshot)
            .ToArray();

        var chunks = new List<List<EntityStateSnapshot>>();
        var current = new List<EntityStateSnapshot>();
        var currentBytes = ProtocolHeaderBytes + SnapshotHeaderBytes;

        foreach (var entity in ordered)
        {
            var entityBytes = EstimateEntityStateBytes();
            if (current.Count > 0 && currentBytes + entityBytes > MaxSequencedSnapshotBytes)
            {
                chunks.Add(current);
                current = [];
                currentBytes = ProtocolHeaderBytes + SnapshotHeaderBytes;
            }

            current.Add(entity);
            currentBytes += entityBytes;
        }

        if (current.Count > 0 || chunks.Count == 0)
        {
            chunks.Add(current);
        }

        var packets = new List<byte[]>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var packet = ProtocolCodec.Encode(new WorldSnapshotMessage(
                _serverTick,
                ordered.Length,
                true,
                i,
                chunks.Count,
                chunk));

            if (packet.Length > MaxSequencedSnapshotBytes)
            {
                Log.Warn($"Snapshot chunk exceeded budget for {recipient.DisplayName}: chunk={i + 1}/{chunks.Count}, bytes={packet.Length}.");
            }

            packets.Add(packet);
        }

        recipient.RememberSnapshotEntities(ordered.Select(entity => entity.NetworkId));
        visibleCount = ordered.Length;
        return packets;
    }

    private void EnsureEntitySpawns(ClientSession recipient, IReadOnlyCollection<ClientSession> authenticated)
    {
        foreach (var session in authenticated)
        {
            if (recipient.KnowsEntity(session.NetworkId))
            {
                continue;
            }

            TrySend(recipient.Peer, new EntitySpawnMessage(
                session.NetworkId,
                session.CharacterId,
                EntityKind.Player,
                session.DisplayName,
                session.Position), DeliveryMethod.ReliableOrdered);
            recipient.RememberKnownEntity(session.NetworkId);
        }
    }

    private static float SnapshotSortKey(ClientSession recipient, ClientSession candidate, float distanceSquared)
    {
        if (candidate.CharacterId == recipient.CharacterId)
        {
            return -1;
        }

        return recipient.WasInLastSnapshot(candidate.NetworkId)
            ? distanceSquared - SnapshotRetentionBonusDistanceSquared
            : distanceSquared;
    }

    private static float DistanceSquared(ClientSession a, ClientSession b)
    {
        var dx = b.Position.X - a.Position.X;
        var dy = b.Position.Y - a.Position.Y;
        return (dx * dx) + (dy * dy);
    }

    private static EntityStateSnapshot ToEntityStateSnapshot(ClientSession session)
    {
        return new EntityStateSnapshot(session.NetworkId, session.Position);
    }

    private static int EstimateEntityStateBytes()
    {
        return EntityStateFixedBytes;
    }

    private void HandleChat(ClientSession sender, string text)
    {
        var safeText = text.Trim();
        if (safeText.StartsWith("/", StringComparison.Ordinal))
        {
            HandleCommand(sender, safeText);
            return;
        }

        BroadcastChat(sender, safeText);
    }

    private void BroadcastChat(ClientSession sender, string text)
    {
        var safeText = text.Trim();
        if (safeText.Length == 0)
        {
            return;
        }

        if (safeText.Length > 240)
        {
            safeText = safeText[..240];
        }

        var broadcast = new ChatBroadcastMessage(sender.DisplayName, safeText);
        foreach (var session in _sessions.Values)
        {
            if (session.IsAuthenticated && session.ZoneId == sender.ZoneId)
            {
                TrySend(session.Peer, broadcast, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    private void HandleCommand(ClientSession sender, string commandLine)
    {
        var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts.Length == 0 ? "" : parts[0].TrimStart('/').ToLowerInvariant();

        if (command is "help" or "?")
        {
            SendSystem(sender, sender.Role == ClientRole.Admin
                ? "commands: /help, /role, /who, /metrics, /stress, /stress status, /stress start [clients] [duration], /stress stop"
                : "commands: /help, /role. Admin commands require role Admin.");
            return;
        }

        if (command == "role")
        {
            SendSystem(sender, $"role: {sender.Role}");
            return;
        }

        if (sender.Role != ClientRole.Admin)
        {
            SendSystem(sender, "command denied: role Admin required.");
            Log.Warn($"Denied command from {sender.DisplayName}: {commandLine}");
            return;
        }

        switch (command)
        {
            case "who":
                SendSystem(sender, FormatWho());
                break;
            case "metrics":
                SendSystem(sender, _metrics.FormatStateSummary(
                    _sessions.Count,
                    _sessions.Values.Count(session => session.IsAuthenticated),
                    _serverTick,
                    _syntheticLoad.Status()));
                SendSystem(sender, _metrics.FormatWindowSummary(TimeSpan.FromSeconds(5)));
                SendSystem(sender, _metrics.FormatWindowSummary(TimeSpan.FromSeconds(60)));
                SendSystem(sender, _metrics.FormatTotalSummary());
                SendSystem(sender, _metrics.FormatMessageSummary());
                break;
            case "stress":
                HandleStressCommand(sender, parts);
                break;
            default:
                SendSystem(sender, $"unknown command: /{command}. Try /help.");
                break;
        }
    }

    private void HandleStressCommand(ClientSession sender, string[] parts)
    {
        var subcommand = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "start";
        switch (subcommand)
        {
            case "status":
                SendSystem(sender, _syntheticLoad.Status());
                break;
            case "stop":
                SendSystem(sender, _syntheticLoad.Stop());
                Log.Info($"{sender.DisplayName} stopped synthetic load.");
                break;
            case "start":
                StartSyntheticLoad(sender, parts);
                break;
            default:
                SendSystem(sender, $"usage: /stress | /stress status | /stress start [clients] [duration] | /stress stop. Default: /stress start {DefaultStressClientCount} {FormatDuration(DefaultStressDuration)}.");
                break;
        }
    }

    private void StartSyntheticLoad(ClientSession sender, string[] parts)
    {
        const int maxClients = 200;
        var clientCount = parts.Length >= 3 && int.TryParse(parts[2], out var parsedCount)
            ? parsedCount
            : DefaultStressClientCount;
        clientCount = Math.Clamp(clientCount, 1, maxClients);

        var duration = parts.Length >= 4 && TryParseDuration(parts[3], out var parsedDuration)
            ? parsedDuration
            : DefaultStressDuration;
        duration = ClampDuration(duration, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10));

        _syntheticLoad.Start(clientCount, duration, _options.Port, _options.ConnectionKey);
        SendSystem(sender, $"stress started: clients={clientCount}, duration={FormatDuration(duration)}.");
        Log.Info($"{sender.DisplayName} started synthetic load: clients={clientCount}, duration={FormatDuration(duration)}.");
    }

    private string FormatWho()
    {
        var players = _sessions.Values
            .Where(session => session.IsAuthenticated)
            .OrderByDescending(session => session.Role)
            .ThenBy(session => session.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(session => $"{session.DisplayName}({session.Role}, {session.LastLatencyMs}ms)")
            .ToArray();

        return players.Length == 0
                ? "who: no authenticated players."
                : $"who: {string.Join(", ", players)}";
    }

    private void SendSystem(ClientSession session, string text)
    {
        TrySend(session.Peer, new ChatBroadcastMessage("server", text), DeliveryMethod.ReliableOrdered);
    }

    private ClientRole ResolveRole(string accountName, string displayName)
    {
        return _options.AdminNames.Contains(accountName) || _options.AdminNames.Contains(displayName)
            ? ClientRole.Admin
            : ClientRole.Player;
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        value = value.Trim();
        var lower = value.ToLowerInvariant();

        if (lower.EndsWith("ms", StringComparison.Ordinal)
            && double.TryParse(lower[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
        {
            duration = TimeSpan.FromMilliseconds(milliseconds);
            return true;
        }

        if (lower.EndsWith("s", StringComparison.Ordinal)
            && double.TryParse(lower[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            duration = TimeSpan.FromSeconds(seconds);
            return true;
        }

        if (lower.EndsWith("m", StringComparison.Ordinal)
            && double.TryParse(lower[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
        {
            duration = TimeSpan.FromMinutes(minutes);
            return true;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bareSeconds))
        {
            duration = TimeSpan.FromSeconds(bareSeconds);
            return true;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out duration))
        {
            return true;
        }

        duration = TimeSpan.Zero;
        return false;
    }

    private static TimeSpan ClampDuration(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalSeconds < 60
            ? $"{duration.TotalSeconds:0.#}s"
            : $"{duration.TotalMinutes:0.#}m";
    }

    private bool TrySend(NetPeer peer, IProtocolMessage message, DeliveryMethod deliveryMethod)
    {
        try
        {
            var packet = ProtocolCodec.Encode(message);
            peer.Send(packet, 0, deliveryMethod);
            _metrics.RecordSent(message, packet.Length);
            return true;
        }
        catch (Exception exception)
        {
            _metrics.RecordSendFailure();
            Log.Warn($"Failed to send {message.Type} to {FormatPeer(peer)}: {exception.Message}");
            return false;
        }
    }

    private bool TrySend(NetPeer peer, byte[] packet, DeliveryMethod deliveryMethod)
    {
        try
        {
            peer.Send(packet, 0, deliveryMethod);
            return true;
        }
        catch (Exception exception)
        {
            _metrics.RecordSendFailure();
            Log.Warn($"Failed to send {packet.Length} bytes to {FormatPeer(peer)}: {exception.Message}");
            return false;
        }
    }

    private static string FormatPeer(NetPeer peer)
    {
        return $"{peer.Address}:{peer.Port}";
    }

    private void DrainMainThreadActions()
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Log.Error("Main-thread action failed", exception);
            }
        }
    }

    private void PersistConnectedPlayers()
    {
        foreach (var session in _sessions.Values.Where(session => session.IsAuthenticated))
        {
            SavePositionBestEffort(session);
        }
    }

    private void SavePositionBestEffort(ClientSession session)
    {
        try
        {
            _characters.SavePositionAsync(session.CharacterId, session.Position, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to persist {session.DisplayName}", exception);
        }
    }
}
