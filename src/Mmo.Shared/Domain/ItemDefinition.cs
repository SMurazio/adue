namespace Mmo.Shared.Domain;

// Static catalog entry for an item type. Immutable and shared; resolved from a stable string Key.
// The Key is the serialization-friendly identity that persists in the DB and (later) rides the wire;
// it never changes once an item ships. Adding an item is a registry entry, not new code branches.
//
// LOOT P4a: Rarity is a trailing optional parameter (default Common) so every existing positional
// construction of (Key, DisplayName, MaxStack, Category) keeps compiling unchanged. It is metadata
// now — the loot window (P4c) reads it for colour; nothing branches on it server-side yet.
public sealed record ItemDefinition(
    string Key,
    string DisplayName,
    int MaxStack,
    ItemCategory Category,
    Rarity Rarity = Rarity.Common)
{
    public bool IsStackable => MaxStack > 1;
}
