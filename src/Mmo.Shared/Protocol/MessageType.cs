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
    // COMBAT-S2B: client->server attack action (its OWN dedup cursor, never movement's). Reliable-ordered.
    Attack = 13,

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
    PlayerStats = 111,
    // COMBAT-TUNING (v31): server->client replication of the live combat feel-knobs (attack cooldown, swing-root
    // duration, sector half-angle/radius, damage). Sent on login + on every admin tuning change so the client's
    // wedge/predictor/cooldown-viz match the server's resolution. See CombatTuningSnapshot.
    CombatTuning = 112,
    // COMBAT-QOL (v32): server->client cosmetic damage event — emitted when a hit actually reduces an entity's HP,
    // AOI-gated to viewers that can see the victim, so the client floats a "-N" number above it. Presentation only;
    // the authoritative HP still rides the snapshot. Sent UNRELIABLE (a dropped number is harmless). See
    // DamageEventMessage.
    DamageEvent = 113,
    // LIVING-ENEMIES P2-POLISH (v33): server->client replication of the per-monster-TYPE tuning (one entry per named
    // template — slime now). Sent on login + whenever a per-type tuning key changes, so the F1 "Monster" tab can list
    // the types and show + edit the authoritative live values. See MonsterTuningSnapshot.
    MonsterTuning = 114,
    // LIVING-ENEMIES P2-POLISH (v33): server->viewer replication of a monster's leash HOME tile, so the client can
    // paint a RED floor tile there (the de-aggro anchor becomes visible). Sent once when the monster enters a viewer's
    // AOI (alongside its EntitySpawn). Per-monster + reliable; cleared client-side on the monster's despawn.
    MonsterHome = 115
}
