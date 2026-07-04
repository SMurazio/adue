using System;
using System.Collections.Generic;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// DUO-SKILLSHOT (exp/duo-abilities): headless tests for the fusion-skillshot foundation + ability 1 — the PURE geometry
// (SkillshotMath: flight, fusion window at exact boundaries, bisector, segment tests), the ENGINE state machine
// (SkillshotEngine: flight → monster hit → despawn, range expiry, pierce cap, paired-only fusion) driven through
// injected fakes, and the pairing seam accessors on ClientSession.
public sealed class SkillshotTests
{
    private const int TickRate = 20;
    private const double Dt = 1d / TickRate;

    // ---- SkillshotMath: fusion window at exact boundaries ----

    [Theory]
    [InlineData(0.5, ProjectileTier.Perfect)]     // exactly the Perfect distance -> Perfect
    [InlineData(0.6, ProjectileTier.Good)]        // past Perfect, inside Good -> Good
    [InlineData(1.25, ProjectileTier.Good)]       // exactly the Good distance -> Good
    public void EvaluateFusion_ClassifiesByClosestApproach(double gap, ProjectileTier expected)
    {
        // Two anti-parallel shots on parallel lines offset by `gap` — their closest approach (at t=0, since they
        // diverge in x) is exactly `gap`, independent of the look-ahead window, so the tier is a clean function of gap.
        var evaluation = SkillshotMath.EvaluateFusion(
            new WorldVector(0, 0), new WorldVector(1, 0), SkillshotEngine.ProjectileSpeedUnitsPerSecond,
            new WorldVector(0, gap), new WorldVector(-1, 0), SkillshotEngine.ProjectileSpeedUnitsPerSecond,
            Dt,
            SkillshotEngine.PerfectFusionDistanceUnits, SkillshotEngine.PerfectFusionWindowTicks,
            SkillshotEngine.GoodFusionDistanceUnits, SkillshotEngine.GoodFusionWindowTicks);

        Assert.True(evaluation.Fused);
        Assert.Equal(expected, evaluation.Tier);
    }

    [Fact]
    public void EvaluateFusion_TooFarApart_DoesNotFuse()
    {
        // Gap beyond the Good distance -> no fusion.
        var evaluation = SkillshotMath.EvaluateFusion(
            new WorldVector(0, 0), new WorldVector(1, 0), SkillshotEngine.ProjectileSpeedUnitsPerSecond,
            new WorldVector(0, 1.3), new WorldVector(-1, 0), SkillshotEngine.ProjectileSpeedUnitsPerSecond,
            Dt,
            SkillshotEngine.PerfectFusionDistanceUnits, SkillshotEngine.PerfectFusionWindowTicks,
            SkillshotEngine.GoodFusionDistanceUnits, SkillshotEngine.GoodFusionWindowTicks);

        Assert.False(evaluation.Fused);
    }

    [Fact]
    public void EvaluateFusion_CrossingFarInTheFuture_DoesNotFuse()
    {
        // The two paths WILL cross (head-on, near y~0.2) but ~80 ticks out — far beyond the look-ahead window — so this
        // tick they are ~98 units apart and must not fuse. This pins the TEMPORAL half of the window.
        var evaluation = SkillshotMath.EvaluateFusion(
            new WorldVector(0, 0), new WorldVector(1, 0), SkillshotEngine.ProjectileSpeedUnitsPerSecond,
            new WorldVector(100, 0.4), new WorldVector(-1, 0), SkillshotEngine.ProjectileSpeedUnitsPerSecond,
            Dt,
            SkillshotEngine.PerfectFusionDistanceUnits, SkillshotEngine.PerfectFusionWindowTicks,
            SkillshotEngine.GoodFusionDistanceUnits, SkillshotEngine.GoodFusionWindowTicks);

        Assert.False(evaluation.Fused);
    }

    [Fact]
    public void Bisector_IsTheNormalizedSumOfHeadings()
    {
        var bisector = SkillshotMath.Bisector(new WorldVector(1, 0), new WorldVector(0, 1));
        Assert.Equal(0.70710678d, bisector.X, 5);
        Assert.Equal(0.70710678d, bisector.Y, 5);
    }

    [Fact]
    public void Bisector_OppositeHeadings_FallsBackToFirst()
    {
        var bisector = SkillshotMath.Bisector(new WorldVector(1, 0), new WorldVector(-1, 0));
        Assert.Equal(1d, bisector.X, 5);
        Assert.Equal(0d, bisector.Y, 5);
    }

    [Fact]
    public void Advance_StepsStraightBySpeedTimesDt()
    {
        var next = SkillshotMath.Advance(new WorldVector(1, 2), new WorldVector(1, 0), 12d, Dt);
        Assert.Equal(1d + (12d * Dt), next.X, 9);
        Assert.Equal(2d, next.Y, 9);
    }

    // ---- SkillshotEngine: flight / hit / despawn / range / pierce / pairing ----

    [Fact]
    public void SoloProjectile_HitsMonster_ThenDespawns()
    {
        var world = new WorldState();
        var monster = world.AddTransient(1, EntityKind.Monster, "M", new TileCoord(2, 0), Direction8.S);
        monster.SetMaxHealthFull(20);

        var harness = new EngineHarness(world, paired: false);
        harness.Engine.Fire(10, Guid.NewGuid(), new WorldVector(0, 0), new WorldVector(1, 0));

        // Step until it despawns (well within the flight time to reach x=2 at 12 u/s).
        for (var i = 0; i < 10 && harness.Engine.InFlightCount > 0; i++)
        {
            harness.Engine.Step((uint)(i + 1), Dt);
        }

        Assert.Equal(SkillshotEngine.SoloDamage, harness.TotalDamage);   // one solo hit = 8
        Assert.Equal(12, monster.Stats.Health);                          // 20 - 8
        Assert.Equal(0, harness.Engine.InFlightCount);                   // despawned on hit
    }

    [Fact]
    public void SoloProjectile_MissesEverything_DespawnsAtRange()
    {
        var world = new WorldState(); // no monsters
        var harness = new EngineHarness(world, paired: false);
        harness.Engine.Fire(10, Guid.NewGuid(), new WorldVector(0, 0), new WorldVector(1, 0));

        // Max range 14 at 12 u/s, dt 0.05 => 0.6/tick => ~24 ticks. Step generously.
        for (var i = 0; i < 40 && harness.Engine.InFlightCount > 0; i++)
        {
            harness.Engine.Step((uint)(i + 1), Dt);
        }

        Assert.Equal(0, harness.TotalDamage);
        Assert.Equal(0, harness.Engine.InFlightCount);
    }

    [Fact]
    public void UnpairedShots_DoNotFuse()
    {
        var world = new WorldState();
        var harness = new EngineHarness(world, paired: false);
        // Two parallel same-direction shots 0.4 apart — would fuse to Perfect IF paired.
        harness.Engine.Fire(10, Guid.NewGuid(), new WorldVector(0, -0.2), new WorldVector(1, 0));
        harness.Engine.Fire(20, Guid.NewGuid(), new WorldVector(0, 0.2), new WorldVector(1, 0));

        harness.Engine.Step(1, Dt);

        Assert.Equal(2, harness.Engine.InFlightCount);           // still two solos
        Assert.DoesNotContain(ProjectileTier.Perfect, harness.SpawnedTiers);
        Assert.DoesNotContain(ProjectileTier.Good, harness.SpawnedTiers);
    }

    [Fact]
    public void PairedCrossingShots_FuseToPerfect_AndPierceUpToThreeKills()
    {
        var world = new WorldState();
        var monsters = new List<WorldEntity>();
        for (var i = 1; i <= 4; i++)
        {
            var m = world.AddTransient((uint)i, EntityKind.Monster, "M", new TileCoord(i, 0), Direction8.S);
            m.SetMaxHealthFull(20); // Perfect damage 22 kills in one hit
            monsters.Add(m);
        }

        var harness = new EngineHarness(world, paired: true);
        // Two parallel same-direction shots 0.4 apart (< 0.5) -> Perfect fusion at (0,0) heading +X.
        harness.Engine.Fire(10, Guid.NewGuid(), new WorldVector(0, -0.2), new WorldVector(1, 0));
        harness.Engine.Fire(20, Guid.NewGuid(), new WorldVector(0, 0.2), new WorldVector(1, 0));

        for (var i = 0; i < 8 && harness.Engine.InFlightCount > 0; i++)
        {
            harness.Engine.Step((uint)(i + 1), Dt);
        }

        Assert.Contains(ProjectileTier.Perfect, harness.SpawnedTiers); // fused to Perfect
        Assert.Equal(0, monsters[0].Stats.Health);                    // killed
        Assert.Equal(0, monsters[1].Stats.Health);                    // killed
        Assert.Equal(0, monsters[2].Stats.Health);                    // killed
        Assert.Equal(20, monsters[3].Stats.Health);                   // 4th survives (pierce cap 3)
        Assert.Equal(0, harness.Engine.InFlightCount);                // despawned after the cap
    }

    // ---- pairing seam (ClientSession) ----

    [Fact]
    public void ClientSession_Pairing_IsSymmetricAndClearable()
    {
        var a = new ClientSession(null!);
        var b = new ClientSession(null!);
        Assert.False(a.HasPartner);
        Assert.Null(a.PartnerSession);

        a.SetPartner(b);
        b.SetPartner(a);
        Assert.True(a.HasPartner);
        Assert.True(b.HasPartner);
        Assert.Same(b, a.PartnerSession);
        Assert.Same(a, b.PartnerSession);

        a.SetPartner(null);
        b.SetPartner(null);
        Assert.False(a.HasPartner);
        Assert.False(b.HasPartner);
    }

    [Fact]
    public void ClientSession_FireCursor_IsMonotonicAndIndependent()
    {
        var session = new ClientSession(null!);
        Assert.True(session.TryConsumeFireSequence(1));
        Assert.True(session.TryConsumeFireSequence(2));
        Assert.False(session.TryConsumeFireSequence(2));  // duplicate rejected
        Assert.False(session.TryConsumeFireSequence(1));  // stale rejected
        Assert.True(session.TryConsumeFireSequence(3));
    }

    // A test harness wiring the engine to fakes: monsters come from a WorldState (gather returns the live set), damage
    // applies the real ApplyDamage (returning killed), and spawn/move/despawn are recorded.
    private sealed class EngineHarness
    {
        private readonly WorldState _world;
        private readonly bool _paired;
        private ulong _nextId = 1000;

        public SkillshotEngine Engine { get; }
        public int TotalDamage { get; private set; }
        public List<ProjectileTier> SpawnedTiers { get; } = new();
        public List<ulong> Despawned { get; } = new();

        public EngineHarness(WorldState world, bool paired)
        {
            _world = world;
            _paired = paired;
            Engine = new SkillshotEngine(
                Spawn,
                Move,
                Despawn,
                Gather,
                Damage,
                ArePaired);
        }

        private ulong Spawn(WorldVector position, WorldVector velocity, ProjectileTier tier)
        {
            SpawnedTiers.Add(tier);
            return _nextId++;
        }

        private void Move(ulong entityId, WorldVector newPosition)
        {
        }

        private void Despawn(ulong entityId)
        {
            Despawned.Add(entityId);
        }

        private void Gather(TileCoord center, int radiusTiles, List<WorldEntity> destination)
        {
            destination.Clear();
            foreach (var entity in _world.Entities)
            {
                destination.Add(entity);
            }
        }

        private bool Damage(WorldEntity monster, int amount, ulong shooterEntityId, Guid shooterCharacterId, uint serverTick)
        {
            if (monster.ApplyDamage(amount))
            {
                TotalDamage += amount;
            }

            return monster.Stats.Health <= 0;
        }

        private bool ArePaired(ulong shooterEntityIdA, ulong shooterEntityIdB) => _paired;
    }
}
