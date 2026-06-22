namespace Mmo.Shared.Domain;

// COMBAT-TUNING (live, protocol v31): the server-authoritative combat feel-knobs, replicated to each client so the
// client's wedge mesh, swing-root prediction, and radial cooldown indicator all derive from the SAME values the
// server resolves with. Previously these lived as duplicated constants on BOTH sides (GameServer +
// FreeAimSectorResolver + MmoClientRoot), which could silently drift; now the server owns them (ServerTuning, live
// via the admin tuning registry) and ships THIS snapshot on login + on every change. The client mirrors it and
// rebuilds anything derived (the wedge mesh, the predictor's root ticks, the cooldown duration) when it changes.
//
// Units: AttackCooldownMs and RootMs are milliseconds; HalfAngleDegrees is the sector HALF-angle in degrees (full
// arc = 2x); RadiusTiles is the sector reach in tiles; Damage is HP per enemy hit. These mirror the registry keys
// combat.attackCooldownMs / combat.rootMs / combat.halfAngleDeg / combat.radiusTiles / combat.damage.
//
// SWING-SLOW (protocol v32): RootMs is now the swing-slow WINDOW DURATION (how long the slow lasts) and the new
// SwingMoveFactor in [0,1] is how HARD the slow is within that window — 0 = full stop (the old root), 1 = no slow,
// 0.4 (default) = move at 40% speed. Both ride the snapshot so the client predictor slows its movement by the SAME
// factor over the SAME window the server does (the swing-slow parity point). SwingMoveFactor mirrors the registry
// key combat.swingMoveFactor.
public readonly record struct CombatTuningSnapshot(
    int AttackCooldownMs,
    int RootMs,
    double HalfAngleDegrees,
    double RadiusTiles,
    int Damage,
    double SwingMoveFactor)
{
    public double HalfAngleRadians => HalfAngleDegrees * System.Math.PI / 180d;
}
