namespace Mmo.Shared.Domain;

// ADUE P2-A (todo/S-p2-practice-room-and-dummy.md, docs/duo-p2-demo-plan.md): THE PRACTICE ROOM — a second sealed,
// teleport-only authored pocket (the BossArena's twin) where a pair rehearses the four duo verbs against a
// non-aggressive dummy BEFORE the real run. Like the arena, its geometry is the single source of truth SHARED so the
// three call sites can never drift:
//   * AuthoredMaps.BuildTownAndFloor1 STAMPS the walls + floor from these coords (both client and server regenerate
//     the same map, so the wall ring is a shared-collision obstacle both sides predict for free);
//   * the server's /practice command reads the entry tiles / dummy-spawn tile (teleport targets + the dummy spawn) and
//     the ContainsInterior test (is this player inside the room? → enter-vs-leave, and the dummy-lifetime occupancy);
//   * the map-reachability TEST carves the interior out of the no-orphan-pockets invariant (a DELIBERATELY sealed
//     pocket — players only ever /practice-teleport in, never walk in), exactly as the arena is carved out.
//
// A far NORTH-WEST corner of the 384x384 map: open grass today (west of the Verge which starts x 100, far north of the
// west wing which ends y 220), diagonally opposite the north-EAST Sunderer arena and well away from the south-centre
// town. 24x24 exterior, a 1-tile wall ring, a 22x22 walkable interior authored as DungeonStone ('-'), a NON-grass
// surface that masks the (Grass-only) node scatter out of the room for free — flat, empty floor, no explicit
// exclusion-rect needed (the same trick the arena uses; it likewise moves the ContentHash + NodeCatalog CatalogHash).
public static class PracticeRoom
{
    // Exterior rect (inclusive) of the 24x24 room — the outer edge of the 1-tile wall ring.
    public const int ExteriorMinX = 8;
    public const int ExteriorMinY = 352;
    public const int ExteriorMaxX = 31;
    public const int ExteriorMaxY = 375;

    // Interior rect (inclusive) of the 22x22 walkable floor — the exterior inset by the 1-tile wall ring. The
    // /practice "inside the room" test and the reachability carve-out both use this.
    public const int InteriorMinX = ExteriorMinX + 1; // 9
    public const int InteriorMinY = ExteriorMinY + 1; // 353
    public const int InteriorMaxX = ExteriorMaxX - 1; // 30
    public const int InteriorMaxY = ExteriorMaxY - 1; // 374

    // Fixed entry tiles: the issuer lands on ONE, their duo partner (when present) on the OTHER — near the south
    // interior edge, two tiles apart so a pair starts together. Both are interior floor tiles. (Mirrors BossArena.)
    public static readonly TileCoord IssuerEntryTile = new(18, 356);
    public static readonly TileCoord PartnerEntryTile = new(20, 356);

    // The dummy spawns here — near the interior centre (interior centre ≈ (19.5, 363.5)), ~10 tiles north of the entry
    // tiles so the pair rehearses crossing projectiles / midpoint / tether against a fixed target a comfortable throw away.
    public static readonly TileCoord DummySpawnTile = new(19, 366);

    // True iff a tile is inside the walkable interior (NOT the wall ring). The load-bearing membership test: it decides
    // whether a /practice issuer is inside (→ leave) or outside (→ enter). Mirrors BossArena.ContainsInterior.
    public static bool ContainsInterior(TileCoord tile) =>
        tile.X >= InteriorMinX && tile.X <= InteriorMaxX &&
        tile.Y >= InteriorMinY && tile.Y <= InteriorMaxY;
}
