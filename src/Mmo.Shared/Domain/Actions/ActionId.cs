namespace Mmo.Shared.Domain.Actions;

// MOVEMENT-ACTIONS (Phase A): the stable registry key / wire byte for a movement-action definition. Each value is
// PINNED (it is the byte Phase B puts on the wire and the registry's dictionary key), so values must never be
// renumbered once they ship. None=0 is the "no action" sentinel (an entity not currently in an action); the seed
// set adds Jump=1. Charge/DodgeRoll were RESERVED from the start and shipped in Phase D (the pre-pinned bytes meant
// NO protocol change was needed). Mirrors the AttackKind/EntityKind byte-enum convention used elsewhere in Mmo.Shared.
public enum ActionId : byte
{
    None = 0,
    Jump = 1,

    // Phase D (player defs in the shared registry; the gnoll's monster charge reuses the same Charge byte with a
    // per-instance def). Bytes were pinned in Phase A, so shipping them changed no wire format.
    Charge = 2,
    DodgeRoll = 3,
}
