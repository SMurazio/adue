using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// ECOLOGY E4 (docs/ecology-v1-design.md D6a, §3/§8 E4): the minimap's region-shading COLOR TABLE — pure,
// Godot-free, so MinimapEcologyOverlayTests pins the exact RGBA per state without a live client. Minimap.cs's
// region overlay draws one translucent rect per authored region, tinted by whichever color this returns for the
// region's WORST type-state (EcologyLegibility.WorstOf over RegionEcologyMessage.Types) — the SAME worst-state
// rule the server's /rumors line uses (EcologyLegibility, shared Mmo.Shared.Domain), so the two D6 legibility
// surfaces never disagree about which type governs a mixed region.
//
// HEALTHY reads as "nothing to show" (alpha 0) — a healthy region is the unremarkable baseline; only a DEVIATING
// region earns screen ink, so a busy minimap never buries the regions that actually need attention. RICH is a
// DEEPER green than the (absent) healthy baseline would be, reading as a positive standout rather than danger.
// OVERGROWN is violet — notable and a little uncanny (D7: bigger, meaner monsters) without reading as the
// outright warning DEPLETED's red does.
public static class MinimapEcologyOverlay
{
    public static (byte R, byte G, byte B, byte A) ColorFor(EcologyPopulationState state) => state switch
    {
        EcologyPopulationState.Depleted => (0xB0, 0x30, 0x30, 0x60),
        EcologyPopulationState.Thin => (0xC0, 0x90, 0x20, 0x50),
        EcologyPopulationState.Healthy => (0x00, 0x00, 0x00, 0x00),
        EcologyPopulationState.Rich => (0x20, 0x80, 0x30, 0x55),
        EcologyPopulationState.Overgrown => (0x80, 0x30, 0xA0, 0x60),
        _ => (0x00, 0x00, 0x00, 0x00),
    };

    // The region's worst-type-state color, given the wire's per-type states. Delegates the "which state governs
    // a mixed region" rule to EcologyLegibility.WorstOf (shared with the server's /rumors line) so the minimap and
    // /rumors always read the same story for the same region.
    public static (byte R, byte G, byte B, byte A) WorstColorFor(IEnumerable<EcologyPopulationState> states) =>
        ColorFor(EcologyLegibility.WorstOf(states));
}
