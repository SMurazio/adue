using System.Globalization;

namespace Mmo.Server.Runtime;

// S60 tuning registry: the table of which keys an admin may set live and how. Each entry knows how to
// clamp/validate an incoming double and apply it to the ServerTuning holder, returning the value actually
// stored (post-clamp) so the caller can log/echo the authoritative result. Adding a new live knob is one
// entry here + one field on ServerTuning + (optionally) one client field — that is the whole extension
// surface. Unknown keys are rejected here (TryApply returns false) and ignored+logged by the handler.
//
// SPEED1 (2026-06-21): the global base step cooldown is now PINNED — it is no longer a live knob (the
// move.stepCooldownMs key was removed) so an admin can't retune everyone's base walk speed mid-run. The
// base is a constant 150 ms (3 ticks at 20 Hz); per-entity /speed (SpeedMultiplier) still scales off it.
//
// Bounds mirror the startup ServerOptions.Validate() bounds so live values can never reach a state the
// server would have refused to boot with: interest radius (0, MaxInterestRadius]. No persistence — see ServerTuning.
public static class ServerTuningRegistry
{
    public const string InterestRadiusKey = "aoi.interestRadius";

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

    public static bool IsKnownKey(string key) => key is InterestRadiusKey;

    public static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
