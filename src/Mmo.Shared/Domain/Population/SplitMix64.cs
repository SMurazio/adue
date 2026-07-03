namespace Mmo.Shared.Domain.Population;

// PROCEDURAL-POPULATION P1 (docs/procedural-population-design.md §4): a self-contained SplitMix64 PRNG
// helper shared by the three placement-math pieces in this folder (TileDistanceField has no randomness
// and doesn't need it; ValueNoise and WeightedScatter both do). Same algorithm, bit-for-bit, as the
// independent copies already living in TerrainGenerator and Zone.PlanResourceNodeScatter -- this is
// deliberately NOT a project-wide unification of those (each is its own independently-seeded stream and
// changing either would move an already-shipped ContentHash/replay). It exists here only so the THREE new
// population files in this folder don't each hand-roll a fourth copy-paste of the same eight lines.
//
// Determinism contract (same as every other PRNG in this codebase): pure 64-bit unsigned arithmetic with
// defined overflow, no System.Random, no clocks, no culture-sensitive APIs -- identical inputs yield
// byte-identical output on every platform/runtime/.NET version.
internal static class SplitMix64
{
    // Folds a 32-bit seed into a well-distributed 64-bit initial state. Identical fold constant/shape as
    // TerrainGenerator.SeedState and Zone.SeedState -- NOT the same call site, so two callers with the
    // same int seed do NOT collide in practice as long as each salts its seed per class/stream the way
    // Zone already does for resource nodes (Seed ^ ResourceNodeSeedSalt). Callers of this file's helpers
    // (WeightedScatter, and any future population code) are expected to follow the same salting practice.
    public static ulong SeedState(int seed)
    {
        return (ulong)(uint)seed * 0x9E3779B97F4A7C15UL;
    }

    // Advances the PRNG state by one step and returns the next 64-bit draw. Callers own the `state`
    // local and thread it through explicitly (no shared/static mutable state anywhere), so two
    // independent streams (e.g. two WeightedScatter calls in the same process) never interfere.
    public static ulong Next(ref ulong state)
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    // Uniform double in [0, 1) from one fresh draw. Standard "top 53 bits" technique: a double's mantissa
    // is exactly 53 bits, so shifting off the low 11 bits of a 64-bit draw and scaling gives a uniform,
    // exactly-representable value with no bias toward either end of the range.
    public static double NextDouble(ref ulong state)
    {
        var draw = Next(ref state);
        return (draw >> 11) * (1.0 / (1UL << 53));
    }

    // Uniform int in [0, exclusiveMax). exclusiveMax must be positive -- callers here always pass a map
    // width/height, which is validated positive by the caller before this is ever reached.
    public static int NextInt(ref ulong state, int exclusiveMax)
    {
        return (int)(Next(ref state) % (ulong)exclusiveMax);
    }
}
