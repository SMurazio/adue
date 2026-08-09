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
    // PLAYER-COLLISION-TOGGLE (v43): admin-gated client->server request to flip whether OTHER PLAYERS are collision
    // obstacles (player↔player collision on/off). Server-authoritative + broadcast so the client predictor's obstacle
    // gather and the server integrator's gather flip TOGETHER (prediction parity — a client-only flag would desync).
    // Tag 17 is the next free client->server tag (8-11 are the deleted tile-step gaps). See AdminSetPlayerCollisionMessage.
    AdminSetPlayerCollision = 17,
    // NODE-FIELD N2 (v46, docs/node-field-design.md D5): client->server harvest request targeting a catalogue
    // INDEX (never a network id — harvestable nodes are no longer entities). Tag 18 is the next free
    // client->server tag (8-11 are the deleted tile-step gaps). See HarvestNodeMessage.
    HarvestNode = 18,
    // DUO-SKILLSHOT (v47, exp/duo-abilities): client->server "fire my fusion skillshot toward this aim". Its OWN
    // dedicated dedup cursor (_lastFireSeq), independent of the move/attack/action cursors (the NET6 third-stream
    // lesson). Reliable-ordered + low-rate. Tag 19 is the next free client->server tag. See FireSkillshotMessage.
    FireSkillshot = 19,
    // DUO-SKILLSHOT (v47): the aim-preview relay. Sent client->server while a player HOLDS the fire key (throttled
    // ~8Hz, only while a partner exists); the server relays it ONLY to the partner (ShooterNetworkId filled in) so
    // both clients draw the faint intercept-preview line. Sent UNRELIABLE (a dropped preview frame is superseded by
    // the next). Tag 20 is the next free client->server tag; the type doubles as the server->partner relay. See
    // AimPreviewMessage.
    AimPreview = 20,
    // DUO-WAVE2 (v48, exp/duo-abilities): the ONE client->server trigger for co-op abilities 2-4 — R=Shield,
    // G=TetherToggle, V=Detonate (DuoAbilityMessage carries the selector byte). Its OWN dedicated dedup cursor
    // (_lastDuoSeq), independent of the move/attack/action/fire cursors (the NET6 "one cursor per stream" lesson).
    // Reliable-ordered + low-rate. Tag 21 is the next free client->server tag. See DuoAbilityMessage.
    DuoAbility = 21,
    // ADUE P1 RUN LOOP (v51, todo/S-adue-p1-run-loop-chassis.md): client->server READY-UP toggle — {uint Sequence,
    // bool Ready}. The run's front door (the /boss command survives only as a dev shortcut). Its OWN dedup cursor
    // (_lastRunReadySeq) like every other client->server stream (the NET6 "one cursor per stream" lesson).
    // Reliable-ordered + very low rate. Deliberately NOT in IsSuppressedWhileDead: a player downed inside a run must
    // still be able to un-ready / ready for the NEXT run from the end screen. Tag 22 is the next free client->server
    // tag (8-11 are the deleted tile-step gaps). See RunReadyMessage.
    RunReady = 22,

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
    CorpseContents = 116,
    // PLAYER-COLLISION-TOGGLE (v43): server->client replication of the authoritative player↔player collision flag. Sent
    // on login (initial truth) + broadcast on every change so every client's obstacle gather gates on the SAME value the
    // server integrator does (prediction parity). Monster collision is unaffected — this gates ONLY whether OTHER PLAYERS
    // are obstacles. Reliable-ordered, global (not AOI-scoped). See PlayerCollisionSettingMessage.
    PlayerCollisionSetting = 117,
    // TELEGRAPH T2 (v44, docs/ability-telegraph-sync-design.md): server->client announcement of a SCHEDULED ground
    // telegraph — {telegraph id, shape (kind + Q12.4 origin + Q12.4 radius), startTick, resolveTick}. The DEADLINE form:
    // clients render the fill as (now − start)/(T − start) against their estimated server clock and self-resolve at T,
    // so caster-long/observer-short latency compensation falls out for free and NO resolve/cancel message exists (a
    // telegraph outlives its caster by the T1 decision, so there is nothing to cancel). Sent reliable-ordered, AOI-scoped
    // per recipient by the SAME known-id diff pass the spawner markers use — which is also what delivers still-active
    // telegraphs to a viewer that enters AOI mid-windup (the late-join case). See TelegraphMessage.
    Telegraph = 118,
    // ECOLOGY E4 (v45, docs/ecology-v1-design.md D5/D6, §3/§8 E4): server->client replication of ONE authored
    // ecology region's current LEGIBLE state — id, display name, rect bounds, and one {typeId, D5 five-state enum}
    // entry per hosted type. NO stock/pressure numbers ride the wire (fuzzy words, never numbers). Sent to every
    // authenticated client: the FULL set (one message per region) on login, and a single re-send of the changed
    // region whenever any of its type-states flips. Global (not AOI-scoped, like PlayerCollisionSetting/MonsterTuning)
    // — pre-walk legibility means every client needs every region regardless of proximity. See RegionEcologyMessage.
    RegionEcology = 119,
    // NODE-FIELD N2 (v46, docs/node-field-design.md D3/D4): server->client announcement that ONE catalogue node's
    // availability flipped (harvest or respawn). Reliable-ordered, GLOBAL (not AOI-scoped). See NodeStateMessage.
    NodeState = 120,
    // NODE-FIELD N2 (v46): sent once on login — the field's full current exception list (only the DEPLETED
    // indices). Reliable-ordered. See NodeStateBatchMessage.
    NodeStateBatch = 121,
    // DUO-SKILLSHOT (v47, exp/duo-abilities): server->client replication of a player's PAIR state — the partner's
    // network id + a paired flag. Sent to BOTH players on /pair (each learns the other's id for previews/cues) and
    // Paired=false to the surviving partner on /unpair or disconnect. Reliable-ordered, owner-only. See
    // PairStatusMessage. Tag 122 is the next free server->client tag.
    PairStatus = 122,
    // DUO-WAVE2 (v48, exp/duo-abilities) ability 2 (Unison Shield): server->client replication of a player's damage-
    // absorption SHIELD — {uint NetworkId, ushort Strength (remaining absorb pool), uint ExpiryTick, bool Active}. Sent
    // to the shielded player AND their partner (so both render the translucent bubble), on arm and on each absorbed
    // hit (the pool shrinks). Active=false / a passed ExpiryTick drops the bubble. Reliable-ordered. See ShieldStatusMessage.
    ShieldStatus = 123,
    // DUO-WAVE2 (v48) abilities 2 & 4: the brief server->partner ECHO CUE — {uint NetworkId, byte Cue} — a flash +
    // expanding ring on the named character so the partner can REACT (shield press / detonate initiate / detonate
    // confirm, per EchoCueKind). Sent UNRELIABLE (a dropped cue is a missed flash, harmless). See EchoCueMessage.
    EchoCue = 124,
    // DUO-WAVE2 (v48) ability 3 (Laser Tether): server->both-partners TETHER state — {uint OwnerNetworkId, uint
    // PartnerNetworkId, byte State} (Off/On/Broken). The client draws the beam between the two named entities and
    // colours it by the distance band it recomputes locally (no band byte on the wire). Reliable-ordered. See
    // TetherStatusMessage.
    TetherStatus = 125,
    // DUO-WAVE2 (v48) ability 4 (Midpoint Detonation): server->both-partners charge marker — {uint ChargeId, Q12.4
    // origin, Q12.4 ushort radius, uint StartTick, uint ResolveTick, bool Active}. A LIVE-TRACKING variant of the
    // telegraph decal: sent EACH charge tick with the current midpoint origin (the pair aims by repositioning), and
    // once with Active=false at resolve. Reliable-ordered. See MidpointChargeMessage. Tag 127 is the next free tag.
    MidpointCharge = 126,
    // BOSS-2 (v49, docs/boss-encounter-sunderer-design.md P1 HUSK): server->client boss PLATING state — {uint
    // BossNetworkId, bool PlatingActive}. PlatingActive=true when the Sunderer's cold steel shell is up (damage
    // reduced — the client tints the boss steel grey), false when it has SHATTERED (a vulnerability window is open) or
    // permanently CRUMBLED below 70% (drop the tint). Broadcast AOI-scoped on plating-on (spawn), shatter, reform, and
    // permanent-off (Laws 4/7 legibility). Reliable-ordered (discrete state edges). See BossPlatingMessage. Tag 128 is
    // the next free server->client tag.
    BossPlating = 127,
    // ADUE P1 RUN LOOP (v51): server->client RUN STATUS — {byte Phase, byte RosterCount, byte ReadyCount, bool
    // SelfReady}. The lobby/ready HUD line and the "a run is live" flag. EDGE-DRIVEN (sent on login and on every
    // phase/ready change), never per-tick — the run clock is not on this message, so nothing has to be re-sent to
    // keep it fresh. Owner-scoped (each recipient's SelfReady differs), reliable-ordered. Phase is range-validated
    // on decode (the ReadAttackKind discipline). See RunStatusMessage. Tag 129 is the next free tag.
    RunStatus = 128,
    // ADUE P1 RUN LOOP (v51): server->client END-OF-RUN SUMMARY — {byte Outcome, uint DurationSeconds, uint
    // DamageDealt, byte BossHealthPercent, byte Deaths}. Sent ONCE per roster member at the moment a run ends
    // (clear or wipe; an Abandoned run has nobody to tell). Everything on it is a counter the server already keeps
    // cheaply — no new instrumentation rides this. Owner-scoped, reliable-ordered. Outcome is range-validated on
    // decode. See RunSummaryMessage. Tag 130 is the next free server->client tag.
    RunSummary = 129
}
