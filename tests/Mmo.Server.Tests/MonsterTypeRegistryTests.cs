using System.Linq;
using Mmo.Server.Runtime;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Server.Tests;

// LIVING-ENEMIES P2-POLISH: unit coverage for the monster TYPE registry — the named-template store + the per-type
// live-tuning apply/clamp + the tick-quantised Tunables + the replicated snapshot. These pin the type-driven tuning
// that replaced the former global monster.* block: the slime type seeds the migrated defaults (with the NEW slower
// move speed), per-type keys apply + clamp + keep the pause range non-inverted, an unknown type/field is rejected,
// and BuildTunables derives the same tick-quantised values the old ServerTuning did.
public sealed class MonsterTypeRegistryTests
{
    private const int TickRate = 20;

    private static MonsterTypeRegistry Registry() => new(TickRate);

    [Fact]
    public void SeedsTheSlimeTypeWithMigratedDefaults()
    {
        var registry = Registry();

        Assert.Single(registry.Types);
        Assert.True(registry.TryGet("slime", out var slime));
        Assert.Equal("slime", slime.Id);
        Assert.Equal("Slime", slime.DisplayName);

        // The former global monster.* defaults, migrated verbatim.
        Assert.Equal(4, slime.RoamRadius);
        Assert.Equal(2000, slime.PauseMinMs);
        Assert.Equal(5000, slime.PauseMaxMs);
        Assert.Equal(6, slime.AggroRadius);
        Assert.Equal(12, slime.ChaseLeash);
        Assert.Equal(1, slime.AttackRange);
        Assert.Equal(10, slime.AttackDamage);
        Assert.Equal(1000, slime.AttackCooldownMs);
        Assert.Equal(100, slime.MaxHealth);

        // The slower-than-player default: < 1.0 so the player (base 1.0) outruns it. LOOT P4c lowered it 0.8 → 0.6
        // (clearly outrunnable: ~417 ms/step vs the player's 250 ms).
        Assert.True(slime.MoveSpeedMultiplier < 1.0);
        Assert.Equal(0.6, slime.MoveSpeedMultiplier, 3);

        // LOOT P4a: the slime references the "slime_loot" table (static seed data; not live-tunable).
        Assert.Equal("slime_loot", slime.LootTableId);
    }

    [Fact]
    public void DefaultResolvesToSlimeAndLookupIsCaseInsensitive()
    {
        var registry = Registry();
        Assert.Equal("slime", registry.Default.Id);
        Assert.True(registry.TryGet("SLIME", out var s));
        Assert.Equal("slime", s.Id);
        Assert.False(registry.TryGet("dragon", out _));
    }

    [Fact]
    public void PerTypeKeysApplyAndClamp()
    {
        var registry = Registry();

        Assert.True(registry.TryApply("slime.roamRadius", 6d, out var r));
        Assert.Equal(6d, r);
        Assert.Equal(6, registry.Default.RoamRadius);

        // roam radius clamps to [1, 32].
        Assert.True(registry.TryApply("slime.roamRadius", 0d, out _));
        Assert.True(registry.Default.RoamRadius >= 1);
        Assert.True(registry.TryApply("slime.roamRadius", 999d, out _));
        Assert.True(registry.Default.RoamRadius <= 32);

        // aggro radius clamps to [1, 64].
        Assert.True(registry.TryApply("slime.aggroRadius", 9999d, out _));
        Assert.True(registry.Default.AggroRadius <= 64);

        // moveSpeed clamps to [0.1, 5].
        Assert.True(registry.TryApply("slime.moveSpeed", 0d, out var ms));
        Assert.True(ms >= 0.1);
        Assert.True(registry.TryApply("slime.moveSpeed", 99d, out _));
        Assert.True(registry.Default.MoveSpeedMultiplier <= 5);

        // maxHealth clamps to >= 1.
        Assert.True(registry.TryApply("slime.maxHealth", 0d, out _));
        Assert.True(registry.Default.MaxHealth >= 1);
    }

    [Fact]
    public void PauseRangeStaysNonInverted()
    {
        var registry = Registry(); // defaults: min 2000, max 5000.

        Assert.True(registry.TryApply("slime.pauseMinMs", 8000d, out _));
        Assert.Equal(8000, registry.Default.PauseMinMs);
        Assert.True(registry.Default.PauseMaxMs >= registry.Default.PauseMinMs);

        Assert.True(registry.TryApply("slime.pauseMaxMs", 1000d, out _));
        Assert.Equal(1000, registry.Default.PauseMaxMs);
        Assert.True(registry.Default.PauseMinMs <= registry.Default.PauseMaxMs);
    }

    [Fact]
    public void UnknownTypeOrFieldIsRejected()
    {
        var registry = Registry();
        Assert.False(registry.TryApply("dragon.roamRadius", 5d, out _)); // unknown type.
        Assert.False(registry.TryApply("slime.nonsense", 5d, out _));    // unknown field.
        Assert.False(registry.TryApply("noDot", 5d, out _));             // malformed.
        Assert.False(registry.TryApply("slime.", 5d, out _));            // trailing dot.
    }

    [Fact]
    public void IsMonsterTypeKeyMatchesOnlyKnownTypeFields()
    {
        var registry = Registry();
        Assert.True(registry.IsMonsterTypeKey("slime.roamRadius"));
        Assert.True(registry.IsMonsterTypeKey("slime.moveSpeed"));
        Assert.False(registry.IsMonsterTypeKey("dragon.roamRadius"));
        Assert.False(registry.IsMonsterTypeKey("slime.nonsense"));
        Assert.False(registry.IsMonsterTypeKey("combat.damage"));
    }

    [Fact]
    public void BuildTunablesIsTickQuantisedLikeTheOldHolder()
    {
        var registry = Registry();
        var t = registry.BuildTunables(registry.Default);

        // Pause: 2000 ms @ 20 Hz = 40 ticks; 5000 ms = 100 ticks.
        Assert.Equal(40u, t.PauseMinTicks);
        Assert.Equal(100u, t.PauseMaxTicks);
        // Attack cooldown: 1000 ms @ 20 Hz = 20 ticks.
        Assert.Equal(20u, t.AttackCooldownTicks);
        // Aggro scan ~0.5 s @ 20 Hz = 10 ticks.
        Assert.Equal(10u, t.AggroScanIntervalTicks);
        // De-aggro derived ×1.5 of aggro (6 → 9), strictly beyond acquire.
        Assert.Equal(9, t.DeaggroRadius);
        Assert.True(t.DeaggroRadius > t.AggroRadius);
        // The straight-through values.
        Assert.Equal(6, t.AggroRadius);
        Assert.Equal(12, t.ChaseLeash);
        Assert.Equal(1.5, t.AttackRangeUnits);
        Assert.Equal(10, t.AttackDamage);
        Assert.Equal(4, t.RoamRadius);
    }

    // LIVING-ENEMIES P2-POLISH item 4 (LOOT P4c default tweak): applying the slime type's default move speed yields a
    // LONGER effective step cooldown than the player's base (the monster is slower → outrunnable). Base 250 ms @ 20 Hz
    // = 5 ticks; at the NEW 0.6x default the slime's effective cooldown rounds to 8 ticks (~417 ms) — clearly slower
    // than the player.
    [Fact]
    public void SlimeMoveSpeedMakesItStepSlowerThanTheBase()
    {
        var registry = Registry();
        var slime = registry.Default;

        var monster = new WorldEntity(
            id: 1, networkId: 1, EntityKind.Monster,
            new TileCoord(10, 10), Direction8.S, "Slime",
            characterId: null, ownerSession: null, isDurable: false);
        Assert.True(monster.TrySetSpeedMultiplier(slime.MoveSpeedMultiplier));

        const uint baseCooldownTicks = 5; // 250 ms @ 20 Hz.
        var effective = monster.EffectiveStepCooldownTicks(baseCooldownTicks, minTicks: 1, maxTicks: 100);

        Assert.True(effective > baseCooldownTicks, $"expected slime cadence > {baseCooldownTicks}, got {effective}.");
        Assert.Equal(8u, effective); // round(5 / 0.6) = 8.
    }

    [Fact]
    public void SnapshotReflectsLiveValues()
    {
        var registry = Registry();
        registry.TryApply("slime.roamRadius", 7d, out _);
        registry.TryApply("slime.moveSpeed", 0.5d, out _);

        var snapshot = registry.BuildSnapshot();
        var slime = snapshot.Types.Single();
        Assert.Equal("slime", slime.Id);
        Assert.Equal("Slime", slime.DisplayName);
        Assert.Equal(7d, FieldValue(slime, "roamRadius"));
        Assert.Equal(0.5, FieldValue(slime, "moveSpeed"), 3);
        Assert.Equal(100d, FieldValue(slime, "maxHealth"));
        Assert.Equal(5000d, FieldValue(slime, "respawnMs")); // P3 default ~5 s, unchanged.
    }

    // Reads one replicated field's current value by its wire key (DATA-DRIVEN snapshot helper).
    private static double FieldValue(MonsterTypeSnapshot t, string key) => t.Fields.Single(f => f.Key == key).Value;

    // DATA-DRIVEN (v40): the descriptor-built snapshot exposes the expected field set — including the 3 hop knobs —
    // each with its current value, clamp bounds, and the right int-vs-double flag.
    [Fact]
    public void SnapshotFieldsAreDataDrivenIncludingHopKnobs()
    {
        var registry = Registry();
        var slime = registry.BuildSnapshot().Types.Single();

        var keys = slime.Fields.Select(f => f.Key).ToArray();
        Assert.Contains("maxHealth", keys);
        Assert.Contains("moveSpeed", keys);
        Assert.Contains("roamRadius", keys);
        Assert.Contains("respawnMs", keys);
        // The newly exposed hop knobs.
        Assert.Contains("hopDistance", keys);
        Assert.Contains("hopHeight", keys);
        Assert.Contains("hopAirborneMs", keys);

        var hopDistance = slime.Fields.Single(f => f.Key == "hopDistance");
        Assert.Equal(1.5, hopDistance.Value, 6); // the bumped default ("range too low" fix).
        Assert.Equal(0.25, hopDistance.Min, 6);
        Assert.Equal(8d, hopDistance.Max, 6);
        Assert.False(hopDistance.IsInteger);

        var hopAirborne = slime.Fields.Single(f => f.Key == "hopAirborneMs");
        Assert.Equal(300, hopAirborne.Value, 6);
        Assert.Equal(50, hopAirborne.Min, 6);
        Assert.Equal(2000, hopAirborne.Max, 6);
        Assert.True(hopAirborne.IsInteger);

        // maxHealth is an integer knob; moveSpeed is a fractional one.
        Assert.True(slime.Fields.Single(f => f.Key == "maxHealth").IsInteger);
        Assert.False(slime.Fields.Single(f => f.Key == "moveSpeed").IsInteger);
    }

    // DATA-DRIVEN (v40): the 3 hop knobs apply + clamp through TryApply and are recognized keys; an unknown field is
    // still rejected. Pins the "one descriptor + one TryApply case" contract for the new fields.
    [Fact]
    public void HopFieldsApplyClampAndAreKnownKeys()
    {
        var registry = Registry();
        var slime = registry.Default;

        Assert.True(registry.IsMonsterTypeKey("slime.hopDistance"));
        Assert.True(registry.IsMonsterTypeKey("slime.hopHeight"));
        Assert.True(registry.IsMonsterTypeKey("slime.hopAirborneMs"));
        Assert.False(registry.IsMonsterTypeKey("slime.hopNonsense"));

        Assert.True(registry.TryApply("slime.hopDistance", 3.0d, out var d));
        Assert.Equal(3.0d, d);
        Assert.Equal(3.0d, slime.HopDistanceUnits, 6);

        // hopDistance clamps to [0.25, 8].
        Assert.True(registry.TryApply("slime.hopDistance", 0d, out var dLo));
        Assert.Equal(0.25d, dLo, 6);
        Assert.True(registry.TryApply("slime.hopDistance", 999d, out var dHi));
        Assert.Equal(8d, dHi, 6);

        // hopHeight clamps to [0, 4].
        Assert.True(registry.TryApply("slime.hopHeight", -5d, out var hLo));
        Assert.Equal(0d, hLo, 6);
        Assert.True(registry.TryApply("slime.hopHeight", 100d, out var hHi));
        Assert.Equal(4d, hHi, 6);

        // hopAirborneMs is an integer ms knob clamped to [50, 2000].
        Assert.True(registry.TryApply("slime.hopAirborneMs", 800d, out var a));
        Assert.Equal(800d, a);
        Assert.Equal(800, slime.HopAirborneMs);
        Assert.True(registry.TryApply("slime.hopAirborneMs", 10d, out var aLo));
        Assert.Equal(50d, aLo);
        Assert.True(registry.TryApply("slime.hopAirborneMs", 999999d, out var aHi));
        Assert.Equal(2000d, aHi);

        Assert.False(registry.TryApply("slime.hopNonsense", 1d, out _));
    }

    // DATA-DRIVEN (the "hops too often" fix): the default hop is airborne for FEWER ticks than the move cadence, so a
    // grounded rest exists between hops. HopAirborneTicks derives from the live HopAirborneMs (round, floored at 1).
    [Fact]
    public void DefaultHopAirborneIsShorterThanTheCadenceLeavingGroundedRest()
    {
        var registry = Registry();
        var slime = registry.Default;

        // Default 300 ms @ 20 Hz = 6 ticks airborne.
        Assert.Equal(6u, registry.HopAirborneTicks(slime));

        // The move cadence at the slime's 0.6x default is 8 ticks (round(5 / 0.6)) — so airborne (6) < cadence (8):
        // the slime rests on the ground for ~2 ticks before the next hop starts.
        const uint baseTicks = 5; // 250 ms @ 20 Hz, the player base cadence.
        var cadence = (uint)Math.Clamp(
            (long)Math.Max(1, Math.Round(baseTicks / slime.MoveSpeedMultiplier, MidpointRounding.AwayFromZero)), 1L, 100L);
        Assert.Equal(8u, cadence);
        Assert.True(registry.HopAirborneTicks(slime) < cadence, "airborne span must be shorter than the cadence for rest.");

        // A live retune re-derives the airborne ticks.
        Assert.True(registry.TryApply("slime.hopAirborneMs", 1000d, out _));
        Assert.Equal(20u, registry.HopAirborneTicks(slime)); // 1000 ms @ 20 Hz = 20 ticks.
    }

    // LIVING-ENEMIES P3: the per-type respawn delay applies + clamps, is a known key, and derives a tick count.
    [Fact]
    public void RespawnDelayAppliesClampsAndDerivesTicks()
    {
        var registry = Registry();
        Assert.True(registry.TryGet("slime", out var slime));
        Assert.Equal(5000, slime.RespawnMs);
        Assert.Equal(100u, registry.RespawnTicks(slime)); // 5000 ms / 50 ms = 100 ticks at 20 Hz.

        Assert.True(registry.IsMonsterTypeKey("slime.respawnMs"));
        Assert.True(registry.TryApply("slime.respawnMs", 3000d, out var applied));
        Assert.Equal(3000d, applied);
        Assert.Equal(3000, slime.RespawnMs);
        Assert.Equal(60u, registry.RespawnTicks(slime));

        // Clamps a wild value to the 5-minute max.
        Assert.True(registry.TryApply("slime.respawnMs", 999999999d, out var clamped));
        Assert.Equal(300000d, clamped);

        // The snapshot reflects it.
        var snapshot = registry.BuildSnapshot();
        Assert.Equal(300000d, FieldValue(snapshot.Types.Single(), "respawnMs"));
    }

    // LOOT P4c (monster-types follow-up #1): the monster's step cadence is now derived from its TYPE's LIVE
    // MoveSpeedMultiplier each tick (GameServer.EffectiveStepCooldownTicksFor), not the entity's spawn-time
    // SpeedMultiplier — so editing "slime.moveSpeed" re-paces an ALREADY-SPAWNED slime on the next tick. This pins the
    // derivation: applying a new moveSpeed changes the cadence the type yields (the formula StepMonsterAi feeds the AI),
    // proving the knob dials live. (round(base / multiplier), clamped — matching the server helper.)
    [Fact]
    public void EditingTypeMoveSpeedChangesTheDerivedCadence()
    {
        var registry = Registry();
        var slime = registry.Default;

        // Mirror GameServer.EffectiveStepCooldownTicksFor: base 5 ticks (250 ms @ 20 Hz), min 1, max 100.
        const uint baseTicks = 5;
        static uint Cadence(double multiplier) =>
            (uint)Math.Clamp((long)Math.Max(1, Math.Round(baseTicks / multiplier, MidpointRounding.AwayFromZero)), 1L, 100L);

        // Default 0.6 -> round(5 / 0.6) = 8 ticks (the new outrunnable cadence).
        Assert.Equal(8u, Cadence(slime.MoveSpeedMultiplier));

        // An admin speeds the type up to 1.0 (player pace). Because StepMonsterAi reads the TYPE value each tick, an
        // already-spawned slime's cadence becomes round(5 / 1.0) = 5 next tick — strictly faster than before.
        Assert.True(registry.TryApply("slime.moveSpeed", 1.0d, out _));
        Assert.Equal(5u, Cadence(slime.MoveSpeedMultiplier));
        Assert.True(Cadence(slime.MoveSpeedMultiplier) < 8u, "speeding the type up must shorten the cadence live.");

        // And slowing it to 0.5 lengthens it to round(5 / 0.5) = 10 ticks — the same live re-pacing the other way.
        Assert.True(registry.TryApply("slime.moveSpeed", 0.5d, out _));
        Assert.Equal(10u, Cadence(slime.MoveSpeedMultiplier));
    }
}
