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
    // a hittable target, but it is NOT stationary — server AI (IMonsterBehavior) idles it near a home anchor and
    // occasionally moves it a short distance within a leash radius via the SAME continuous-movement path players use, so the
    // client renders + interpolates it for free as a remote entity (no client-side AI). Distinct kind so the
    // roam AI / future aggro target this kind specifically, and so it is NOT swept up by the Dummy HP-regen.
    // Renders on the client through the existing Box archetype fallback (non-Player/Resource → Box).
    //
    // Wire note: EntityKind rides the spawn message as a single byte, so adding a value is wire-compatible —
    // no protocol VERSION bump (client + server ship together). The codec validates the byte range on decode.
    Monster = 5,

    // LOOT P4b: a dropped CORPSE left on the death tile of a killed monster. A replicated transient world entity
    // (so it AOI-replicates + renders + interacts through the SAME paths players/monsters/resources use — no new
    // replication fork). It holds the rolled loot, the eligible-looter set, a loot-mode, and a decay deadline
    // SERVER-SIDE only (the client never receives the contents this phase — P4c adds the loot-window replication).
    // It is stationary, non-attackable (NOT swept up by combat — IsAttackableEnemy excludes it), and despawns on
    // loot-all OR decay. Renders on the client through the existing Box archetype fallback (non-Player/Resource →
    // Box) like the Dummy/Monster, so no new visual archetype is strictly required (a distinct "Corpse" archetype
    // can be added later for art).
    //
    // Wire note: same as Monster — a new EntityKind byte is wire-compatible (client + server ship together), so no
    // protocol VERSION bump. The spawn message already carries Kind as a byte.
    Corpse = 6,

    // DUO-SKILLSHOT (exp/duo-abilities): a server-simulated transient PROJECTILE — a fusion-skillshot shot in flight.
    // A lightweight WorldEntity so it AOI-replicates + interpolates through the SAME snapshot path players/monsters use
    // (projectiles MOVE — exactly what entities are for). The SkillshotEngine owns its flight (straight-line, tick-
    // stepped), its monster-hit resolution (reusing the melee's damage/contribution/death seam), and its fusion merge.
    // It is NON-combatant (zeroed vitals so no overhead HP bar) and is NEVER a collision obstacle nor an attackable
    // enemy (CombatTargeting excludes it). Its tier (solo/good/perfect) rides the EXISTING replicated TintRgb+ScaleMilli
    // so it renders coloured/sized with no new wire field. Renders client-side via a dedicated bright Projectile visual.
    Projectile = 7,
}
