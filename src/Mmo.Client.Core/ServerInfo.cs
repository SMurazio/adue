namespace Mmo.Client.Core;

public sealed record ServerInfo(
    string ServerName,
    byte ProtocolVersion,
    int TickRate,
    int StepCooldownMs,
    int TurnDelayMs,
    float InterestRadiusTiles)
{
    public double EffectiveStepCadenceMs => MovementCadence.EffectiveStepCadenceMs(StepCooldownMs, TickRate);

    // S63: the turn delay tick-quantised the SAME way as the step cadence so the predictor's turn cost rounds
    // to the identical tick count the server uses (no parity drift). Advertised in ServerHello (v18).
    public double EffectiveTurnDelayMs => MovementCadence.EffectiveTurnDelayMs(TurnDelayMs, TickRate);
}
