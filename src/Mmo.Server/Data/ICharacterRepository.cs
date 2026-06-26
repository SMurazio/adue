using Mmo.Shared.Domain;

namespace Mmo.Server.Data;

public interface ICharacterRepository
{
    Task<CharacterRecord> LoadOrCreateAsync(
        string accountName,
        string displayName,
        CancellationToken cancellationToken);

    // CONTINUOUS MIGRATION (Phase 10): write-behind save of the character's CONTINUOUS position. Persists the
    // float pos_x/pos_y (the sub-tile spot) and the derived rounded tile_x/tile_y together, so login restores the
    // exact off-grid position while any tile-keyed query still sees a coherent tile.
    Task SavePositionAsync(Guid characterId, WorldVector position, CancellationToken cancellationToken);

    // Loads a character's persisted item stacks (one ItemStack per character_items row). Returns an
    // empty list for a character with no items.
    Task<IReadOnlyList<ItemStack>> LoadItemsAsync(Guid characterId, CancellationToken cancellationToken);

    // Write-behind upsert of changed stacks: rows with Quantity > 0 are upserted, rows with
    // Quantity <= 0 are deleted. Applied in a single transaction.
    Task SaveItemsAsync(Guid characterId, IReadOnlyList<ItemStack> changes, CancellationToken cancellationToken);
}
