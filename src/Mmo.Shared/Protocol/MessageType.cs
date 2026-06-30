namespace Mmo.Shared.Protocol;

public enum MessageType : ushort
{
    ClientHello = 1,
    LoginRequest = 2,
    // CONTINUOUS MIGRATION (Phase 3, v36): MoveIntent is now the per-INPUT continuous move (one per client frame):
    // {uint InputSeq, float DirX, float DirY, float DtSeconds}. The server integrates each fresh input by its dt on
    // the receive path (anti-speedhack: per-input dt clamp + a per-peer wall-clock dt budget). Reuses tag 3.
    MoveIntent = 3,
    ChatSend = 4,
    SnapshotAck = 5,
    InteractRequest = 6,
    AdminSetTuning = 7,
    // CONTINUOUS MIGRATION (Phase 3, v36): the tile-step commit/mode/move-input machinery is DELETED (per-input
    // continuous movement replaces it). Tags 8 (StepCommitRequest), 9 (MovementMode), 10 (MoveInput), 11
    // (StepCommitBatch) are left as numeric GAPS — survivors are NOT renumbered so the rest of the catalogue is stable.
    // COMBAT-S1: admin-gated client->server "set my local player's current vital" verb (dev-set window).
    AdminSetStat = 12,
    // COMBAT-S2B: client->server attack action (its OWN dedup cursor, never movement's). Reliable-ordered.
    Attack = 13,
    // LOOT P4c (v35): client->server loot-window verb on a corpse the player has OPEN — take ONE stack by template
    // key, take ALL, or CLOSE the window. (OPENING the window reuses the existing InteractRequest on a corpse.)
    // Reliable-ordered: a dropped take/close must not be lost. See LootActionMessage.
    LootAction = 14,
    // MOVEMENT-ACTIONS Phase B1 (v38): client->server action trigger (jump now) on its OWN dedup cursor, SEPARATE from
    // both movement AND attack (the NET6 "two streams, one cursor" lesson — a third stream gets a third cursor). Mirrors
    // Attack: reliable-ordered, low-rate, carries an authored tick (rides the wire for B2; B1 anchors server-side). Tag
    // 15 is the next free client->server tag (8-11 are the deleted tile-step gaps, 12-14 are AdminSetStat/Attack/Loot).
    // See ActionIntentMessage.
    ActionIntent = 15,
    // MONSTER-TUNING-SAVE (v42): admin-gated, parameterless command — PERSIST the current live-tuned monster TYPE values
    // back to the data manifest (Content/monsters.json) so they survive a restart (AdminSetTuning is in-memory only).
    // Tag 16 is the next free client->server tag (8-11 are the deleted tile-step gaps). See SaveMonsterTuningMessage.
    SaveMonsterTuning = 16,

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
    // LIVING-ENEMIES P3 (v34): server->viewer replication of a SPAWNER's red-tile marker — the PERSISTENT leash/
    // de-aggro anchor that OWNS + respawns a monster. Replaces the former per-monster MonsterHome (v33): the marker is
    // keyed by a stable SPAWNER id (not the monster's network id, which changes on each death/respawn), and carries an
    // Active flag — Active=true when the spawner enters a viewer's AOI (show/place the red tile), Active=false when it
    // leaves (drop it). The marker therefore STAYS PUT while the monster dies and a fresh one respawns. Reliable.
    SpawnerMarker = 115,
    // LOOT P4c (v35): server->owner replication of an OPEN corpse's contents — the rolled stacks (template key +
    // quantity + rarity tier) the loot window lists, rarity-coloured. Sent eligibility-gated when the player opens a
    // corpse (InteractRequest on it) and re-sent after each take/loot-all so the window reflects the live remaining
    // contents; Open=false tells the client to CLOSE the window (last item taken / out of range / corpse gone).
    // Owner-only + reliable-ordered (like InventoryUpdate — corpse loot never AOI-replicates). See CorpseContentsMessage.
    CorpseContents = 116
}
