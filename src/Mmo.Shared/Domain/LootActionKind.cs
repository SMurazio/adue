namespace Mmo.Shared.Domain;

// LOOT P4c: which loot-window action the client is requesting against an OPEN corpse. TakeItem takes the one stack
// named by the LootActionMessage.TemplateKey; LootAll takes everything that fits; Close releases the window (the
// server drops the open-loot pairing). Opening is NOT here — it reuses InteractRequest on a corpse. New verbs are
// additive enum entries, never new message types.
public enum LootActionKind : byte
{
    TakeItem = 0,
    LootAll = 1,
    Close = 2
}
