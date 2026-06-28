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

        // The former global monster.* defaults, migrated. CONTINUOUS: roam/aggro/leash are now world-unit RANGE doubles.
        Assert.Equal(4d, slime.RoamRadius);
        Assert.Equal(2000, slime.PauseMinMs);
        Assert.Equal(5000, slime.PauseMaxMs);
        Assert.Equal(6d, slime.AggroRadius);
        Assert.Equal(12d, slime.ChaseLeash);
        Assert.Equal(1.5d, slime.AttackRangeUnits); // the continuous attack range the AI reads (the int tile knob is retired)
        Assert.Equal(10, slime.AttackDamage);
        Assert.Equal(1000, slime.AttackCooldownMs);
        Assert.Equal(100, slime.MaxHealth);

        // SLIME-FEEL-POLISH hop knobs: airborne 300 ms + delay 400 ms = a 700 ms cycle with a visible grounded rest.
        Assert.Equal(300, slime.HopAirborneMs);
        Assert.Equal(400, slime.HopDelayMs);

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

        // CONTINUOUS: roam range accepts FRACTIONAL world-units now.
        Assert.True(registry.TryApply("slime.roamRadius", 6.5d, out var r));
        Assert.Equal(6.5d, r);
        Assert.Equal(6.5d, registry.Default.RoamRadius);

        // roam range clamps to [0.5, 32] (continuous).
        Assert.True(registry.TryApply("slime.roamRadius", 0d, out _));
        Assert.True(registry.Default.RoamRadius >= 0.5d);
        Assert.True(registry.TryApply("slime.roamRadius", 999d, out _));
        Assert.True(registry.Default.RoamRadius <= 32d);

        // aggro range clamps to [0.5, 64].
        Assert.True(registry.TryApply("slime.aggroRadius", 9999d, out _));
        Assert.True(registry.Default.AggroRadius <= 64d);

        // "attack range" edits the CONTINUOUS AttackRangeUnits the AI reads (not the retired integer-tile knob),
        // accepts fractional, and clamps to [0.5, 8].
        Assert.True(registry.TryApply("slime.attackRange", 2.25d, out var ar));
        Assert.Equal(2.25d, ar);
        Assert.Equal(2.25d, registry.Default.AttackRangeUnits);
        Assert.True(registry.TryApply("slime.attackRange", 0d, out _));
        Assert.True(registry.Default.AttackRangeUnits >= 0.5d);

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
        Assert.True(registry.IsMonsterTypeKey("slime.hopDelayMs"));
        Assert.False(registry.IsMonsterTypeKey("slime.moveSpeed")); // the retired knob is no longer a recognized key.
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

    // MoveSpeedMultiplier is now INTERP-CADENCE-ONLY (it seeds the entity's replicated SpeedMultiplier at spawn — the
    // EntitySpawn / MovementSpeedChanged cadence the client interpolates at — and no longer drives the hop cadence, which
    // is HopAirborneTicks + HopDelayTicks). This still pins that the slime's 0.6x default makes its entity-level effective
    // step cooldown LONGER than the player's base: 250 ms @ 20 Hz = 5 ticks; round(5 / 0.6) = 8 ticks (~417 ms).
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
        registry.TryApply("slime.hopDelayMs", 250d, out _);

        var snapshot = registry.BuildSnapshot();
        var slime = snapshot.Types.Single();
        Assert.Equal("slime", slime.Id);
        Assert.Equal("Slime", slime.DisplayName);
        Assert.Equal(7d, FieldValue(slime, "roamRadius"));
        Assert.Equal(250d, FieldValue(slime, "hopDelayMs"));
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
        Assert.Contains("roamRadius", keys);
        Assert.Contains("respawnMs", keys);
        // The retired "move speed (x)" knob is no longer exposed.
        Assert.DoesNotContain("moveSpeed", keys);
        // The hop feel-knobs, including the new delay.
        Assert.Contains("hopDistance", keys);
        Assert.Contains("hopHeight", keys);
        Assert.Contains("hopAirborneMs", keys);
        Assert.Contains("hopDelayMs", keys);

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

        var hopDelay = slime.Fields.Single(f => f.Key == "hopDelayMs");
        Assert.Equal(400, hopDelay.Value, 6); // the default grounded rest between hops.
        Assert.Equal(0, hopDelay.Min, 6);
        Assert.Equal(5000, hopDelay.Max, 6);
        Assert.True(hopDelay.IsInteger);

        // maxHealth is an integer knob; hop distance is a fractional one.
        Assert.True(slime.Fields.Single(f => f.Key == "maxHealth").IsInteger);
        Assert.False(slime.Fields.Single(f => f.Key == "hopDistance").IsInteger);
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
        Assert.True(registry.IsMonsterTypeKey("slime.hopDelayMs"));
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

        // hopDelayMs is an integer ms knob clamped to [0, 5000] (0 = re-hop the instant it lands).
        Assert.True(registry.TryApply("slime.hopDelayMs", 600d, out var dl));
        Assert.Equal(600d, dl);
        Assert.Equal(600, slime.HopDelayMs);
        Assert.True(registry.TryApply("slime.hopDelayMs", -5d, out var dlLo));
        Assert.Equal(0d, dlLo);
        Assert.True(registry.TryApply("slime.hopDelayMs", 999999d, out var dlHi));
        Assert.Equal(5000d, dlHi);

        Assert.False(registry.TryApply("slime.hopNonsense", 1d, out _));
    }

    // SLIME-FEEL-POLISH: the hop CADENCE (time between hop starts) is HopAirborneTicks + HopDelayTicks, and the airborne
    // span is shorter than the cadence by exactly the DELAY ticks — that delay IS the grounded rest between hops.
    [Fact]
    public void DefaultHopAirborneIsShorterThanTheCadenceLeavingGroundedRest()
    {
        var registry = Registry();
        var slime = registry.Default;

        // Default 300 ms @ 20 Hz = 6 ticks airborne; 400 ms @ 20 Hz = 8 ticks delay.
        Assert.Equal(6u, registry.HopAirborneTicks(slime));
        Assert.Equal(8u, registry.HopDelayTicks(slime));

        // The hop cadence is airborne + delay = 14 ticks (~700 ms) — so airborne (6) < cadence (14): the slime rests on
        // the ground for the 8 delay ticks before the next hop starts.
        var cadence = registry.HopAirborneTicks(slime) + registry.HopDelayTicks(slime);
        Assert.Equal(14u, cadence);
        Assert.True(registry.HopAirborneTicks(slime) < cadence, "airborne span must be shorter than the cadence for rest.");
        Assert.Equal(registry.HopDelayTicks(slime), cadence - registry.HopAirborneTicks(slime)); // the rest == delay.

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

    // SLIME-FEEL-POLISH: the monster's HOP CADENCE (the value StepMonsterAi feeds the AI as stepCooldownTicks) is now
    // HopAirborneTicks + HopDelayTicks — NOT the retired moveSpeed-derived cadence. This pins that the DELAY knob dials
    // the cadence live: editing "slime.hopDelayMs" re-paces an already-spawned slime on the next tick (the registry is
    // read fresh each tick), and a longer delay lengthens the cadence while airborne stays put.
    [Fact]
    public void EditingTypeHopDelayChangesTheHopCadence()
    {
        var registry = Registry();
        var slime = registry.Default;

        // Mirror GameServer.StepMonsterAi: cadence == HopAirborneTicks + HopDelayTicks.
        uint Cadence() => registry.HopAirborneTicks(slime) + registry.HopDelayTicks(slime);

        // Default: 6 airborne (300 ms) + 8 delay (400 ms) = 14 ticks (~700 ms).
        Assert.Equal(14u, Cadence());

        // An admin lengthens the rest to 1000 ms (20 ticks) — the cadence becomes 6 + 20 = 26 ticks, strictly slower,
        // and the airborne span is unchanged (delay is the grounded rest, decoupled from flight time).
        Assert.True(registry.TryApply("slime.hopDelayMs", 1000d, out _));
        Assert.Equal(6u, registry.HopAirborneTicks(slime));
        Assert.Equal(20u, registry.HopDelayTicks(slime));
        Assert.Equal(26u, Cadence());
        Assert.True(Cadence() > 14u, "lengthening the delay must lengthen the hop cadence live.");

        // A 0 ms delay means re-hop the instant it lands — the cadence collapses to the airborne span alone.
        Assert.True(registry.TryApply("slime.hopDelayMs", 0d, out _));
        Assert.Equal(0u, registry.HopDelayTicks(slime));
        Assert.Equal(registry.HopAirborneTicks(slime), Cadence());
    }
}
