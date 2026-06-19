using Mmo.Shared.Domain;

namespace Mmo.Server.Data;

public interface ICharacterRepository
{
    Task<CharacterRecord> LoadOrCreateAsync(
        string accountName,
        string displayName,
        CancellationToken cancellationToken);

    Task SaveTileAsync(Guid characterId, TileCoord tile, CancellationToken cancellationToken);

    // Loads a character's persisted item stacks (one ItemStack per character_items row). Returns an
    // empty list for a character with no items.
    Task<IReadOnlyList<ItemStack>> LoadItemsAsync(Guid characterId, CancellationToken cancellationToken);

    // Write-behind upsert of changed stacks: rows with Quantity > 0 are upserted, rows with
    // Quantity <= 0 are deleted. Applied in a single transaction.
    Task SaveItemsAsync(Guid characterId, IReadOnlyList<ItemStack> changes, CancellationToken cancellationToken);
}
