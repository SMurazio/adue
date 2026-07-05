using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// DUO-SKILLSHOT (exp/duo-abilities): the server-side fusion-skillshot engine — the lightweight, headlessly-testable
// stepper modelled on TelegraphScheduler (injected seams, single-threaded tick loop, reused scratch, no allocation on
// the hot path). It OWNS the in-flight projectiles' authoritative flight state; the WorldEntity vehicle exists only so
// the projectile AOI-replicates + interpolates for free (spawned/moved/despawned through the injected seams that talk
// to Zone). Each Step():
//   1. FUSION pass — for every pair of SOLO projectiles whose shooters are mutually PAIRED, look-ahead the two paths
//      this tick; if they cross within the Perfect (tight) or Good (loose) spatial+temporal window, despawn both and
//      spawn ONE fused projectile at the crossing midpoint along the bisector, x1.5 speed, bonus damage (+ pierce for
//      Perfect).
//   2. FLIGHT pass — advance each projectile straight along its heading by speed·dt (capped by remaining range),
//      resolve monster hits along the per-tick segment through the SAME monster-damage seam the melee uses, and
//      despawn on hit (or on pierce exhaustion) / on range expiry.
// The geometry (fusion window, bisector, segment tests) lives in the pure SkillshotMath so it is unit-tested directly;
// this class owns the state machine + the seam plumbing (also fake-injectable for the flight/hit/pierce/fusion tests).
public sealed class SkillshotEngine
{
    // ---- tunables (the one obvious place; experiment values from the orchestrator spec) ----
    public const double ProjectileSpeedUnitsPerSecond = 12d;
    public const double ProjectileMaxRangeUnits = 14d;
    public const double FusedSpeedMultiplier = 1.5d;

    // The projectile↔monster overlap radius (segment-vs-body): the monster's body circle, reusing the SAME canonical
    // body radius the free-aim melee resolves against so a projectile "clips" a monster it grazes, not only a dead-
    // centre pass.
    public const double ProjectileHitRadiusUnits = FreeAimSector.EntityHitRadiusTiles;

    public const int SoloDamage = 8;
    public const int GoodDamage = 14;
    public const int PerfectDamage = 22;

    // Perfect fusion pierces: it continues through monsters it KILLS, up to this many hits total, then despawns.
    public const int PerfectPierceMaxHits = 3;

    // Fusion windows: Perfect = tight (crossing within 0.5u AND a 2-tick look-ahead); Good = loose (1.25u / 4 ticks).
    public const double PerfectFusionDistanceUnits = 0.5d;
    public const int PerfectFusionWindowTicks = 2;
    public const double GoodFusionDistanceUnits = 1.25d;
    public const int GoodFusionWindowTicks = 4;

    // ---- injected seams (Zone/world/combat plumbing; fakes in tests) ----

    // Spawn the replicated projectile WorldEntity at `position` with world `velocity` (units/sec; its direction gives
    // the facing, its magnitude rides the wire so remote clients EXTRAPOLATE smoothly between the sparse tile-cross
    // snapshot updates — a projectile flies at constant velocity, so the dead-reckoned path is exact) and the tier's
    // visual, returning its stable entity id (the id the move/despawn seams key on). The impl rents a network id,
    // spawns a transient EntityKind.Projectile through Zone, sets its velocity, zeroes its vitals, and records its
    // tier for the tint/scale replication.
    public delegate ulong SpawnProjectileDelegate(WorldVector position, WorldVector velocity, ProjectileTier tier);

    // Move the projectile entity to `newPosition` and migrate its spatial-grid bucket (Zone.MoveProjectile).
    public delegate void MoveProjectileDelegate(ulong entityId, WorldVector newPosition);

    // Despawn the projectile entity (remove from world + free its network id + drop its tier record).
    public delegate void DespawnProjectileDelegate(ulong entityId);

    // Gather candidate entities within `radiusTiles` of `center` (the SAME spatial index AOI/combat use) into
    // `destination` (cleared first) — the engine filters to attackable monsters and applies the exact segment test.
    public delegate void GatherCandidatesDelegate(TileCoord center, int radiusTiles, List<WorldEntity> destination);

    // Apply `amount` projectile damage to `monster` through the shared melee seam (ApplyDamage + cosmetic damage
    // event + contribution ledger + death/KillMonster). Returns true iff the monster DIED from this hit (drives the
    // Perfect pierce "continues through monsters it kills" rule).
    public delegate bool DamageMonsterDelegate(WorldEntity monster, int amount, ulong shooterEntityId, Guid shooterCharacterId, uint serverTick);

    // Whether the two shooter entities are mutually PAIRED (the fusion gate — only paired partners' shots fuse).
    public delegate bool ArePairedDelegate(ulong shooterEntityIdA, ulong shooterEntityIdB);

    // BOSS-2 (P1): report a FUSION merge with its tier — the SAME injected-seam style as the delegates above. The
    // BossEncounterEngine subscribes so a fused skillshot SHATTERS the Sunderer's plating (any tier; the fusion need
    // NOT hit the boss — the merge itself is the gate). Optional (null = no subscriber); default no-op.
    public delegate void FusionReportDelegate(ProjectileTier tier, uint serverTick);

    // BOSS-2 (P1): report a skillshot HIT on a monster (any monster — this engine doesn't know which is the boss; the
    // encounter filters). Fires once per monster actually struck along a projectile's per-tick segment. The
    // BossEncounterEngine counts boss hits for the SOLO 3-in-6 s shatter fallback (no fusion possible solo). Reporting
    // hits here — rather than at the shared monster-damage seam — keeps tether/detonation damage (which share that
    // seam) from being miscounted as skillshot hits. Optional (null = no subscriber); default no-op.
    public delegate void MonsterHitReportDelegate(ulong monsterId, uint serverTick);

    private sealed class Projectile
    {
        public ulong EntityId;
        public ulong ShooterEntityId;
        public Guid ShooterCharacterId;
        public WorldVector Position;
        public WorldVector Direction; // unit
        public double Speed;
        public double RangeRemaining;
        public ProjectileTier Tier;
        public int HitsRemaining;                 // monster-hit budget (1 for Solo/Good; PerfectPierceMaxHits for Perfect)
        public readonly HashSet<ulong> HitMonsters = []; // per-projectile hit dedup (a shot never double-hits one body)
    }

    private readonly SpawnProjectileDelegate _spawn;
    private readonly MoveProjectileDelegate _move;
    private readonly DespawnProjectileDelegate _despawn;
    private readonly GatherCandidatesDelegate _gather;
    private readonly DamageMonsterDelegate _damage;
    private readonly ArePairedDelegate _arePaired;
    private readonly FusionReportDelegate _onFusion;
    private readonly MonsterHitReportDelegate _onMonsterHit;

    private readonly List<Projectile> _projectiles = [];
    private readonly List<WorldEntity> _candidateScratch = [];

    // BOSS-2 (P1): the fusion/monster-hit report seams default to no-ops so every existing construction (the flight/
    // fusion tests) is byte-identical; GameServer injects the real BossEncounterEngine hooks.
    public SkillshotEngine(
        SpawnProjectileDelegate spawn,
        MoveProjectileDelegate move,
        DespawnProjectileDelegate despawn,
        GatherCandidatesDelegate gather,
        DamageMonsterDelegate damage,
        ArePairedDelegate arePaired,
        FusionReportDelegate? onFusion = null,
        MonsterHitReportDelegate? onMonsterHit = null)
    {
        _spawn = spawn ?? throw new ArgumentNullException(nameof(spawn));
        _move = move ?? throw new ArgumentNullException(nameof(move));
        _despawn = despawn ?? throw new ArgumentNullException(nameof(despawn));
        _gather = gather ?? throw new ArgumentNullException(nameof(gather));
        _damage = damage ?? throw new ArgumentNullException(nameof(damage));
        _arePaired = arePaired ?? throw new ArgumentNullException(nameof(arePaired));
        _onFusion = onFusion ?? ((ProjectileTier _, uint _) => { });
        _onMonsterHit = onMonsterHit ?? ((ulong _, uint _) => { });
    }

    // The in-flight projectile count — the tick-loop gate (skip Step when 0) and the tests' observation window.
    public int InFlightCount => _projectiles.Count;

    // Fire a solo skillshot from `origin` along `aimUnitDir` (already unit-length; a zero vector is ignored). Spawns
    // the replicated projectile and enrolls it in the flight/fusion passes. Called from GameServer.HandleFireSkillshot.
    public void Fire(ulong shooterEntityId, Guid shooterCharacterId, WorldVector origin, WorldVector aimUnitDir)
    {
        if (aimUnitDir.LengthSquared <= 0d)
        {
            return;
        }

        var dir = aimUnitDir.Normalized();
        var entityId = _spawn(origin, dir * ProjectileSpeedUnitsPerSecond, ProjectileTier.Solo);
        _projectiles.Add(new Projectile
        {
            EntityId = entityId,
            ShooterEntityId = shooterEntityId,
            ShooterCharacterId = shooterCharacterId,
            Position = origin,
            Direction = dir,
            Speed = ProjectileSpeedUnitsPerSecond,
            RangeRemaining = ProjectileMaxRangeUnits,
            Tier = ProjectileTier.Solo,
            HitsRemaining = 1,
        });
    }

    // One tick: resolve fusions (against this-tick look-ahead), then advance + hit-test + expire every projectile.
    // ~free when nothing is in flight (the common case).
    public void Step(uint serverTick, double dtSeconds)
    {
        if (_projectiles.Count == 0)
        {
            return;
        }

        ResolveFusions(serverTick, dtSeconds);
        AdvanceAndHit(serverTick, dtSeconds);
    }

    // FUSION pass: find pairs of SOLO projectiles whose shooters are mutually paired and whose paths cross in-window,
    // and merge each into one fused projectile. Only Solo projectiles fuse (a fused shot never re-fuses — no partner
    // pairing for it, and it prevents cascades). Restart the scan after each merge (it mutates the list); the in-flight
    // set is tiny, so the O(n²) rescan is negligible.
    private void ResolveFusions(uint serverTick, double dtSeconds)
    {
        var fusedSomething = true;
        while (fusedSomething)
        {
            fusedSomething = false;
            for (var i = 0; i < _projectiles.Count && !fusedSomething; i++)
            {
                var a = _projectiles[i];
                if (a.Tier != ProjectileTier.Solo)
                {
                    continue;
                }

                for (var j = i + 1; j < _projectiles.Count; j++)
                {
                    var b = _projectiles[j];
                    if (b.Tier != ProjectileTier.Solo || !_arePaired(a.ShooterEntityId, b.ShooterEntityId))
                    {
                        continue;
                    }

                    var evaluation = SkillshotMath.EvaluateFusion(
                        a.Position, a.Direction, a.Speed,
                        b.Position, b.Direction, b.Speed,
                        dtSeconds,
                        PerfectFusionDistanceUnits, PerfectFusionWindowTicks,
                        GoodFusionDistanceUnits, GoodFusionWindowTicks);
                    if (!evaluation.Fused)
                    {
                        continue;
                    }

                    Fuse(a, b, evaluation);
                    // BOSS-2 (P1): report the fusion + tier so the boss encounter can shatter the plating. The merge
                    // itself is the gate (receiver-forgives) — reported here regardless of whether it later hits.
                    _onFusion(evaluation.Tier, serverTick);
                    fusedSomething = true;
                    break;
                }
            }
        }
    }

    // Merge two solo projectiles into one fused projectile: despawn both, spawn a new entity at the crossing midpoint
    // along the bisector, at x1.5 speed, with the tier's damage + pierce budget and the MAX of the two remaining ranges.
    // Attribution (shooter id/character for the damage ledger) goes to the lower entity id for a deterministic choice.
    private void Fuse(Projectile a, Projectile b, SkillshotMath.FusionEvaluation evaluation)
    {
        _despawn(a.EntityId);
        _despawn(b.EntityId);
        _projectiles.Remove(a);
        _projectiles.Remove(b);

        var dir = SkillshotMath.Bisector(a.Direction, b.Direction);
        var (attributionShooter, attributionCharacter) = a.EntityId <= b.EntityId
            ? (a.ShooterEntityId, a.ShooterCharacterId)
            : (b.ShooterEntityId, b.ShooterCharacterId);
        var hitsRemaining = evaluation.Tier == ProjectileTier.Perfect ? PerfectPierceMaxHits : 1;
        var speed = ProjectileSpeedUnitsPerSecond * FusedSpeedMultiplier;

        var entityId = _spawn(evaluation.CrossingPoint, dir * speed, evaluation.Tier);
        _projectiles.Add(new Projectile
        {
            EntityId = entityId,
            ShooterEntityId = attributionShooter,
            ShooterCharacterId = attributionCharacter,
            Position = evaluation.CrossingPoint,
            Direction = dir,
            Speed = speed,
            RangeRemaining = Math.Max(a.RangeRemaining, b.RangeRemaining),
            Tier = evaluation.Tier,
            HitsRemaining = hitsRemaining,
        });
    }

    // FLIGHT pass: advance each projectile along its heading by speed·dt (capped by remaining range), resolve monster
    // hits along the per-tick segment, and despawn on hit/pierce-exhaustion or range expiry. Iterates a snapshot of the
    // list (ToArray via the index loop over a copy is unnecessary — we remove by reference at the end) so mid-pass
    // despawns are safe.
    private void AdvanceAndHit(uint serverTick, double dtSeconds)
    {
        // Iterate over a stable copy of the current set: Fire/Fuse never run during this pass, and we remove expired
        // projectiles from _projectiles as we go.
        for (var index = _projectiles.Count - 1; index >= 0; index--)
        {
            var projectile = _projectiles[index];
            var from = projectile.Position;
            var travel = projectile.Speed * dtSeconds;
            var step = Math.Min(travel, projectile.RangeRemaining);
            var to = from + (projectile.Direction * step);

            var expired = ResolveHits(projectile, from, to, serverTick);

            projectile.RangeRemaining -= step;
            projectile.Position = to;
            _move(projectile.EntityId, to);

            if (!expired && projectile.RangeRemaining <= 0d)
            {
                expired = true;
            }

            if (expired)
            {
                _despawn(projectile.EntityId);
                _projectiles.Remove(projectile);
            }
        }
    }

    // Resolve monster hits for one projectile's per-tick segment [from,to]. Gathers nearby monsters, tests the exact
    // segment-vs-body overlap, and applies damage nearest-first (so a piercing shot kills in travel order). Returns
    // true iff the projectile is spent (a non-piercing shot that hit anything, a Perfect that hit a monster it did NOT
    // kill or exhausted its pierce budget). A projectile that hit nothing this tick returns false (keeps flying).
    private bool ResolveHits(Projectile projectile, WorldVector from, WorldVector to, uint serverTick)
    {
        // Gather over a tile box that supersets the segment + body reach (mirrors the melee/telegraph gather margin):
        // centre on the segment midpoint, radius = half the segment length + the body radius, +1 tile of slack.
        var midTile = ((from + to) * 0.5d).ToTileRounded();
        var half = (to - from).Length * 0.5d;
        var gatherRadius = Math.Max(1, (int)Math.Ceiling(half + ProjectileHitRadiusUnits) + 1);
        _gather(midTile, gatherRadius, _candidateScratch);

        // Collect the monsters this segment overlaps (deduped against prior hits), tagged with the along-segment param
        // so a piercing shot resolves them nearest-first.
        _monsterHitScratch.Clear();
        foreach (var candidate in _candidateScratch)
        {
            // LIVE FEEL FIX (see TetherEngine.ResolveSweetDamage): IsAttackableEnemy is the single target-set
            // truth — the extra Kind==Monster clause excluded training dummies from every duo ability at once.
            if (!CombatTargeting.IsAttackableEnemy(candidate))
            {
                continue;
            }

            if (projectile.HitMonsters.Contains(candidate.Id) || candidate.Stats.Health <= 0)
            {
                continue;
            }

            var (distance, t) = SkillshotMath.PointSegmentDistance(candidate.Position, from, to);
            if (distance <= ProjectileHitRadiusUnits)
            {
                _monsterHitScratch.Add((candidate, t));
            }
        }

        if (_monsterHitScratch.Count == 0)
        {
            return false;
        }

        _monsterHitScratch.Sort(static (x, y) => x.T.CompareTo(y.T));

        var spent = false;
        foreach (var (monster, _) in _monsterHitScratch)
        {
            projectile.HitMonsters.Add(monster.Id);
            // BOSS-2 (P1): report the skillshot hit (the encounter counts boss hits for the solo shatter fallback).
            _onMonsterHit(monster.Id, serverTick);
            var killed = _damage(monster, DamageFor(projectile.Tier), projectile.ShooterEntityId, projectile.ShooterCharacterId, serverTick);
            projectile.HitsRemaining--;

            // Pierce rule: a Perfect shot continues ONLY through monsters it KILLS and while budget remains; any other
            // shot (Solo/Good), a Perfect that failed to kill, or an exhausted budget spends the projectile.
            var pierces = projectile.Tier == ProjectileTier.Perfect && killed && projectile.HitsRemaining > 0;
            if (!pierces)
            {
                spent = true;
                break;
            }
        }

        return spent;
    }

    private readonly List<(WorldEntity Monster, double T)> _monsterHitScratch = [];

    private static int DamageFor(ProjectileTier tier) => tier switch
    {
        ProjectileTier.Perfect => PerfectDamage,
        ProjectileTier.Good => GoodDamage,
        _ => SoloDamage,
    };
}
