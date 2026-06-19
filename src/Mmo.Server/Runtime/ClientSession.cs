using LiteNetLib;
using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

public sealed class ClientSession
{
    private readonly HashSet<uint> _lastSnapshotEntityIds = [];
    private readonly HashSet<uint> _knownEntityIds = [];
    private readonly Dictionary<uint, uint> _sentEntityRevisions = [];
    private uint _nextSnapshotSequence = 1;
    private uint _lastFullSnapshotSentTick;
    private uint? _lastInteractTick;

    // Minimum ticks between accepted Interact requests from one client. Cheap flood guard so a client
    // cannot spam the interaction path within a single tick or hammer it across consecutive ticks;
    // depleting a node already gates legitimate re-harvest for far longer.
    private const uint InteractCooldownTicks = 4;

    public ClientSession(NetPeer peer)
    {
        Peer = peer;
    }

    public NetPeer Peer { get; }
    public bool IsAuthenticated { get; private set; }
    public bool LoginInProgress { get; set; }
    public ulong? EntityId { get; private set; }
    public uint NetworkId { get; private set; }
    public Guid CharacterId { get; private set; }
    public string DisplayName { get; private set; } = "unknown";
    public ClientRole Role { get; private set; } = ClientRole.Player;
    public string ZoneId { get; private set; } = "sandbox";
    public int LastLatencyMs { get; set; }
    public int BadPacketCount { get; private set; }
    public uint LastAcknowledgedSnapshotSequence { get; private set; }

    public void Authenticate(uint networkId, Guid characterId, string displayName, ClientRole role, string zoneId)
    {
        NetworkId = networkId;
        CharacterId = characterId;
        DisplayName = displayName;
        Role = role;
        ZoneId = zoneId;
        IsAuthenticated = true;
        LoginInProgress = false;
    }

    public void AttachEntity(WorldEntity entity)
    {
        EntityId = entity.Id;
        NetworkId = entity.NetworkId;
    }

    public int RecordBadPacket()
    {
        BadPacketCount++;
        return BadPacketCount;
    }

    // Returns true and arms the cooldown if an Interact request is allowed at this tick; false if the
    // client is still inside its interaction cooldown window.
    public bool TryConsumeInteract(uint serverTick)
    {
        if (_lastInteractTick.HasValue && serverTick - _lastInteractTick.Value < InteractCooldownTicks)
        {
            return false;
        }

        _lastInteractTick = serverTick;
        return true;
    }

    public bool KnowsEntity(uint networkId)
    {
        return _knownEntityIds.Contains(networkId);
    }

    public void RememberKnownEntity(uint networkId)
    {
        _knownEntityIds.Add(networkId);
    }

    public void ForgetKnownEntity(uint networkId)
    {
        _knownEntityIds.Remove(networkId);
    }

    public bool WasInLastSnapshot(uint networkId)
    {
        return _lastSnapshotEntityIds.Contains(networkId);
    }

    public IEnumerable<uint> SnapshotEntitiesMissingFrom(IReadOnlySet<uint> currentNetworkIds)
    {
        foreach (var networkId in _lastSnapshotEntityIds)
        {
            if (!currentNetworkIds.Contains(networkId))
            {
                yield return networkId;
            }
        }
    }

    public void CollectSnapshotEntitiesMissingFrom(IReadOnlySet<uint> currentNetworkIds, ICollection<uint> destination)
    {
        foreach (var networkId in _lastSnapshotEntityIds)
        {
            if (!currentNetworkIds.Contains(networkId))
            {
                destination.Add(networkId);
            }
        }
    }

    public void RememberSnapshotEntities(IEnumerable<uint> networkIds)
    {
        _lastSnapshotEntityIds.Clear();
        foreach (var networkId in networkIds)
        {
            _lastSnapshotEntityIds.Add(networkId);
        }
    }

    public void RememberSnapshotEntities(IReadOnlyList<WorldEntity> entities)
    {
        _lastSnapshotEntityIds.Clear();
        foreach (var entity in entities)
        {
            _lastSnapshotEntityIds.Add(entity.NetworkId);
        }
    }

    public bool ShouldSendFullSnapshot(uint serverTick, uint heartbeatTicks)
    {
        if (heartbeatTicks <= 1)
        {
            return true;
        }

        if (serverTick % heartbeatTicks != NetworkId % heartbeatTicks)
        {
            return false;
        }

        return _lastFullSnapshotSentTick == 0 || serverTick - _lastFullSnapshotSentTick >= heartbeatTicks;
    }

    public bool HasSentRevision(WorldEntity entity)
    {
        return _sentEntityRevisions.TryGetValue(entity.NetworkId, out var revision)
            && revision == entity.StateRevision;
    }

    public void RememberSentRevision(WorldEntity entity)
    {
        _sentEntityRevisions[entity.NetworkId] = entity.StateRevision;
    }

    public void ForgetSentRevision(uint networkId)
    {
        _sentEntityRevisions.Remove(networkId);
    }

    public void RememberSnapshotSent(uint serverTick, bool isComplete)
    {
        if (isComplete)
        {
            _lastFullSnapshotSentTick = serverTick;
        }
    }

    public uint NextSnapshotSequence()
    {
        return _nextSnapshotSequence++;
    }

    public void AcknowledgeSnapshot(uint snapshotSequence)
    {
        if (snapshotSequence > LastAcknowledgedSnapshotSequence)
        {
            LastAcknowledgedSnapshotSequence = snapshotSequence;
        }
    }

}
