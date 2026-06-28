namespace Mmo.Shared.Domain;

// COMBAT-TUNING (live, protocol v31): the server-authoritative combat feel-knobs, replicated to each client so the
// client's wedge mesh, swing-root prediction, and radial cooldown indicator all derive from the SAME values the
// server resolves with. Previously these lived as duplicated constants on BOTH sides (GameServer +
// FreeAimSectorResolver + MmoClientRoot), which could silently drift; now the server owns them (ServerTuning, live
// via the admin tuning registry) and ships THIS snapshot on login + on every change. The client mirrors it and
// rebuilds anything derived (the wedge mesh, the predictor's root ticks, the cooldown duration) when it changes.
//
// Units: AttackCooldownMs and RootMs are milliseconds; HalfAngleDegrees is the sector HALF-angle in degrees (full
// arc = 2x); RadiusUnits is the sector reach in world units; Damage is HP per enemy hit. These mirror the registry
// keys combat.attackCooldownMs / combat.rootMs / combat.halfAngleDeg / combat.radiusTiles / combat.damage.
public readonly record struct CombatTuningSnapshot(
    int AttackCooldownMs,
    int RootMs,
    double HalfAngleDegrees,
    double RadiusUnits,
    int Damage)
{
    public double HalfAngleRadians => HalfAngleDegrees * System.Math.PI / 180d;
}
