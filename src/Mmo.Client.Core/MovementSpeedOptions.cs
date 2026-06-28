using System.Globalization;

namespace Mmo.Client.Core;

// S106 — the discrete tick-quantized speed set behind the F6 "Move speed" dropdown. The dropdown is a TESTING /
// feel-tuning control: it sets the LOCAL player's per-entity speed live via /speed <multiplier> (the existing
// per-entity path, GameServer.HandleSpeedCommand -> WorldEntity.TrySetSpeedMultiplier -> MovementSpeedChanged),
// NOT the global move.stepCooldownMs (that is F4 server tuning).
//
// The speeds are UNNAMED (no Walk/Run brackets — a user requirement): each option is one tick-quantized cadence,
// labelled by NUMBERS only. For a cadence of N server ticks the multiplier that yields exactly N ticks is
// baseWalkTicks / N (so N == baseWalkTicks is exactly 1.0x — the default walk). cadence = N * tickIntervalMs;
// units/s = 1000 / cadence.
//
// N is clamped to the server's effective-cooldown range (MinEffectiveStepCooldownMs / MaxEffectiveStepCooldownMs,
// quantized to ticks) so the dropdown never offers a speed the server would refuse — picking an out-of-range N
// would just be clamped server-side to a DIFFERENT cadence than the label promises, so we drop it. Pure +
// deterministic so the multiplier->cadence->label math unit-tests directly.
public static class MovementSpeedOptions
{
    // The candidate cadence lengths in WHOLE server ticks, fastest (N=1) to slowest. N=baseWalkTicks is the
    // default 1.0x walk; the spread brackets it both faster and slower for feel-testing. Out-of-clamp entries are
    // dropped by Build, so this is a superset.
    private static readonly int[] CandidateTicks = { 1, 2, 3, 4, 5, 6, 8 };

    // The server's effective-cooldown clamp in ms (GameServer.MinEffectiveStepCooldownMs /
    // MaxEffectiveStepCooldownMs). Mirrored here so the dropdown only offers cadences the server will honour
    // exactly. If these ever change server-side, update them here (the server stays authoritative either way —
    // an out-of-range pick would just be clamped, which is why we pre-filter).
    public const int MinEffectiveStepCooldownMs = 50;
    public const int MaxEffectiveStepCooldownMs = 5000;

    public readonly record struct SpeedOption(int Ticks, double Multiplier, double CadenceMs, double UnitsPerSecond, string Label)
    {
        // Whether this option is the default walk (multiplier 1.0 == baseWalkTicks). The dropdown preselects it.
        public bool IsDefaultWalk => Ticks > 0 && Math.Abs(Multiplier - 1.0d) < 1e-9d;
    }

    // Builds the in-range, ordered speed options for a given base-walk cadence (the server's StepCooldownMs from
    // ServerHello) and tick rate. baseWalkTicks is derived the SAME way the server does (ceil(stepCooldownMs /
    // tickIntervalMs), >= 1) so N == baseWalkTicks lands exactly on 1.0x. Returns fastest-first.
    public static IReadOnlyList<SpeedOption> Build(int baseStepCooldownMs, int tickRate)
    {
        var rate = tickRate > 0 ? tickRate : 20;
        var tickIntervalMs = 1000d / rate;
        var baseWalkTicks = Math.Max(1, (int)Math.Ceiling(baseStepCooldownMs / tickIntervalMs));

        // The clamp expressed in ticks, mirroring GameServer.Min/MaxEffectiveStepCooldownTicks (ceil of the ms
        // clamp over the tick interval, >= 1). A candidate N outside [minTicks, maxTicks] is dropped.
        var minTicks = Math.Max(1, (int)Math.Ceiling(MinEffectiveStepCooldownMs / tickIntervalMs));
        var maxTicks = Math.Max(1, (int)Math.Ceiling(MaxEffectiveStepCooldownMs / tickIntervalMs));

        var options = new List<SpeedOption>();
        foreach (var n in CandidateTicks)
        {
            if (n < minTicks || n > maxTicks)
            {
                continue;
            }

            var multiplier = (double)baseWalkTicks / n;
            var cadenceMs = n * tickIntervalMs;
            var unitsPerSecond = 1000d / cadenceMs;
            options.Add(new SpeedOption(n, multiplier, cadenceMs, unitsPerSecond, FormatLabel(multiplier, cadenceMs, unitsPerSecond)));
        }

        return options;
    }

    // Numbers-only label, e.g. "1.50x - 100 ms - 10.0/s". No bracket name (the user requirement). Invariant
    // culture so the dropdown reads the same everywhere and the /speed value formats with a '.' decimal.
    public static string FormatLabel(double multiplier, double cadenceMs, double unitsPerSecond)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.00}x - {1:0} ms - {2:0.0}/s",
            multiplier,
            cadenceMs,
            unitsPerSecond);
    }

    // The /speed command argument for a multiplier, formatted invariantly (a '.' decimal) so the server's
    // double.TryParse(... InvariantCulture) in HandleSpeedCommand reads it back exactly. Enough precision that
    // baseWalkTicks/N round-trips (e.g. 3/8 = 0.375).
    public static string FormatSpeedCommandArgument(double multiplier)
    {
        return multiplier.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
