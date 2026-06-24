namespace Mmo.Shared.Domain;

// LOOT P4a: an item/resource quality tier. Drives the loot-window colour + the drop-moment emphasis
// (P4c wires the colour; P4a only sets the metadata). Ordered low→high so a "best rarity in a stack"
// or colour ramp is a numeric compare. New tiers are additive enum entries, never code branches.
// No prior quality concept existed in the item system, so this is the single home for rarity.
public enum Rarity : byte
{
    Common = 0,
    Uncommon = 1,
    Rare = 2,
    Epic = 3,
    Legendary = 4
}
