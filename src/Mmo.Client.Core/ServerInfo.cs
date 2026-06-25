namespace Mmo.Client.Core;

public sealed record ServerInfo(
    string ServerName,
    byte ProtocolVersion,
    int TickRate,
    int StepCooldownMs,
    float InterestRadiusTiles,
    // CONTINUOUS MIGRATION (Phase 4, v37): the server's authoritative player body radius (tile units), replicated on
    // ServerHello so the local-player predictor collides against EXACTLY the radius the server integrates with.
    float BodyRadiusUnits)
{
    public double EffectiveStepCadenceMs => MovementCadence.EffectiveStepCadenceMs(StepCooldownMs, TickRate);
}
