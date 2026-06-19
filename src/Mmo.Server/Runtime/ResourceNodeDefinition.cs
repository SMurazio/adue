namespace Mmo.Server.Runtime;

// Static catalog entry for a resource-node type: what a single harvest yields (an item template key
// plus a quantity) and how many server ticks until a depleted node returns to Available. Immutable and
// shared; mirrors the item definition/registry split so adding a node type is a registry entry, not new
// code. Key is the stable type identity (e.g. "tree", "rock", "plant"). Validation runs at construction
// via the property initializers, so a malformed definition fails fast at startup.
public sealed record ResourceNodeDefinition(
    string Key,
    string DisplayName,
    string YieldItemKey,
    int YieldQuantity,
    uint RespawnTicks)
{
    public string Key { get; } = string.IsNullOrWhiteSpace(Key)
        ? throw new ArgumentException("Resource node definitions must have a non-empty key.", nameof(Key))
        : Key;

    public string YieldItemKey { get; } = string.IsNullOrWhiteSpace(YieldItemKey)
        ? throw new ArgumentException("Resource node must yield a non-empty item key.", nameof(YieldItemKey))
        : YieldItemKey;

    public int YieldQuantity { get; } = YieldQuantity >= 1
        ? YieldQuantity
        : throw new ArgumentException("Resource node must yield a positive quantity.", nameof(YieldQuantity));

    public uint RespawnTicks { get; } = RespawnTicks >= 1
        ? RespawnTicks
        : throw new ArgumentException("Resource node must have a positive respawn time.", nameof(RespawnTicks));
}
