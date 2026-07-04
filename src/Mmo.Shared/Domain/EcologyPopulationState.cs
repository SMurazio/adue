namespace Mmo.Shared.Domain;

// ECOLOGY E4 (docs/ecology-v1-design.md D5, §3/§8 E4): the WIRE projection of the server-only
// EcologyState.PopulationState (Mmo.Server.Runtime) — the five legible population states a region×type can be
// in. Declared here (SHARED, not server-only) because both the client's minimap overlay (Mmo.Client.Core) and
// the server's /rumors text need to read a REPLICATED state, and only Shared is visible to both sides of the
// wire. Ordinal-identical to the server enum BY DESIGN (EcologyWire.ToWireState maps between them with an
// explicit switch, not a raw cast, so a future reordering of either enum fails to COMPILE, not ship silently).
//
// D5: fuzzy words, never numbers — this enum (and its byte wire form) is the ONLY ecology signal that ever
// reaches a client. Exact stock/pressure numbers stay server + admin-only (/ecology).
public enum EcologyPopulationState : byte
{
    Depleted = 0,
    Thin = 1,
    Healthy = 2,
    Rich = 3,
    Overgrown = 4,
}

// The shared "which state governs when a region hosts several monster types" rule (D6a/D6b) — used by BOTH the
// minimap overlay color (client, MinimapEcologyOverlay) and the server's /rumors line (EcologyRumors), so the
// two D6 legibility surfaces can never disagree about which type's state a mixed region is showing.
public static class EcologyLegibility
{
    // Severity is NOT a symmetric "distance from Healthy": DEPLETED is the most urgent (something is visibly
    // wrong), OVERGROWN is next (notable, but not alarming — bigger/meaner monsters, not a wound), then THIN,
    // then RICH (a mild positive), then HEALTHY last (nothing worth flagging — the unremarkable baseline).
    private static readonly EcologyPopulationState[] SeverityOrder =
    [
        EcologyPopulationState.Depleted,
        EcologyPopulationState.Overgrown,
        EcologyPopulationState.Thin,
        EcologyPopulationState.Rich,
        EcologyPopulationState.Healthy,
    ];

    // The single WORST state among `states` (a region's per-type entries), by the severity order above. Ties
    // within the SAME severity rank resolve to the FIRST matching entry in `states`' own order (so a caller that
    // hands types in authored order gets a stable, reviewable "first wins" tie-break). Empty input defaults to
    // Healthy (defensive only — EcologyRegistry requires every authored region to host >=1 type).
    public static EcologyPopulationState WorstOf(IEnumerable<EcologyPopulationState> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        var best = EcologyPopulationState.Healthy;
        var bestRank = int.MaxValue;
        var any = false;
        foreach (var state in states)
        {
            var rank = Array.IndexOf(SeverityOrder, state);
            if (!any || rank < bestRank)
            {
                best = state;
                bestRank = rank;
                any = true;
            }
        }

        return best;
    }

    // A SYMMETRIC "how far from the unremarkable baseline" measure — used ONLY to pick the single most-extreme
    // region for the login rumor (D6c: "max distance from Healthy in either direction"). Deliberately different
    // from WorstOf's asymmetric severity ranking: Depleted and Overgrown are EQUALLY extreme here (distance 2),
    // as are Thin and Rich (distance 1), which is exactly the "either direction" the design calls for.
    public static int DistanceFromHealthy(EcologyPopulationState state) =>
        Math.Abs((int)state - (int)EcologyPopulationState.Healthy);
}
