using LiteNetLib;
using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

public sealed class ClientSession
{
    private readonly HashSet<uint> _lastSnapshotEntityIds = [];
    private readonly HashSet<uint> _knownEntityIds = [];
    private uint _nextSnapshotSequence = 1;

    public ClientSession(NetPeer peer)
    {
        Peer = peer;
    }

    public NetPeer Peer { get; }
    public bool IsAuthenticated { get; private set; }
    public bool LoginInProgress { get; set; }
    public uint NetworkId { get; private set; }
    public Guid CharacterId { get; private set; }
    public string DisplayName { get; private set; } = "unknown";
    public ClientRole Role { get; private set; } = ClientRole.Player;
    public string ZoneId { get; private set; } = "sandbox";
    public WorldVector Position { get; private set; } = WorldVector.Zero;
    public WorldVector PendingDirection { get; private set; } = WorldVector.Zero;
    public int LastLatencyMs { get; set; }
    public int BadPacketCount { get; private set; }
    public uint LastAcknowledgedSnapshotSequence { get; private set; }

    public void Authenticate(uint networkId, Guid characterId, string displayName, ClientRole role, string zoneId, WorldVector position)
    {
        NetworkId = networkId;
        CharacterId = characterId;
        DisplayName = displayName;
        Role = role;
        ZoneId = zoneId;
        Position = position;
        IsAuthenticated = true;
        LoginInProgress = false;
    }

    public void SetDirection(WorldVector direction)
    {
        PendingDirection = direction.NormalizeOrZero();
    }

    public int RecordBadPacket()
    {
        BadPacketCount++;
        return BadPacketCount;
    }

    public bool KnowsEntity(uint networkId)
    {
        return _knownEntityIds.Contains(networkId);
    }

    public void RememberKnownEntity(uint networkId)
    {
        _knownEntityIds.Add(networkId);
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

    public void RememberSnapshotEntities(IEnumerable<uint> networkIds)
    {
        _lastSnapshotEntityIds.Clear();
        foreach (var networkId in networkIds)
        {
            _lastSnapshotEntityIds.Add(networkId);
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

    public void Advance(float deltaSeconds, float movementUnitsPerSecond, WorldBounds worldBounds)
    {
        Position = worldBounds.Clamp(Position + (PendingDirection * movementUnitsPerSecond * deltaSeconds));
    }
}
