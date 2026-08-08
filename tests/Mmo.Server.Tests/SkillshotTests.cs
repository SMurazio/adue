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

    // DUO-GRILL-FUSION (Fable design-grill HIGH-1): this used to be "PairedCrossingShots_FuseToPerfect_
    // AndPierceUpToThreeKills" — the SAME geometry (two parallel same-direction shots 0.4u apart, fused on the very
    // first tick) but that WAS the mastery-inversion exploit: point-blank shooters, zero flight distance for either
    // shot, yet it classified Perfect. The earned-geometry gate (SkillshotEngine.MinFusionFlightDistanceUnits) now
    // caps a same-tick, zero-travel merge to Solo/base — acceptance: "adjacent shooters, immediate cross -> no
    // Perfect (base/no tier)".
    [Fact]
    public void AdjacentShooters_ImmediateCross_MergesAtSoloTier_NotPerfect()
    {
        var world = new WorldState();
        var monsters = new List<WorldEntity>();
        for (var i = 1; i <= 4; i++)
        {
            var m = world.AddTransient((uint)i, EntityKind.Monster, "M", new TileCoord(i, 0), Direction8.S);
            m.SetMaxHealthFull(20);
            monsters.Add(m);
        }

        var harness = new EngineHarness(world, paired: true);
        // Two parallel same-direction shots 0.4 apart (< 0.5 Perfect distance), fired from the same spot -> the
        // crossing-window test is satisfied on tick 1, before EITHER projectile has traveled anywhere.
        harness.Engine.Fire(10, Guid.NewGuid(), new WorldVector(0, -0.2), new WorldVector(1, 0));
        harness.Engine.Fire(20, Guid.NewGuid(), new WorldVector(0, 0.2), new WorldVector(1, 0));

        for (var i = 0; i < 8 && harness.Engine.InFlightCount > 0; i++)
        {
            harness.Engine.Step((uint)(i + 1), Dt);
        }

        Assert.DoesNotContain(ProjectileTier.Perfect, harness.SpawnedTiers);
        Assert.DoesNotContain(ProjectileTier.Good, harness.SpawnedTiers);
        // BOSS-2 (P1): the fusion REPORT still fires (any tier opens some shatter window) — the corner-case shatter
        // path stays reachable even though the merge itself is degraded. Acceptance: "P1 shatter still reachable in
        // the degraded/solo path."
        Assert.Contains(ProjectileTier.Solo, harness.FusionReports);
        Assert.Equal(SkillshotEngine.SoloDamage, harness.TotalDamage);           // base damage, no bonus
        Assert.Equal(20 - SkillshotEngine.SoloDamage, monsters[0].Stats.Health); // hit, not killed
        Assert.Equal(20, monsters[1].Stats.Health);                              // no pierce -> untouched
        Assert.Equal(20, monsters[2].Stats.Health);
        Assert.Equal(20, monsters[3].Stats.Health);
        Assert.Equal(0, harness.Engine.InFlightCount);                          // despawned after the single hit
    }

    // DUO-GRILL-FUSION acceptance: "separated shooters, mid-flight cross -> tiers unchanged from today." Fired 8u
    // apart on either side and closing head-on (0.4u perpendicular offset, same offset the point-blank test above
    // uses) — the crossing-window test cannot be satisfied until the pair is genuinely converging, by which point
    // each projectile has already flown well past MinFusionFlightDistanceUnits (2.0u), so the earned-geometry gate
    // must be a no-op: whatever EvaluateFusion classifies (unmodified by this fix) rides through untouched.
    [Fact]
    public void SeparatedShooters_ConvergeMidFlight_TierNotCappedToSolo()
    {
        var world = new WorldState(); // no monsters -- this test is about the TIER, not damage/pierce.
        var harness = new EngineHarness(world, paired: true);
        harness.Engine.Fire(10, Guid.NewGuid(), new WorldVector(-8, 0d), new WorldVector(1, 0));
        harness.Engine.Fire(20, Guid.NewGuid(), new WorldVector(8, 0.4d), new WorldVector(-1, 0));

        for (var i = 0; i < 30 && harness.Engine.InFlightCount > 0; i++)
        {
            harness.Engine.Step((uint)(i + 1), Dt);
        }

        Assert.True(
            harness.SpawnedTiers.Contains(ProjectileTier.Good) || harness.SpawnedTiers.Contains(ProjectileTier.Perfect),
            $"expected a Good or Perfect fusion (earned geometry should not suppress it); got [{string.Join(",", harness.SpawnedTiers)}]");
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

    // DUO-GRILL-FUSION (Fable design-grill HIGH-1) acceptance: "fire spam capped by cooldown (test the dedup +
    // cooldown interaction)." Mirrors the ORDER HandleFireSkillshot applies the two gates — dedup
    // (TryConsumeFireSequence) first, cooldown (TryConsumeFireCooldown) second — so a resent/duplicate sequence can
    // never re-arm or bypass the cooldown, and a fresh sequence mid-cooldown is dedup-legal but still fire-rejected.
    [Fact]
    public void ClientSession_FireCooldown_GatesRepeatsIndependentlyOfDedup()
    {
        var session = new ClientSession(null!);
        const uint cooldownTicks = 12; // ~0.6s at the default 20 Hz tick rate.

        // First press: fresh sequence, cooldown cold -> both gates pass.
        Assert.True(session.TryConsumeFireSequence(1));
        Assert.True(session.TryConsumeFireCooldown(0, cooldownTicks));

        // Mash Q again immediately (same tick): dedup passes (fresh sequence), cooldown is still hot -> rejected.
        Assert.True(session.TryConsumeFireSequence(2));
        Assert.False(session.TryConsumeFireCooldown(0, cooldownTicks));

        // A RESEND of that same sequence is now rejected purely by dedup, before the cooldown is even consulted —
        // it can never re-arm or "refresh" the cooldown window.
        Assert.False(session.TryConsumeFireSequence(2));

        // Still mashing mid-cooldown with fresh sequences: dedup keeps passing, cooldown keeps rejecting.
        Assert.True(session.TryConsumeFireSequence(3));
        Assert.False(session.TryConsumeFireCooldown(cooldownTicks - 1, cooldownTicks));

        // Once the cooldown window fully elapses, a fresh sequence fires again.
        Assert.True(session.TryConsumeFireSequence(4));
        Assert.True(session.TryConsumeFireCooldown(cooldownTicks, cooldownTicks));
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

        // DUO-GRILL-FUSION: every tier reported through FusionReportDelegate (BOSS-2's plating-shatter seam) — lets a
        // test assert the report seam still fires (with whatever tier, including a gate-degraded Solo) without
        // wiring up BossEncounterEngine.
        public List<ProjectileTier> FusionReports { get; } = new();

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
                ArePaired,
                onFusion: (tier, _) => FusionReports.Add(tier));
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
