namespace Mmo.Shared.Domain;

// COMBAT-S2B: which attack the client requested. Only the instant-resolve melee "shotgun" cone exists this
// stage (docs/combat-design.md). It rides the wire as a byte on AttackMessage; the codec validates the range
// on decode. Future attacks (the travelling arrow, etc.) extend this enum — the cone math is selected by kind.
public enum AttackKind : byte
{
    MeleeCone = 0,
}
