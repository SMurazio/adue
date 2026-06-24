namespace Mmo.Shared.Domain;

// LOOT P4b: how a corpse's loot is distributed among its eligible looters. The eligibility DATA (who earned the
// kill — the contribution ledger's set) is decoupled from the MODE that interprets it, so fair-group-loot variants
// later are a new mode over the SAME eligibility set, not a rewrite (the design's "config change, not a rewrite").
//
// Only FfaAmongEligible is implemented in P4b (the default): any eligible looter may take everything (solo = the
// killer alone, so it behaves exactly like personal loot for a solo kill). The remaining values are RESERVED seams
// the corpse already carries on its tag so the later modes are additive:
//   - RoundRobin  — eligible looters take turns owning the next item.
//   - Personal    — each eligible looter sees their OWN instanced roll (the modern fair default; needs a per-player
//                   roll at the single roll-site, already isolated in KillMonster).
//   - NeedGreed / MasterLooter — classic group-loot ceremonies.
// Lives in Mmo.Shared so a future loot-window (P4c) can name the mode on the wire without a server type leak.
public enum LootMode : byte
{
    FfaAmongEligible = 0,
    RoundRobin = 1,
    Personal = 2,
    NeedGreed = 3,
    MasterLooter = 4
}
