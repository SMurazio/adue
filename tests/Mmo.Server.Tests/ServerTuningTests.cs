using Mmo.Server.Configuration;
using Mmo.Server.Runtime;
using Xunit;

namespace Mmo.Server.Tests;

// S60 unit coverage for the live-tuning holder + registry: the holder seeds from ServerOptions, and the
// registry clamps known keys to the startup bounds and rejects unknown/invalid keys. The end-to-end admin
// gating + live effect is covered by AdminTuningIntegrationTests.
public sealed class ServerTuningTests
{
    private static ServerOptions Options(int stepCooldownMs = 140, float interestRadius = 35f) =>
        new(
            7777,
            20,
            "tuning-test",
            DatabaseProvider.Sqlite,
            "Data Source=:memory:",
            "db/sqlite",
            64,
            64,
            stepCooldownMs,
            15,
            interestRadius,
            150,
            SpawnDistribution.Distributed,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void SeedsFromOptions()
    {
        var tuning = new ServerTuning(Options(stepCooldownMs: 200, interestRadius: 40f));

        Assert.Equal(200, tuning.StepCooldownMs);
        Assert.Equal(40f, tuning.InterestRadius);
    }

    [Fact]
    public void StepCooldownTicksMatchesOptionsDerivation()
    {
        var options = Options(stepCooldownMs: 140);
        var tuning = new ServerTuning(options);

        Assert.Equal(options.StepCooldownTicks, tuning.StepCooldownTicks);
    }

    [Fact]
    public void StepCooldownIsPinnedAndNotLiveTunable()
    {
        // SPEED1: the move.stepCooldownMs live knob was removed — the base cooldown is a pinned constant.
        // The registry must reject the old key and leave the seeded base untouched.
        var tuning = new ServerTuning(Options(stepCooldownMs: 150));

        Assert.False(ServerTuningRegistry.TryApply(tuning, "move.stepCooldownMs", 250d, out _));
        Assert.False(ServerTuningRegistry.IsKnownKey("move.stepCooldownMs"));
        Assert.Equal(150, tuning.StepCooldownMs);
    }

    [Fact]
    public void AppliesInterestRadiusAndClampsToBounds()
    {
        var tuning = new ServerTuning(Options());

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.InterestRadiusKey, 60d, out var applied));
        Assert.Equal(60f, tuning.InterestRadius);
        Assert.Equal(60d, applied);

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.InterestRadiusKey, 0d, out _));
        Assert.True(tuning.InterestRadius >= 1f);

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.InterestRadiusKey, 100000d, out _));
        Assert.True(tuning.InterestRadius <= 512f);
    }

    [Fact]
    public void UnknownKeyIsRejectedAndChangesNothing()
    {
        var tuning = new ServerTuning(Options(stepCooldownMs: 140, interestRadius: 35f));

        Assert.False(ServerTuningRegistry.TryApply(tuning, "does.not.exist", 999d, out _));
        Assert.Equal(140, tuning.StepCooldownMs);
        Assert.Equal(35f, tuning.InterestRadius);
    }

    [Fact]
    public void NonFiniteValueIsRejected()
    {
        var tuning = new ServerTuning(Options());

        Assert.False(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.InterestRadiusKey, double.NaN, out _));
        Assert.False(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.InterestRadiusKey, double.PositiveInfinity, out _));
    }

    // COMBAT-TUNING: the combat.* keys are known, seed to the historical defaults, apply, and clamp to bounds.
    [Fact]
    public void CombatKeysSeedToHistoricalDefaults()
    {
        var tuning = new ServerTuning(Options());

        Assert.Equal(600, tuning.AttackCooldownMs);
        Assert.Equal(0, tuning.AttackRootMs);
        Assert.Equal(45d, tuning.FreeAimHalfAngleDegrees);
        Assert.Equal(1.6d, tuning.FreeAimRadiusTiles);
        Assert.Equal(20, tuning.AttackDamage);

        var snapshot = tuning.CombatSnapshot;
        Assert.Equal(600, snapshot.AttackCooldownMs);
        Assert.Equal(0, snapshot.RootMs);
        Assert.Equal(45d, snapshot.HalfAngleDegrees);
        Assert.Equal(1.6d, snapshot.RadiusUnits);
        Assert.Equal(20, snapshot.Damage);
    }

    [Fact]
    public void CombatKeysAreKnownAndFlaggedAsCombat()
    {
        foreach (var key in new[]
                 {
                     ServerTuningRegistry.AttackCooldownMsKey,
                     ServerTuningRegistry.AttackRootMsKey,
                     ServerTuningRegistry.FreeAimHalfAngleDegKey,
                     ServerTuningRegistry.FreeAimRadiusTilesKey,
                     ServerTuningRegistry.AttackDamageKey,
                 })
        {
            Assert.True(ServerTuningRegistry.IsKnownKey(key));
            Assert.True(ServerTuningRegistry.IsCombatKey(key));
        }

        // A non-combat key is known but not flagged as combat (so it doesn't trigger a combat re-broadcast).
        Assert.True(ServerTuningRegistry.IsKnownKey(ServerTuningRegistry.InterestRadiusKey));
        Assert.False(ServerTuningRegistry.IsCombatKey(ServerTuningRegistry.InterestRadiusKey));

        // LIVING-ENEMIES P3: the global player respawn delay is a known, non-combat key.
        Assert.True(ServerTuningRegistry.IsKnownKey(ServerTuningRegistry.PlayerRespawnMsKey));
        Assert.False(ServerTuningRegistry.IsCombatKey(ServerTuningRegistry.PlayerRespawnMsKey));

        // LOOT P4b: the corpse decay duration is a known, non-combat key.
        Assert.True(ServerTuningRegistry.IsKnownKey(ServerTuningRegistry.CorpseDecayMsKey));
        Assert.False(ServerTuningRegistry.IsCombatKey(ServerTuningRegistry.CorpseDecayMsKey));
    }

    // LOOT P4b: the corpse decay duration applies, clamps to [1000, 1800000] ms, and derives a tick count.
    [Fact]
    public void CorpseDecayDurationAppliesAndClamps()
    {
        var tuning = new ServerTuning(Options());
        Assert.Equal(180000, tuning.CorpseDecayMs); // default ~3 min.
        Assert.Equal(3600u, tuning.CorpseDecayTicks); // 180000 ms / 50 ms = 3600 ticks at 20 Hz.

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.CorpseDecayMsKey, 60000d, out var applied));
        Assert.Equal(60000d, applied);
        Assert.Equal(60000, tuning.CorpseDecayMs);
        Assert.Equal(1200u, tuning.CorpseDecayTicks);

        // Clamps below-min (instant-vanish) up to the 1 s floor, and a wild value down to the 30 min ceiling.
        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.CorpseDecayMsKey, 0d, out var floored));
        Assert.Equal(1000d, floored);
        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.CorpseDecayMsKey, 9_999_999d, out var ceiled));
        Assert.Equal(1800000d, ceiled);
    }

    // LIVING-ENEMIES P3: the player respawn delay applies, clamps to [0, 60000] ms, and derives a tick count.
    [Fact]
    public void PlayerRespawnDelayAppliesAndClamps()
    {
        var tuning = new ServerTuning(Options());
        Assert.Equal(2000, tuning.PlayerRespawnMs); // default ~2 s.

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.PlayerRespawnMsKey, 3000d, out var applied));
        Assert.Equal(3000d, applied);
        Assert.Equal(3000, tuning.PlayerRespawnMs);
        Assert.Equal(60u, tuning.PlayerRespawnTicks); // 3000 ms / 50 ms = 60 ticks at 20 Hz.

        // Clamps a wild value to the 60 s max.
        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.PlayerRespawnMsKey, 999999d, out var clamped));
        Assert.Equal(60000d, clamped);
    }

    [Fact]
    public void CombatKeysApplyWithinBounds()
    {
        var tuning = new ServerTuning(Options());

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.AttackCooldownMsKey, 900d, out var cd));
        Assert.Equal(900d, cd);
        Assert.Equal(900, tuning.AttackCooldownMs);

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.AttackRootMsKey, 120d, out _));
        Assert.Equal(120, tuning.AttackRootMs);

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.FreeAimHalfAngleDegKey, 60d, out _));
        Assert.Equal(60d, tuning.FreeAimHalfAngleDegrees);

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.FreeAimRadiusTilesKey, 3.0d, out _));
        Assert.Equal(3.0d, tuning.FreeAimRadiusTiles);

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.AttackDamageKey, 50d, out _));
        Assert.Equal(50, tuning.AttackDamage);
    }

    [Fact]
    public void CombatKeysClampOutOfRangeValues()
    {
        var tuning = new ServerTuning(Options());

        // Below-min and above-max each clamp into the registry bounds.
        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.AttackCooldownMsKey, 0d, out _));
        Assert.True(tuning.AttackCooldownMs >= 50);
        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.AttackCooldownMsKey, 1_000_000d, out _));
        Assert.True(tuning.AttackCooldownMs <= 5000);

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.AttackRootMsKey, -100d, out _));
        Assert.True(tuning.AttackRootMs >= 0);
        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.AttackRootMsKey, 99_999d, out _));
        Assert.True(tuning.AttackRootMs <= 2000);

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.FreeAimHalfAngleDegKey, 0d, out _));
        Assert.True(tuning.FreeAimHalfAngleDegrees >= 1d);
        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.FreeAimHalfAngleDegKey, 999d, out _));
        Assert.True(tuning.FreeAimHalfAngleDegrees <= 180d);

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.FreeAimRadiusTilesKey, 0d, out _));
        Assert.True(tuning.FreeAimRadiusTiles >= 0.25d);
        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.FreeAimRadiusTilesKey, 999d, out _));
        Assert.True(tuning.FreeAimRadiusTiles <= 16d);

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.AttackDamageKey, -5d, out _));
        Assert.True(tuning.AttackDamage >= 0);
        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.AttackDamageKey, 1_000_000d, out _));
        Assert.True(tuning.AttackDamage <= 10000);
    }

    [Fact]
    public void CombatCooldownTicksTracksLiveValue()
    {
        // The derived AttackCooldownTicks reflects a live change (Ceiling at 20 Hz): 600 ms -> 12 ticks; after a
        // bump to 1000 ms -> 20 ticks. Mirrors the old GameServer.AttackCooldownTicks derivation.
        var tuning = new ServerTuning(Options());
        Assert.Equal(12u, tuning.AttackCooldownTicks);

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.AttackCooldownMsKey, 1000d, out _));
        Assert.Equal(20u, tuning.AttackCooldownTicks);
    }

    // LIVING-ENEMIES P2-POLISH: the former global monster.* tuning keys were REPLACED by per-TYPE keys owned by
    // MonsterTypeRegistry (see MonsterTypeRegistryTests). ServerTuning no longer holds any monster knob.

    // CONTINUOUS MIGRATION (Phase 2): the body-radius knob seeds to the shared default (0.5), is a known non-combat
    // key, applies, and clamps STRICTLY below 0.5 (so a 1-tile-wide gap stays passable) and above 0.
    [Fact]
    public void BodyRadiusSeedsToSharedDefaultAndClampsStrictlyBelowHalf()
    {
        var tuning = new ServerTuning(Options());
        Assert.Equal(Mmo.Shared.Domain.CollisionDefaults.BodyRadius, tuning.BodyRadiusUnits);
        Assert.Equal(0.5d, tuning.BodyRadiusUnits);

        Assert.True(ServerTuningRegistry.IsKnownKey(ServerTuningRegistry.BodyRadiusUnitsKey));
        Assert.False(ServerTuningRegistry.IsCombatKey(ServerTuningRegistry.BodyRadiusUnitsKey));

        // A mid-range value applies verbatim.
        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.BodyRadiusUnitsKey, 0.4d, out var applied));
        Assert.Equal(0.4d, applied);
        Assert.Equal(0.4d, tuning.BodyRadiusUnits);

        // 0.5 (or above) clamps STRICTLY below 0.5 — never inscribes the full tile (which would jam a 1-wide gap).
        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.BodyRadiusUnitsKey, 0.5d, out var ceiled));
        Assert.True(ceiled < 0.5d, $"body radius must clamp strictly below 0.5, got {ceiled}");
        Assert.True(tuning.BodyRadiusUnits < 0.5d);

        // A 0/negative radius floors to a small positive (a 0 radius would make the swept-circle test degenerate).
        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.BodyRadiusUnitsKey, 0d, out var floored));
        Assert.True(floored > 0d);
    }

    [Fact]
    public void CombatRootTicksMatchesSharedConversion()
    {
        // The server's AttackRootTicks must equal the shared CombatTuning conversion off the live rootMs — the
        // parity invariant the client predictor mirrors via the replicated rootMs.
        var tuning = new ServerTuning(Options());
        // AttackRootTicks == the shared conversion of the LIVE rootMs — whatever the default (now 0 = no root).
        Assert.Equal(Mmo.Shared.Domain.CombatTuning.RootTicks(20, tuning.AttackRootMs), tuning.AttackRootTicks);

        Assert.True(ServerTuningRegistry.TryApply(tuning, ServerTuningRegistry.AttackRootMsKey, 350d, out _));
        Assert.Equal(Mmo.Shared.Domain.CombatTuning.RootTicks(20, 350), tuning.AttackRootTicks);
    }
}
