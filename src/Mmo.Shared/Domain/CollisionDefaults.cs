namespace Mmo.Shared.Domain;

// CONTINUOUS MIGRATION (Phase 2): the SHARED collision constants both the server default AND the future Phase-4
// client read, so the common (un-retuned) path needs NO wire field for them — the server's default and the client's
// derived radius come from the same constant. A server-side feel override (ServerTuning.BodyRadiusUnits) only changes
// the server until Phase 3 decides whether to replicate it; the DEFAULT here is the byte-identical baseline.
public static class CollisionDefaults
{
    // The player body radius in world units. 0.5 inscribes a 1x1 body in a 1x1 tile. STRICTLY < 0.5 in practice
    // (ServerTuning clamps the live knob below 0.5) so a 1-tile-wide gap stays passable; this constant is the
    // nominal default the server seeds and the client mirrors. Phase 4 MUST use the IDENTICAL radius (+ dt) for the
    // determinism contract.
    public const double BodyRadius = 0.5d;
}
