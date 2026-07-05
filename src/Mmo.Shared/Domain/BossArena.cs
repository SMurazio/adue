namespace Mmo.Shared.Domain;

// BOSS-1 (docs/boss-encounter-sunderer-design.md, "The room"): the authored Sunderer arena — a single source of
// truth for its geometry, SHARED so three call sites can never drift:
//   * AuthoredMaps.BuildTownAndFloor1 STAMPS the walls + floor from these coords (both client and server regenerate
//     the same map, so the wall ring is a shared-collision obstacle both sides predict for free);
//   * the server's BossEncounterEngine reads the entry tiles / boss-spawn centre / interior rect (teleport targets +
//     the "is this player inside the arena?" test that decides /boss enter-vs-leave and the empty-arena reset);
//   * the map-reachability TEST carves the interior out of the no-orphan-pockets invariant (the arena is
//     DELIBERATELY a sealed pocket — players only ever teleport in, never walk in).
//
// A far NORTH-EAST corner of the 384x384 map (§4 recon: north of the two wings/cross-band at y 220-230, east of the
// Verge which ends x 300 — open grass today, diagonally opposite the south-centre town). 24x24 exterior, a 1-tile
// wall ring, a 22x22 walkable interior. The interior is authored as DungeonStone ('-'), a non-Grass surface, which
// masks the node scatter out for free (the WeightedScatter classes are all Grass-only — no explicit exclusion-rect
// needed; the category gate does it, and it still moves the NodeCatalog CatalogHash as the pins expect).
public static class BossArena
{
    // Exterior rect (inclusive) of the 24x24 room — the outer edge of the 1-tile wall ring.
    public const int ExteriorMinX = 356;
    public const int ExteriorMinY = 356;
    public const int ExteriorMaxX = 379;
    public const int ExteriorMaxY = 379;

    // Interior rect (inclusive) of the 22x22 walkable floor — the exterior inset by the 1-tile wall ring. The
    // engine's "inside the arena" test and the reachability carve-out both use this.
    public const int InteriorMinX = ExteriorMinX + 1; // 357
    public const int InteriorMinY = ExteriorMinY + 1; // 357
    public const int InteriorMaxX = ExteriorMaxX - 1; // 378
    public const int InteriorMaxY = ExteriorMaxY - 1; // 378

    // Fixed entry tiles: the issuer lands on ONE, their duo partner (when present) on the OTHER — near the south
    // interior edge, a couple of tiles apart so a pair starts together. Both are interior floor tiles.
    public static readonly TileCoord IssuerEntryTile = new(367, 361);
    public static readonly TileCoord PartnerEntryTile = new(369, 361);

    // The boss spawns here — near the interior centre, ~10 tiles north of the entry tiles (inside the design's 8-12u
    // "sweet band"; the interior max diagonal ≈ 29.7u keeps overstretch reachable per the tether-geometry note).
    public static readonly TileCoord BossSpawnTile = new(368, 371);

    // True iff a tile is inside the walkable interior (NOT the wall ring). The load-bearing membership test: it
    // decides whether a /boss issuer is inside (→ leave) or outside (→ enter), and whether a participant is still
    // present in the arena (walked out / respawned-to-town → no longer present).
    public static bool ContainsInterior(TileCoord tile) =>
        tile.X >= InteriorMinX && tile.X <= InteriorMaxX &&
        tile.Y >= InteriorMinY && tile.Y <= InteriorMaxY;
}
