using Mmo.Shared.Domain;
using Mmo.Shared.Protocol;

namespace Mmo.Server.Runtime;

// ECOLOGY E4 (docs/ecology-v1-design.md §3/§8 E4): the WIRE PROJECTION seam — builds the fuzzy-words-only
// RegionEcologyMessage for one authored region from the live EcologyState, plus the shared "which type governs a
// mixed region" read (WorstStateOf) that both the minimap overlay and /rumors need. No stock/pressure number
// ever crosses this seam (D5): only the region's authored geometry/name (immutable, from EcologyRegistry) and
// each hosted type's CURRENT PopulationState, converted 1:1 to the shared wire enum EcologyPopulationState.
internal static class EcologyWire
{
    // D5: EcologyState.PopulationState (server-only) and EcologyPopulationState (Mmo.Shared, the wire form) are
    // ordinal-identical BY DESIGN, but mapped with an explicit switch (not a raw cast) so a future reordering of
    // either enum fails to COMPILE here instead of silently shipping the wrong byte on the wire.
    public static EcologyPopulationState ToWireState(EcologyState.PopulationState state) => state switch
    {
        EcologyState.PopulationState.Depleted => EcologyPopulationState.Depleted,
        EcologyState.PopulationState.Thin => EcologyPopulationState.Thin,
        EcologyState.PopulationState.Healthy => EcologyPopulationState.Healthy,
        EcologyState.PopulationState.Rich => EcologyPopulationState.Rich,
        EcologyState.PopulationState.Overgrown => EcologyPopulationState.Overgrown,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown ecology population state."),
    };

    // Builds the full wire message for one authored region: its id/display name/rect (from the registry,
    // immutable) plus one {typeId, state} entry per hosted type. Entry order follows `region.Types.Keys`
    // (a Dictionary's insertion order in practice, not a documented guarantee) — that's fine, because neither the
    // minimap overlay nor /rumors depend on entry order, only on the WORST state across all entries (order-
    // independent by definition; see WorstStateOf).
    public static RegionEcologyMessage BuildMessage(EcologyState ecology, EcologyRegion region)
    {
        var entries = new List<RegionEcologyTypeEntry>(region.Types.Count);
        foreach (var typeId in region.Types.Keys)
        {
            entries.Add(new RegionEcologyTypeEntry(typeId, ToWireState(ecology.StateOf(region.Id, typeId))));
        }

        return new RegionEcologyMessage(region.Id, region.DisplayName, region.MinX, region.MinY, region.MaxX, region.MaxY, entries);
    }

    // The WORST state across a region's hosted types (EcologyLegibility.WorstOf, shared with the client's minimap
    // overlay) — the single value that also drives the server's /rumors line. Reads live off EcologyState so the
    // caller never needs to round-trip through a just-built RegionEcologyMessage.
    public static EcologyPopulationState WorstStateOf(EcologyState ecology, EcologyRegion region)
    {
        var states = new List<EcologyPopulationState>(region.Types.Count);
        foreach (var typeId in region.Types.Keys)
        {
            states.Add(ToWireState(ecology.StateOf(region.Id, typeId)));
        }

        return EcologyLegibility.WorstOf(states);
    }
}
