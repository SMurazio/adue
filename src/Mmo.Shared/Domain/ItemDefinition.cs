namespace Mmo.Shared.Domain;

// Static catalog entry for an item type. Immutable and shared; resolved from a stable string Key.
// The Key is the serialization-friendly identity that persists in the DB and (later) rides the wire;
// it never changes once an item ships. Adding an item is a registry entry, not new code branches.
public sealed record ItemDefinition(
    string Key,
    string DisplayName,
    int MaxStack,
    ItemCategory Category)
{
    public bool IsStackable => MaxStack > 1;
}
