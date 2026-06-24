using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// LIVING-ENEMIES P3: a PERSISTENT server-side spawner that OWNS a monster. It is a plain server object (NOT a
// replicated world entity): it sits at a fixed tile, holds a monster TYPE, and manages a single live monster's
// life cycle — spawn it, and when it dies schedule a respawn and spawn a fresh full-HP one of the same type at the
// same tile after the delay. The spawner OUTLIVES its monster's death/respawn (the monster's network id is reborn
// each time; the spawner id is stable), which is exactly what lets the red marker tile stay put across a kill.
//
// The red de-aggro/leash anchor the client paints is THIS spawner's tile (replicated via SpawnerMarkerMessage,
// keyed by SpawnerId) — the monster's leash HOME equals Tile. `/monster <name>` now creates a spawner instead of a
// transient monster + a per-monster home; the spawner spawns the first monster.
//
// CAPACITY: it holds <= 1 live monster for now (LiveMonsterId), but the shape keeps the door open to N — the death
// /respawn bookkeeping is per-monster and the owner (GameServer) drives it, so growing to a small pool is additive.
public sealed class MonsterSpawner
{
    public MonsterSpawner(uint spawnerId, TileCoord tile, MonsterType type)
    {
        SpawnerId = spawnerId;
        Tile = tile;
        Type = type;
    }

    // Stable id (rented from a server-side counter) keying the replicated red-tile marker. Distinct from any monster
    // network id — it never changes for the spawner's life, so the marker survives a kill+respawn.
    public uint SpawnerId { get; }

    // The fixed tile the spawner sits on (= the leash home of whatever monster it currently owns) and where the red
    // marker is painted.
    public TileCoord Tile { get; }

    // The monster TYPE this spawner produces (slime for now). Read live each respawn so a type retune applies to the
    // NEXT spawned monster.
    public MonsterType Type { get; }

    // The entity id of the currently-live monster, or null when the spawner's monster is dead and (maybe) waiting to
    // respawn. Set by the owner (GameServer) on spawn, cleared on death.
    public ulong? LiveMonsterId { get; private set; }

    // The server tick at which the dead monster should respawn, or null when a monster is alive (nothing pending).
    // Set on death (= death tick + the type's respawn delay in ticks); cleared when the respawn fires.
    public uint? RespawnAtTick { get; private set; }

    // True iff a respawn is pending and its due tick has arrived. The owner polls this each tick for the spawners it
    // tracks and, on a hit, spawns a fresh monster (which clears the schedule via AttachMonster).
    public bool IsRespawnDue(uint serverTick) =>
        LiveMonsterId is null && RespawnAtTick.HasValue && serverTick >= RespawnAtTick.Value;

    // Records that `monsterId` is now this spawner's live monster, clearing any pending respawn. Called by the owner
    // right after it spawns the entity (initial spawn AND each respawn).
    public void AttachMonster(ulong monsterId)
    {
        LiveMonsterId = monsterId;
        RespawnAtTick = null;
    }

    // Records that the spawner's live monster died at `serverTick`: drop the live id and schedule the respawn
    // `respawnDelayTicks` later. No-op if the dead id is not the one we own (a stale notification). Returns true iff
    // it matched + scheduled.
    public bool NotifyMonsterDied(ulong monsterId, uint serverTick, uint respawnDelayTicks)
    {
        if (LiveMonsterId != monsterId)
        {
            return false;
        }

        LiveMonsterId = null;
        RespawnAtTick = serverTick + respawnDelayTicks;
        return true;
    }
}
