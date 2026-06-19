using System.Globalization;

namespace Mmo.Server.Runtime;

// S60 tuning registry: the table of which keys an admin may set live and how. Each entry knows how to
// clamp/validate an incoming double and apply it to the ServerTuning holder, returning the value actually
// stored (post-clamp) so the caller can log/echo the authoritative result. Adding a new live knob is one
// entry here + one field on ServerTuning + (optionally) one client field — that is the whole extension
// surface. Unknown keys are rejected here (TryApply returns false) and ignored+logged by the handler.
//
// Bounds mirror the startup ServerOptions.Validate() bounds so live values can never reach a state the
// server would have refused to boot with: step cooldown [50, 5000] ms (also the per-entity effective
// clamp), interest radius (0, MaxInterestRadius]. No persistence — see ServerTuning.
public static class ServerTuningRegistry
{
    public const string StepCooldownMsKey = "move.stepCooldownMs";
    public const string TurnDelayMsKey = "move.turnDelayMs";
    public const string InterestRadiusKey = "aoi.interestRadius";

    private const int MinStepCooldownMs = 50;
    private const int MaxStepCooldownMs = 5000;

    // Turn-delay live bounds mirror the startup ServerOptions.Validate() bound [0, 1000] ms. 0 is permitted at
    // the registry level, but the tick quantisation (Max(1, …)) still costs a turn at least one tick.
    private const int MinTurnDelayMs = 0;
    private const int MaxTurnDelayMs = 1000;

    // Sane upper bound for a live AOI radius. The startup options only require > 0; here a live max guards
    // against an admin typo turning every AOI query into a near-world scan and stalling the tick loop.
    private const float MinInterestRadius = 1f;
    private const float MaxInterestRadius = 512f;

    // Applies a tuning key to the holder, clamping/validating first. Returns false for an unknown key (the
    // caller ignores + logs). On success, `applied` is the post-clamp value actually stored.
    public static bool TryApply(ServerTuning tuning, string key, double value, out double applied)
    {
        applied = 0d;
        if (!double.IsFinite(value))
        {
            return false;
        }

        switch (key)
        {
            case StepCooldownMsKey:
            {
                var clamped = Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), MinStepCooldownMs, MaxStepCooldownMs);
                tuning.StepCooldownMs = clamped;
                applied = clamped;
                return true;
            }
            case TurnDelayMsKey:
            {
                var clamped = Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), MinTurnDelayMs, MaxTurnDelayMs);
                tuning.TurnDelayMs = clamped;
                applied = clamped;
                return true;
            }
            case InterestRadiusKey:
            {
                var clamped = Math.Clamp((float)value, MinInterestRadius, MaxInterestRadius);
                tuning.InterestRadius = clamped;
                applied = clamped;
                return true;
            }
            default:
                return false;
        }
    }

    public static bool IsKnownKey(string key) => key is StepCooldownMsKey or TurnDelayMsKey or InterestRadiusKey;

    public static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
