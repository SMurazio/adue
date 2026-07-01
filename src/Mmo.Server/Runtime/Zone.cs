using Mmo.Shared.Domain;
using Mmo.Server.Configuration;

namespace Mmo.Server.Runtime;

public sealed class Zone
{
    public const string DefaultId = "sandbox";
    public static TileCoord DefaultSpawnTile { get; } = TileGrid.DefaultSpawnTile;

    private readonly TileGrid _tileGrid;
    private readonly TileCoord[] _spawnTiles;
    private int _nextSpawnTileIndex;

    // CONTINUOUS MIGRATION (Phase 2): the reused per-tick nearby-walls scratch buffer for the player continuous
    // integrator (IntegrateMovement). Owned by the Zone and refilled per integrate call (TileGrid.QueryNearbyWalls
    // clears it), so the wall query allocates ZERO per tick. Single-threaded tick loop, so one shared buffer is safe
    // (each IntegrateMovement call fully consumes it before the next entity's call — the Resolve runs synchronously
    // inside the same call). NEVER touched by the monster TryStep path.
    private readonly List<ContinuousCollision.Wall> _wallScratch = new();

    // PLAYER↔MONSTER + PLAYER↔PLAYER COLLISION: the reused per-tick scratch for the player integrator's nearby-body
    // obstacle set (monsters + OTHER players). `_obstacleCandidateScratch` receives the spatial-grid superset
    // (GatherInterestCandidates clears it); the Monster|Player candidates within reach of the swept move (self excluded)
    // are projected into `_obstacleScratch` as Circles and handed to the swept-circle resolver so a player STOPS at /
    // SLIDES along a monster OR another player (never walks through it). Both reused → zero per-tick alloc. Single-threaded
    // tick loop, so one shared pair is safe (each IntegrateMovement fully consumes them before the next entity's call).
    private readonly List<WorldEntity> _obstacleCandidateScratch = new();
    private readonly List<ContinuousCollision.Circle> _obstacleScratch = new();

    // The spatial-grid query radius (TILES) for the player's nearby-body obstacle gather. Two bodies overlap only
    // when their centres are within radius+radius = 1.0 tile; a 2-tile box comfortably covers that plus the sub-tile
    // per-input move delta, so the exact circle-vs-circle test in the resolver never misses a blocking body. A pure
    // perf bound — correctness is the resolver's distance test (the grid returns a superset).
    private const int ObstacleQueryRadiusTiles = 2;

    public Zone(string id, TileGrid tileGrid, IEnumerable<TileCoord> spawnTiles)
        : this(id, tileGrid, spawnTiles, TileGrid.DefaultSeed, TerrainGenerator.CurrentGenVersion)
    {
    }

    public Zone(
        string id,
        TileGrid tileGrid,
        IEnumerable<TileCoord> spawnTiles,
        int seed,
        int genVersion,
        int entityGridCellSize = WorldState.DefaultGridCellSize)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Zone id is required.", nameof(id));
        }

        Id = id;
        Seed = seed;
        GenVersion = genVersion;
        World = new WorldState(entityGridCellSize);
        _tileGrid = tileGrid ?? throw new ArgumentNullException(nameof(tileGrid));
        _spawnTiles = spawnTiles
            .Where(_tileGrid.IsWalkable)
            .Distinct()
            .ToArray();

        if (_spawnTiles.Length == 0)
        {
            throw new ArgumentException("At least one walkable spawn tile is required.", nameof(spawnTiles));
        }
    }

    public string Id { get; }
    public int Width => _tileGrid.Width;
    public int Height => _tileGrid.Height;
    public int Seed { get; }
    public int GenVersion { get; }
    public IReadOnlySet<TileCoord> BlockedTiles => _tileGrid.BlockedTiles;
    public IReadOnlyList<TileCoord> SpawnTiles => _spawnTiles;
    public WorldState World { get; }

    // PLAYER-COLLISION-TOGGLE: the live, server-authoritative flag gating whether OTHER PLAYERS are collision obstacles
    // for a moving player (player↔player collision). DEFAULT TRUE = the shipped behaviour (players collide). GameServer
    // owns the toggle plumbing (admin message + replication) and writes this; GatherEntityObstacles below READS it each
    // integrate. When FALSE, players are dropped from the obstacle gather → they pass through each other (byte-identical
    // to the player↔monster-only path); MONSTER collision (player↔monster + monster↔monster) is UNAFFECTED either way.
    // A plain bool read per gather (no per-tick cost). Single-threaded tick loop, so no synchronisation is needed.
    public bool PlayerCollisionEnabled { get; set; } = true;

    public static Zone CreateDefault(int width, int height, SpawnDistribution spawnDistribution = SpawnDistribution.Distributed)
    {
        return CreateGenerated(width, height, TileGrid.DefaultSeed, TerrainGenerator.CurrentGenVersion, spawnDistribution);
    }

    public static Zone CreateGenerated(
        int width,
        int height,
        int seed,
        int genVersion,
        SpawnDistribution spawnDistribution = SpawnDistribution.Distributed,
        int entityGridCellSize = WorldState.DefaultGridCellSize)
    {
        var tileGrid = TileGrid.CreateGenerated(width, height, seed, genVersion);
        return new Zone(
            DefaultId,
            tileGrid,
            CreateSpawnTiles(tileGrid, spawnDistribution),
            seed,
            genVersion,
            entityGridCellSize);
    }

    public bool IsWalkable(TileCoord tile)
    {
        return _tileGrid.IsWalkable(tile);
    }

    public TileCoord ResolveSpawnTile(TileCoord preferredTile)
    {
        if (IsWalkable(preferredTile))
        {
            return preferredTile;
        }

        return _spawnTiles[0];
    }

    public TileCoord ResolvePlayerSpawnTile(TileCoord persistedTile)
    {
        if (IsWalkable(persistedTile) && persistedTile != TileGrid.DefaultSpawnTile)
        {
            return persistedTile;
        }

        return NextSpawnTile();
    }

    public TileCoord NextSpawnTile()
    {
        var index = _nextSpawnTileIndex++ % _spawnTiles.Length;
        return _spawnTiles[index];
    }

    // CONTINUOUS MIGRATION (Phase 2): the PLAYER continuous-integrator wrapper — WITH swept-circle WALL COLLISION
    // (the behavioural flip; Phase 1 walked through walls). Per tick for
    // a moving player:
    //   1. delta  = entity.ComputeMoveDelta(unitDir, dt)            // velocity + facing set here; raw delta returned
    //   2. walls  = TileGrid.QueryNearbyWalls(pos, delta, radius)   // shared TileWalls, stable row-major, scratch
    //   3. end    = ContinuousCollision.Resolve(pos, delta, radius, walls)  // shared, deterministic, byte-identical
    //   4. crossed = entity.ApplyResolvedMove(end)                  // collided position + tile-crossing bookkeeping
    //   5. migrate the spatial bucket ONLY when the ROUNDED tile crossed (grid stays integer-keyed; Phase 6 floats it)
    // The wall set, radius and dt are the SAME the Phase-4 client predictor will use, so server and prediction land
    // byte-identically at a wall (the determinism contract). previousTile is captured before ApplyResolvedMove mutates
    // Position. Returns true iff the rounded tile crossed (the same signal the integrator returns). A zero delta
    // (stopped / rooted upstream) resolves in place — no tile crossing, no migration.
    public bool IntegrateMovement(WorldEntity entity, WorldVector unitDir, double dtSeconds, double radius)
    {
        var previousTile = entity.TileCoord;
        var start = entity.Position;

        var delta = entity.ComputeMoveDelta(unitDir, dtSeconds);
        _tileGrid.QueryNearbyWalls(start, delta, radius, _wallScratch);

        // PLAYER↔MONSTER + PLAYER↔PLAYER COLLISION: gather the nearby MONSTER and OTHER-PLAYER bodies as Circle obstacles
        // so the moving player STOPS at / SLIDES along a monster OR another player, resolved by the SAME shared swept-circle
        // resolver as walls (so the Phase-4 client predictor, feeding the SAME body set, predicts the same slide → no
        // rubber-band). SELF (the moving player) is excluded by the Id != self.Id guard (no-collide-with-yourself). With no
        // other body nearby the obstacle list is empty and the result is byte-identical to the walls-only path.
        GatherEntityObstacles(entity, start, radius, _obstacleScratch);
        var resolved = _obstacleScratch.Count == 0
            ? ContinuousCollision.Resolve(start, delta, radius, _wallScratch)
            : ContinuousCollision.Resolve(start, delta, radius, _wallScratch, _obstacleScratch);

        var crossedTile = entity.ApplyResolvedMove(resolved);

        // VELOCITY COHERENCE (player↔entity jitter fix): replicate the ACTUAL resolved velocity, not the pre-collision
        // desired dir×speed ComputeMoveDelta set. A player blocked/sliding at a wall — or now at another player/monster —
        // otherwise replicates a velocity pointing INTO the obstacle, and remote viewers (who extrapolate along the
        // replicated velocity) dead-reckon the player INTO the body, then snap back each snapshot = the jitter seen on
        // the OTHER client (authoritative positions never overlapped → no compenetration, just the extrapolation
        // overshoot). Same fix the glider got (P2): moving/sliding → the resolved velocity; fully blocked → StopMovement
        // (Velocity 0 + the stop-edge StateRevision bump so the zero RE-PUBLISHES — a bare SetVelocity(0) would be
        // delta'd out and leave the remote extrapolating the stale velocity). The LOCAL client predicts its own player
        // from the confirmed POSITION and never reads this replicated velocity, so there is NO reconcile interaction —
        // this only changes how OTHER clients render this player. Facing (set by ComputeMoveDelta) is kept.
        var resolvedDelta = resolved - start;
        if (resolvedDelta.LengthSquared > 0d)
        {
            entity.SetVelocity(resolvedDelta * (1d / dtSeconds));
        }
        else
        {
            entity.StopMovement();
        }

        if (crossedTile)
        {
            World.OnEntityMoved(entity, previousTile);
        }

        return crossedTile;
    }

    // PLAYER↔MONSTER + PLAYER↔PLAYER COLLISION: fill `destination` (cleared first) with the nearby MONSTER and
    // OTHER-PLAYER bodies — as Circles of the shared body radius — that the moving player `self` could collide with this
    // step. Queries the spatial grid for a small tile box around the start tile (a superset; the resolver applies the
    // exact circle-vs-circle test), keeps Monster|Player and ALWAYS excludes `self` (the no-collide-with-yourself guard —
    // `self` is the moving player; it must NEVER be its own obstacle or it pins in place), and projects each to a Circle
    // at its AUTHORITATIVE Position. Each client predicts the LOCAL player against each OTHER player's REPLICATED
    // position (the same authoritative-tracking position used here), so the resolves are mutual + symmetric. Deterministic
    // order (the grid's stable candidate order, then the Id sort). Reuses the Zone's candidate scratch internally; appends
    // Circles to the caller's reused `destination`. NO per-tick alloc.
    private void GatherEntityObstacles(WorldEntity self, WorldVector start, double radius, List<ContinuousCollision.Circle> destination)
    {
        destination.Clear();
        World.GatherInterestCandidates(start.ToTileRounded(), ObstacleQueryRadiusTiles, _obstacleCandidateScratch);
        // PARITY (player↔monster review MEDIUM): resolve obstacles in STABLE Id order so this server integrate and the
        // client predictor process a crowd in the SAME order — the circle de-penetration is Gauss-Seidel (order-
        // dependent), so without a shared order a player overlapping >=2 bodies resolves differently on each side →
        // a persistent reconcile (crowd rubber-band). The client gather sorts by the same Id (the shared NetworkId).
        _obstacleCandidateScratch.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        for (var i = 0; i < _obstacleCandidateScratch.Count; i++)
        {
            var candidate = _obstacleCandidateScratch[i];
            // Keep MONSTERS always; keep OTHER PLAYERS only when player↔player collision is enabled (the live server flag,
            // PLAYER-COLLISION-TOGGLE) — when it is OFF, players are dropped so a mover passes through them (monster
            // collision unaffected). ALWAYS drop self (no-collide-with-yourself — self is the moving player; it must NEVER
            // be its own obstacle or it pins in place). The CLIENT gather mirrors this gate on the SAME replicated flag.
            var isObstacleKind = candidate.Kind == EntityKind.Monster
                || (candidate.Kind == EntityKind.Player && PlayerCollisionEnabled);
            if (!isObstacleKind || candidate.Id == self.Id)
            {
                continue;
            }

            var pos = candidate.Position;
            destination.Add(new ContinuousCollision.Circle(pos.X, pos.Y, radius));
        }
    }

    // CONTINUOUS MIGRATION (Phase 8): the nearby-walls query seam for the MONSTER hop locomotion (HopLocomotion). A
    // thin forwarder to the owned TileGrid's QueryNearbyWalls (the SAME shared TileWalls the player integrator uses),
    // so a hop collides against the identical wall derivation players do — same body radius, same row-major order,
    // same resolver. The locomotion owns its OWN scratch buffer (passed here), distinct from the player integrator's
    // _wallScratch, since a monster hop and a player integrate never interleave within one Resolve call but DO run in
    // the same tick pass over different entities.
    public void QueryNearbyWalls(WorldVector start, WorldVector delta, double radius, List<ContinuousCollision.Wall> scratch)
    {
        _tileGrid.QueryNearbyWalls(start, delta, radius, scratch);
    }

    // CONTINUOUS MIGRATION (Phase 8): apply a monster HOP's resolved landing (WorldEntity.ApplyResolvedMove) and
    // migrate its spatial-grid bucket on a tile cross — the SAME bookkeeping IntegrateMovement does for a player, the
    // ONLY apply seam the hop locomotion uses. previousTile is captured before ApplyResolvedMove mutates Position.
    // Returns whether the rounded tile crossed (the migration signal). A sub-tile hop resolves in place — no migration.
    public bool ApplyMonsterLanding(WorldEntity entity, WorldVector landing)
    {
        var previousTile = entity.TileCoord;
        var crossedTile = entity.ApplyResolvedMove(landing);
        if (crossedTile)
        {
            World.OnEntityMoved(entity, previousTile);
        }

        return crossedTile;
    }

    public WorldEntity SpawnPlayer(
        uint networkId,
        Guid characterId,
        string displayName,
        TileCoord tile,
        ClientSession ownerSession,
        Inventory inventory)
    {
        return World.AddPlayer(networkId, characterId, displayName, ResolveSpawnTile(tile), ownerSession, inventory);
    }

    public WorldEntity SpawnTransient(
        uint networkId,
        EntityKind kind,
        string displayName,
        TileCoord tile,
        Direction8 facing)
    {
        if (!IsWalkable(tile))
        {
            throw new ArgumentException($"Transient spawn tile {tile} is not walkable.", nameof(tile));
        }

        return World.AddTransient(networkId, kind, displayName, tile, facing);
    }

    public WorldEntity SpawnResourceNode(
        uint networkId,
        ResourceNodeDefinition definition,
        TileCoord tile)
    {
        if (!IsWalkable(tile))
        {
            throw new ArgumentException($"Resource node tile {tile} is not walkable.", nameof(tile));
        }

        return World.AddResourceNode(networkId, definition.DisplayName, tile, new ResourceNode(definition));
    }

    // Seed constant XORed into the map seed to derive an independent resource-node placement seed, so the
    // node layout is deterministic from (and tied to) the map but does not alias the terrain PRNG stream.
    private const int ResourceNodeSeedSalt = 0x5C4A11ED;

    // Deterministically scatters harvestable resource nodes across the whole walkable map. Determinism is
    // the contract: identical (seed, size, density, registry) inputs MUST yield a byte-identical layout,
    // so a restart regenerates exactly the same world even though nodes aren't persisted. Like
    // TerrainGenerator this uses an explicit seeded SplitMix64 PRNG (no System.Random without a seed, no
    // clocks, no culture) and a fixed iteration order.
    //
    // Algorithm: rejection sampling. Derive a target count from map area and densityTilesPerNode
    // (~1 node per density² tiles). Repeatedly draw a uniformly-random tile; accept it only if it is
    // walkable, unused, and at least minSpacing tiles (Chebyshev) from every already-placed node, so
    // nodes spread across the map instead of clumping. Placed types round-robin the registry definitions
    // for a roughly even mix. A bounded attempt budget guards against an over-dense target on a small or
    // wall-heavy map (we place as many as fit rather than looping forever). Returns placements in
    // placement order so callers can rent network ids and spawn.
    public IReadOnlyList<(ResourceNodeDefinition Definition, TileCoord Tile)> PlanResourceNodeScatter(
        ResourceNodeRegistry registry,
        int densityTilesPerNode)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var placements = new List<(ResourceNodeDefinition, TileCoord)>();
        var definitions = registry.Definitions.ToArray();
        if (definitions.Length == 0 || densityTilesPerNode <= 0)
        {
            return placements;
        }

        // Inset by 1: the outermost ring is always blocked border, so never sample it.
        const int margin = 1;
        var minX = margin;
        var maxX = Width - 1 - margin;
        var minY = margin;
        var maxY = Height - 1 - margin;
        if (maxX < minX || maxY < minY)
        {
            return placements;
        }

        var spanX = maxX - minX + 1;
        var spanY = maxY - minY + 1;

        var targetCount = Math.Max(1, (Width * Height) / (densityTilesPerNode * densityTilesPerNode));

        // Min spacing scales with density (~70% of the cell pitch) so denser maps still spread evenly
        // without the spacing constraint starving the target count. Clamped to >= 1 (never two on a tile).
        var minSpacing = Math.Max(1, (densityTilesPerNode * 7) / 10);

        var used = new HashSet<TileCoord>();
        // Attempt budget: generous multiple of the target so a reachable target fills, but bounded so an
        // impossible target (too dense for the walkable area) terminates with a partial, deterministic set.
        var maxAttempts = targetCount * 64L + 1024L;

        var state = SeedState(Seed ^ ResourceNodeSeedSalt);
        var definitionIndex = 0;
        for (var attempt = 0L; attempt < maxAttempts && placements.Count < targetCount; attempt++)
        {
            state = NextState(state);
            var x = minX + (int)(Mix(state) % (ulong)spanX);
            state = NextState(state);
            var y = minY + (int)(Mix(state) % (ulong)spanY);
            var tile = new TileCoord(x, y);

            if (!IsWalkable(tile) || !HasClearApproachRoom(tile) || !used.Add(tile))
            {
                continue;
            }

            if (!IsFarEnough(placements, tile, minSpacing))
            {
                used.Remove(tile);
                continue;
            }

            placements.Add((definitions[definitionIndex], tile));
            definitionIndex = (definitionIndex + 1) % definitions.Length;
        }

        return placements;
    }

    // S48: a node must sit in open ground — all 8 Chebyshev neighbours walkable — so it never spawns
    // jammed against a wall/border ring where harvesters had no clear tile to stand on. The current map is
    // mostly open, so the strict all-8 rule fills the target comfortably (no relaxation to >= K needed).
    private bool HasClearApproachRoom(TileCoord tile)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                if (!IsWalkable(new TileCoord(tile.X + dx, tile.Y + dy)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsFarEnough(
        List<(ResourceNodeDefinition Definition, TileCoord Tile)> placements,
        TileCoord candidate,
        int minSpacing)
    {
        foreach (var (_, tile) in placements)
        {
            var dx = Math.Abs(tile.X - candidate.X);
            var dy = Math.Abs(tile.Y - candidate.Y);
            if (Math.Max(dx, dy) < minSpacing)
            {
                return false;
            }
        }

        return true;
    }

    // SplitMix64, mirroring TerrainGenerator: a tiny, fully-specified integer PRNG whose output is
    // identical on every platform/runtime (pure 64-bit unsigned arithmetic with defined overflow), so the
    // node layout cannot drift the way a framework RNG might.
    private static ulong SeedState(int seed)
    {
        return (ulong)(uint)seed * 0x9E3779B97F4A7C15UL;
    }

    private static ulong NextState(ulong state)
    {
        unchecked
        {
            return state + 0x9E3779B97F4A7C15UL;
        }
    }

    private static ulong Mix(ulong state)
    {
        unchecked
        {
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    public bool Despawn(ulong entityId, out WorldEntity entity)
    {
        return World.Remove(entityId, out entity);
    }

    // LIVING-ENEMIES P3: teleport an entity to `tile` (player death->respawn), migrating its spatial-index bucket the
    // same way TryStep does for a move. previousTile is read before WorldEntity.TeleportTo mutates Tile in place. The
    // tile must be walkable (the caller passes a resolved spawn tile, always walkable).
    public void Teleport(WorldEntity entity, TileCoord tile)
    {
        var previousTile = entity.TileCoord;
        entity.TeleportTo(tile);
        World.OnEntityMoved(entity, previousTile);
    }

    private static IReadOnlyList<TileCoord> CreateSpawnTiles(TileGrid tileGrid, SpawnDistribution spawnDistribution)
    {
        var center = new TileCoord(tileGrid.Width / 2, tileGrid.Height / 2);
        if (spawnDistribution == SpawnDistribution.Clustered)
        {
            return [center];
        }

        if (spawnDistribution == SpawnDistribution.Scattered)
        {
            return CreateScatteredSpawnTiles(tileGrid, center);
        }

        var spawnTiles = new List<TileCoord>();
        const int spreadTiles = 32;
        const int spacingTiles = 4;
        for (var y = center.Y - spreadTiles; y <= center.Y + spreadTiles; y += spacingTiles)
        {
            for (var x = center.X - spreadTiles; x <= center.X + spreadTiles; x += spacingTiles)
            {
                var tile = new TileCoord(x, y);
                if (tileGrid.IsWalkable(tile))
                {
                    spawnTiles.Add(tile);
                }
            }
        }

        if (spawnTiles.Count == 0)
        {
            spawnTiles.Add(center);
        }

        return spawnTiles;
    }

    // Distributes spawn tiles in an even grid spanning the whole walkable map, so clients spread
    // across the world (and AOI actually filters) instead of crowding the fixed central patch that
    // Distributed seeds. Spacing scales with map size so the spawn count stays roughly constant
    // (~16 points per axis) regardless of how large the world is. Blocked tiles are skipped.
    private static IReadOnlyList<TileCoord> CreateScatteredSpawnTiles(TileGrid tileGrid, TileCoord center)
    {
        // The outermost ring (x/y == 0 or width/height - 1) is always blocked border, so inset by 1.
        const int margin = 1;
        const int targetPointsPerAxis = 16;

        var minX = margin;
        var maxX = tileGrid.Width - 1 - margin;
        var minY = margin;
        var maxY = tileGrid.Height - 1 - margin;

        var spacingX = Math.Max(1, (maxX - minX) / targetPointsPerAxis);
        var spacingY = Math.Max(1, (maxY - minY) / targetPointsPerAxis);

        var spawnTiles = new List<TileCoord>();
        for (var y = minY; y <= maxY; y += spacingY)
        {
            for (var x = minX; x <= maxX; x += spacingX)
            {
                var tile = new TileCoord(x, y);
                if (tileGrid.IsWalkable(tile))
                {
                    spawnTiles.Add(tile);
                }
            }
        }

        if (spawnTiles.Count == 0)
        {
            spawnTiles.Add(center);
        }

        return spawnTiles;
    }
}
