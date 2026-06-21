namespace Mmo.Shared.Domain;

public enum EntityKind : byte
{
    Player = 1,
    Npc = 2,
    Resource = 3,

    // COMBAT-S2A: a stationary "target dummy" — a server-spawned enemy with CharacterStats (HP) and no
    // behaviour, used as a hittable target for the combat stages. Distinct from Npc so the dummy is an
    // unambiguous, future-attackable kind (Stage 2b's melee cone resolves damage against it). Renders on
    // the client through the existing Box archetype fallback (the factory's default for non-Player/Resource
    // kinds), so no new visual archetype is needed this stage.
    Dummy = 4,
}
