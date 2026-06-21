namespace Mmo.Shared.Protocol;

public enum MessageType : ushort
{
    ClientHello = 1,
    LoginRequest = 2,
    MoveIntent = 3,
    ChatSend = 4,
    SnapshotAck = 5,
    InteractRequest = 6,
    AdminSetTuning = 7,
    StepCommitRequest = 8,
    MovementMode = 9,
    MoveInput = 10,
    StepCommitBatch = 11,
    // COMBAT-S1: admin-gated client->server "set my local player's current vital" verb (dev-set window).
    AdminSetStat = 12,

    ServerHello = 100,
    LoginResult = 101,
    WorldSnapshot = 102,
    ChatBroadcast = 103,
    ServerError = 104,
    EntitySpawn = 105,
    EntityDespawn = 106,
    ZoneInfo = 107,
    InteractResult = 108,
    InventoryUpdate = 109,
    MovementSpeedChanged = 110,
    // COMBAT-S1: server->owner replication of the local player's vitals (HP/mana/stamina, current+max).
    PlayerStats = 111
}
