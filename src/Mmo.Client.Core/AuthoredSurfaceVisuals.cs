using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// M2 (docs/town-floor1-blockout-design.md): the pure, Godot-free half of the authored-map graybox floor —
// the category→flat-albedo palette, the "does this blocked tile get a wall box?" rule, and the per-chunk
// per-category tile grouping the painter turns into MultiMeshes. Lives here (not in the Godot project) so
// the mapping and grouping logic are headlessly testable; the Godot side stays a thin "make nodes" shell.
public static class AuthoredSurfaceVisuals
{
    // Number of SurfaceCategory values — sizes the per-category material/instance arrays. Pinned by test
    // against the enum so adding a category without an albedo entry fails loudly instead of index-crashing.
    public const int CategoryCount = 5;

    // Flat unshaded albedo per surface category (graybox aesthetic, town-blockout D3): Grass green, Dirt
    // brown, Cobble warm gray, DungeonStone cold gray, Water blue. Plain floats (no Godot Color) so the
    // palette is testable headlessly; the painter wraps them once per category at build time.
    public static (float R, float G, float B) Albedo(SurfaceCategory category)
    {
        return category switch
        {
            SurfaceCategory.Grass => (0.33f, 0.49f, 0.27f),
            SurfaceCategory.Dirt => (0.52f, 0.40f, 0.26f),
            SurfaceCategory.Cobble => (0.58f, 0.55f, 0.50f),
            SurfaceCategory.DungeonStone => (0.41f, 0.44f, 0.49f),
            SurfaceCategory.Water => (0.21f, 0.42f, 0.66f),
            _ => throw new ArgumentOutOfRangeException(
                nameof(category), category, "SurfaceCategory has no authored albedo — add it to the palette."),
        };
    }

    // Whether a BLOCKED tile should render as a gray wall box. Non-authored zones (genVersion 1): always —
    // exactly the pre-M2 behavior. Authored zones carve two exceptions:
    //   * Water (`~`): blocked for movement but painted as a flat blue floor — a gray box standing on a
    //     pond reads wrong; the blue plane alone reads as "water, can't walk there" (M2 water decision).
    //   * Out-of-world padding (space): nothing exists there, so nothing is drawn at all (M1 kept these
    //     tiles as a separate list precisely so the painter can tell them from real walls).
    public static bool ShouldDrawWallBox(AuthoredMap? authored, TileCoord tile)
    {
        if (authored is null)
        {
            return true;
        }

        return !authored.IsOutOfWorld(tile) && authored.CategoryAt(tile) != SurfaceCategory.Water;
    }

    // Group one floor chunk's paintable tiles by surface category, scanning the half-open tile range
    // [x0,x1) × [y0,y1) row-major (y, then x — so each category's list is deterministically ordered).
    // Out-of-world tiles are skipped entirely (no floor there). Wall (`#`) tiles ARE included under their
    // Grass-default category: the wall box (y ≈ -0.03..0.83) fully hides the quad, and skipping them would
    // risk sliver gaps where the 0.92-wide box doesn't cover the tile edge — the handful of extra
    // instances is cheaper than the seam.
    public static List<TileCoord>[] CollectChunkTiles(AuthoredMap map, int x0, int y0, int x1, int y1)
    {
        var perCategory = new List<TileCoord>[CategoryCount];
        for (var i = 0; i < perCategory.Length; i++)
        {
            perCategory[i] = [];
        }

        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var tile = new TileCoord(x, y);
                if (map.IsOutOfWorld(tile))
                {
                    continue;
                }

                perCategory[(int)map.CategoryAt(x, y)].Add(tile);
            }
        }

        return perCategory;
    }
}
