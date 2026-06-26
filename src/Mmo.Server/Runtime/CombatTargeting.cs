using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

// COMBAT: the two server-authoritative target-eligibility gates, in one neutral home shared by the combat
// resolver (FreeAimSectorResolver) and the regen loop (GameServer.RegenEnemies). Relocated VERBATIM from the
// retired MeleeConeResolver (Phase 7, continuous-combat migration) when the dead tile-fan melee was deleted —
// the friendly-fire and regen logic is unchanged; only its home moved off the dead resolver.
public static class CombatTargeting
{
    // An attack damages enemies only — the target dummy, NPCs, and (LIVING-ENEMIES P1) roaming Monsters. Other
    // Players are never damaged (no friendly fire this stage); resource nodes / stat-less entities are not
    // combatants. The single friendly-fire gate, so a future PvP toggle changes only here.
    public static bool IsAttackableEnemy(WorldEntity entity)
    {
        return entity.Kind is EntityKind.Dummy or EntityKind.Npc or EntityKind.Monster;
    }

    // LIVING-ENEMIES P1: which enemies HEAL BACK via the heavy HP-regen loop (RegenEnemies). The stationary test
    // targets (Dummy/Npc) regen so they stay permanent practice dummies; a roaming Monster does NOT — its HP just
    // depletes and stays (death/respawn is a later phase). DISTINCT from IsAttackableEnemy on purpose: a Monster
    // is attackable but not self-healing. Adding Monster to IsAttackableEnemy must NOT make it regen, so the regen
    // loop gates on this narrower set.
    public static bool IsRegeneratingEnemy(WorldEntity entity)
    {
        return entity.Kind is EntityKind.Dummy or EntityKind.Npc;
    }
}
