namespace Mmo.Client.Core;

public sealed record ServerInfo(
    string ServerName,
    byte ProtocolVersion,
    int TickRate,
    int StepCooldownMs,
    float InterestRadiusTiles)
{
    public double EffectiveStepCadenceMs => MovementCadence.EffectiveStepCadenceMs(StepCooldownMs, TickRate);
}
