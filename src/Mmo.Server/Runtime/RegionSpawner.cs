using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// ECOLOGY E2 (docs/ecology-v1-design.md §3/§8 E2): owns ONE region×type's DERIVED spawn geography (from
// RegionSpawnPlanner, computed once at boot) + its live monster set. Distinct from the legacy MonsterSpawner
// (D10 "the /monster dev command stays" — orphan spawners keep their OWN timer-respawn path untouched): a
// RegionSpawner holds MANY live monsters (up to its effective maxLive) and has NO respawn timer at all —
// repopulation flows ONLY from EcologyState's stock via GameServer.MaterializeRegionSpawners. It is a plain
// server object (not a replicated world entity, like MonsterSpawner) — nothing about a region's spawn geometry
// is wire-visible in E2 (that's E4's RegionEcologyMessage).
//
// TICK-AGNOSTIC (mirrors EcologyState / TelegraphScheduler): every method takes serverTick as a parameter
// rather than reading a live clock, so materialization is headlessly testable in a plain tick-count loop with
// no real-time wait.
public sealed class RegionSpawner
{
    private readonly HashSet<ulong> _liveMonsterIds = [];
    private int _nextSpawnTileIndex;
    private uint _nextEligibleSpawnTick; // 0 = eligible immediately (the world starts already populated at K).

    public RegionSpawner(string regionId, string typeId, MonsterType type, int baseMaxLive, IReadOnlyList<TileCoord> spawnTiles)
    {
        RegionId = regionId;
        TypeId = typeId;
        Type = type;
        BaseMaxLive = baseMaxLive;
        SpawnTiles = spawnTiles;
    }

    // The ecology region id + monster type id this spawner materializes (EcologyState.RecordKill/StockOf/StateOf
    // are keyed off exactly these two strings).
    public string RegionId { get; }
    public string TypeId { get; }

    // The resolved MonsterType this spawner spawns (looked up from MonsterTypeRegistry once at boot by TypeId).
    public MonsterType Type { get; }

    // The AUTHORED maxLive (EcologyTypeConfig.MaxLive) — D7's overgrown effectiveMaxLive is computed FROM this
    // at materialization time (1.5x, ceil), never mutated here.
    public int BaseMaxLive { get; }

    // The DERIVED spawn tiles (RegionSpawnPlanner, boot-time, deterministic). May be EMPTY (a region entirely
    // outside a smaller test/procedural zone) — TryTakeNextTile below degrades to "never spawns" in that case.
    public IReadOnlyList<TileCoord> SpawnTiles { get; }

    public IReadOnlyCollection<ulong> LiveMonsterIds => _liveMonsterIds;
    public int LiveCount => _liveMonsterIds.Count;

    public bool IsSpawnPacingDue(uint serverTick) => serverTick >= _nextEligibleSpawnTick;

    // Arms the pacing gate for `pacingTicks` from now — called once per ATTEMPT (whether or not the attempt
    // actually spawned a monster), so a permanently player-camped tile doesn't turn into a busy-loop: the
    // spawner tries again in one pacing window, at the NEXT tile (the cursor always advances in TryTakeNextTile).
    public void ArmPacing(uint serverTick, uint pacingTicks) => _nextEligibleSpawnTick = serverTick + pacingTicks;

    // Round-robins through the derived spawn tiles, ALWAYS advancing the cursor (even if the caller ends up
    // skipping this tile for a nearby player) so repeated attempts sweep the whole set rather than fixating on
    // one blocked spot. False (tile left default) if this region×type has no derived spawn tiles at all.
    public bool TryTakeNextTile(out TileCoord tile)
    {
        if (SpawnTiles.Count == 0)
        {
            tile = default;
            return false;
        }

        tile = SpawnTiles[_nextSpawnTileIndex];
        _nextSpawnTileIndex = (_nextSpawnTileIndex + 1) % SpawnTiles.Count;
        return true;
    }

    public void AddLiveMonster(ulong monsterId) => _liveMonsterIds.Add(monsterId);

    public void RemoveLiveMonster(ulong monsterId) => _liveMonsterIds.Remove(monsterId);

    // /clearspawners (D10): drop every tracked live monster id WITHOUT touching SpawnTiles/pacing/cursor — the
    // caller (GameServer.ClearRegionSpawnerMonsters) has already despawned the world entities; this just empties
    // the bookkeeping so the set can never leak a stale id.
    public void ClearLiveMonsters() => _liveMonsterIds.Clear();
}
