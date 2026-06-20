using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

public readonly record struct MovementStepResult(
    Direction8 Direction,
    TileCoord From,
    TileCoord Target,
    bool CooldownElapsed,
    bool TargetWalkable,
    bool Accepted,
    string Reason,
    TileCoord Result);
