namespace Mmo.Shared.Domain;

// LOOT P4c: one row of an OPEN corpse's contents as shipped to the loot window — the stack's template key, how many,
// and its rarity tier (for the window's rarity colour). DisplayName is resolved on the client against its
// ItemRegistry (falling back to the raw key), so only the stable key + quantity + rarity ride the wire — the same
// thin shape InventoryUpdate uses, plus the rarity byte the window needs to colour the row.
public readonly record struct CorpseItem(string TemplateKey, int Quantity, Rarity Rarity);
