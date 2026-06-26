using Mmo.Shared.Domain;

namespace Mmo.Server.Data;

// CONTINUOUS MIGRATION (Phase 10): a loaded character now carries its CONTINUOUS WorldVector position (the true
// sub-tile spot persisted in pos_x/pos_y), not just the rounded tile. Tile is a DERIVED accessor (the nearest
// tile centre) so every existing `.Tile` read site (spawn-tile resolution, takeover, tile-keyed tests) keeps
// working unchanged while login can now restore the exact off-grid Position.
public sealed record CharacterRecord(
    Guid AccountId,
    Guid CharacterId,
    string DisplayName,
    string ZoneId,
    WorldVector Position)
{
    public TileCoord Tile => Position.ToTileRounded();
}
