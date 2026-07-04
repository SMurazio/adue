using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// ECOLOGY E4 (docs/ecology-v1-design.md D6b/D6c, §8 E4): the flavor-text TABLE for the /rumors command + the
// login rumor — ONE format string per D5 legibility state, written ONCE here so HandleRumorsCommand (every
// region, every state) and the login rumor (the single most-extreme region) share identical wording. D6b:
// "/rumors is SERVER-side text — no client parsing", so this table stays server-only; the client only ever sees
// the finished ChatBroadcast line, never the template or the state it was chosen from.
internal static class EcologyRumors
{
    // {0} = the region's authored display name. Overgrown mentions D7's visible payoff (bigger AND meaner
    // monsters) without a number — "unusual numbers, and unnatural size" reads the D7 mechanic in fuzzy words.
    private static readonly Dictionary<EcologyPopulationState, string> LineByState = new()
    {
        [EcologyPopulationState.Depleted] = "{0} has been hunted to the brink.",
        [EcologyPopulationState.Thin] = "Game grows scarce in {0}.",
        [EcologyPopulationState.Healthy] = "{0} teems with its usual life.",
        [EcologyPopulationState.Rich] = "{0} flourishes.",
        [EcologyPopulationState.Overgrown] = "{0} is overrun — travelers report unusual numbers, and unnatural size.",
    };

    // The flavor line for one region already at its WORST type-state (EcologyWire.WorstStateOf) — the /rumors
    // per-region line and the login rumor's single line both resolve through this one formatter.
    public static string LineFor(string displayName, EcologyPopulationState worstState) =>
        string.Format(LineByState[worstState], displayName);
}
