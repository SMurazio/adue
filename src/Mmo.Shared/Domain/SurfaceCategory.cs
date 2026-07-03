namespace Mmo.Shared.Domain;

/// <summary>
/// Per-tile floor SURFACE category of an authored map (town-blockout design D1/D3). Purely visual on
/// the client (each category maps to one flat albedo color in the terrain painter) and semantic for
/// content rules (e.g. D6: resource scatter only ever lands on <see cref="Grass"/>). Walkability is
/// NOT derived from the category — the blocked set is separate — with one authored coupling: water
/// (`~`) tiles are always blocked (visual variety, no swim tech).
///
/// Byte-backed and part of the terrain ContentHash determinism contract: each tile's category byte is
/// hashed row-major into the authored-layout hash (see <see cref="TerrainGenerator.ContentHash(AuthoredMap)"/>),
/// so client and server drift on categories hard-fails exactly like drift on walls. NEVER reorder or
/// renumber existing values — only append — or every shipped authored map's hash silently moves.
/// </summary>
public enum SurfaceCategory : byte
{
    /// <summary>Open grass (`.`) — the default everywhere on non-authored (genVersion 1) maps.</summary>
    Grass = 0,

    /// <summary>Dirt / road (`,`).</summary>
    Dirt = 1,

    /// <summary>Town cobble (`:`, and under `S` spawn anchors).</summary>
    Cobble = 2,

    /// <summary>Dungeon stone (`-`) — the tower-floor interior surface.</summary>
    DungeonStone = 3,

    /// <summary>Water (`~`) — always blocked; blue visual anchor, no swim mechanics.</summary>
    Water = 4,
}
