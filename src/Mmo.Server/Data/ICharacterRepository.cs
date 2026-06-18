using Mmo.Shared.Domain;

namespace Mmo.Server.Data;

public interface ICharacterRepository
{
    Task<CharacterRecord> LoadOrCreateAsync(
        string accountName,
        string displayName,
        CancellationToken cancellationToken);

    Task SaveTileAsync(Guid characterId, TileCoord tile, CancellationToken cancellationToken);
}
