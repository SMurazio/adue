namespace Mmo.Shared.Domain;

// Coarse classification for items. Kept tiny on purpose: gather/craft only needs Resource for now.
// New categories are additive enum entries, never code branches in the inventory path.
public enum ItemCategory : byte
{
    Resource = 1
}
