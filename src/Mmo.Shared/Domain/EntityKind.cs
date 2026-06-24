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

    // LIVING-ENEMIES P1: a server-driven roaming monster. Like the Dummy it carries CharacterStats (HP) and is
    // a hittable target, but it is NOT stationary — server AI (MonsterRoamAi) idles it near a home anchor and
    // occasionally walks it a few tiles within a leash radius via the SAME tile-step path players use, so the
    // client renders + interpolates it for free as a remote entity (no client-side AI). Distinct kind so the
    // roam AI / future aggro target this kind specifically, and so it is NOT swept up by the Dummy HP-regen.
    // Renders on the client through the existing Box archetype fallback (non-Player/Resource → Box).
    //
    // Wire note: EntityKind rides the spawn message as a single byte, so adding a value is wire-compatible —
    // no protocol VERSION bump (client + server ship together). The codec validates the byte range on decode.
    Monster = 5,
}
