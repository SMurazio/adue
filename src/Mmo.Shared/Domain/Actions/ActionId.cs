namespace Mmo.Shared.Domain.Actions;

// MOVEMENT-ACTIONS (Phase A): the stable registry key / wire byte for a movement-action definition. Each value is
// PINNED (it is the byte Phase B puts on the wire and the registry's dictionary key), so values must never be
// renumbered once they ship. None=0 is the "no action" sentinel (an entity not currently in an action); the seed
// set adds Jump=1. Charge/DodgeRoll are RESERVED here (Phase D) so their bytes are fixed up front and never collide
// with a future addition. Mirrors the AttackKind/EntityKind byte-enum convention used elsewhere in Mmo.Shared.
public enum ActionId : byte
{
    None = 0,
    Jump = 1,

    // Reserved for Phase D (not implemented in Phase A) — pinned so the wire bytes are stable from the start.
    Charge = 2,
    DodgeRoll = 3,
}
