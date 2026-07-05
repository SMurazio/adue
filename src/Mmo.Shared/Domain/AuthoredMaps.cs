namespace Mmo.Shared.Domain;

/// <summary>
/// The authored maps (town-blockout D1). Shared because the client REGENERATES the map from
/// ZoneInfo's (seed, genVersion) descriptor — shared code is the only place both sides can see, and
/// the ContentHash drift check hard-fails if they ever disagree.
///
/// Editing rules: the real map is authored as a STAMP PROGRAM (D2a, <see cref="BuildTownAndFloor1"/>)
/// — edit the ops, never emitted rows. Alphabet per <see cref="AuthoredMap"/> (D3); every walkable
/// tile must stay reachable from every `S` (the no-orphan-pockets test enforces it). ANY tile change
/// changes the genVersion 2 ContentHash — server and client must ship the same program (that is the
/// point). WARNING (M1 review F2): authored rows must only ever live as QUOTED C# strings (the
/// expander's output, or hand-quoted test grids like <see cref="AlphabetTestMap"/>) — NEVER as raw
/// multi-line strings or .txt files, because editors silently strip trailing whitespace and would
/// corrupt space-padded (out-of-world) rows without any diff you'd notice.
/// </summary>
public static class AuthoredMaps
{
    /// <summary>
    /// The genVersion 2 world dimensions. Content, not config: ServerOptions derives its world-size
    /// defaults from these so the configured dims and the authored grid can never drift (the
    /// generator hard-throws on mismatch). A test asserts the emitted grid matches these constants.
    /// </summary>
    public const int TownAndFloor1Width = 384;

    /// <summary>See <see cref="TownAndFloor1Width"/>.</summary>
    public const int TownAndFloor1Height = 384;

    /// <summary>
    /// THE genVersion 2 map (town-blockout §4): the base town, the great wall + gate, and floor 1's
    /// three wings. Expanded ONCE at static init by the deterministic stamp program below — this array
    /// is the canonical shipped artifact the parser and the ContentHash cover.
    /// Do NOT mutate the array at runtime (it is content, not state).
    /// </summary>
    public static readonly string[] TownAndFloor1 = BuildTownAndFloor1();

    /// <summary>
    /// The 12x12 M1 test grid: exercises EVERY alphabet char (walls, all four surfaces, water, a
    /// spawn anchor, all four markers, out-of-world padding) so the whole parser substrate stays under
    /// test — the real map deliberately has no dungeon stone or out-of-world padding yet, so it cannot
    /// carry that coverage. Test fixture only; no genVersion points here.
    /// </summary>
    public static readonly string[] AlphabetTestMap =
    [
        "############",
        "#..,,:S:-..#",
        "#.T,,,:,-R.#",
        "#..~~,:,-..#",
        "#..~~,:,...#",
        "#....,:,H..#",
        "#....,:,...#",
        "#..P.,:,...#",
        "#....,,,...#",
        "#..........#",
        "##########  ",
        "            ",
    ];

    /// <summary>
    /// The TownAndFloor1 stamp program (D2a). Layout brief: docs/town-floor1-blockout-design.md §4 —
    /// y grows NORTHWARD (row 0 = y 0 = the south edge), all stamp coordinates inclusive. Public so
    /// the expansion-determinism test can re-run it; every other caller reads the canonical
    /// <see cref="TownAndFloor1"/> instance.
    /// </summary>
    public static string[] BuildTownAndFloor1()
    {
        var map = new MapStamps(TownAndFloor1Width, TownAndFloor1Height, '.');

        // World border.
        map.Border(0, 0, 383, 383, 1, '#');

        // TOWN (x 172-216, y 20-56): the cobble core wrapped in a 2-wide dirt ring road.
        map.FillRect(176, 24, 212, 52, ',');
        map.FillRect(178, 26, 210, 50, ':');

        // Seven houses: 4x3 BLOCKED footprints (M1 review F4 — collision lives in the map so the
        // flood-fill reachability test sees it) with the walkable `H` sprite anchor on the tile just
        // south of each footprint. Two rows on the cobble grid, streets >= 3 tiles between footprints.
        foreach (var (hx, hy) in new[] { (180, 46), (187, 46), (194, 46), (201, 46), (180, 28), (194, 28), (201, 28) })
        {
            map.FillRect(hx, hy, hx + 3, hy + 2, '#');
            map.Put(hx + 1, hy - 1, 'H');
        }

        // Six spawn anchors ringing the plaza center (194, 38) — new players wake here (D4).
        foreach (var (sx, sy) in new[] { (193, 37), (195, 37), (192, 38), (196, 38), (193, 39), (195, 39) })
        {
            map.Put(sx, sy, 'S');
        }

        // The pinned town oak and quarry rock (D6 pins), on the grass strip south of the ring road.
        map.Put(188, 22, 'T');
        map.Put(204, 22, 'R');

        // POND west of town.
        map.FillRect(140, 30, 152, 42, '~');

        // ROAD north to the wall, 3 wide, butted against the ring road's top row (y 52) so the dirt
        // is continuous from town to gate (§4 says "from y 56"; started at y 53 to avoid a grass gap).
        map.FillRect(192, 53, 194, 109, ',');

        // GREAT WALL: full-width 3-row band, the 4-wide GATE carved back to road dirt, and the two
        // portal props flanking the approach (the future floor-2 stub).
        map.FillRect(0, 110, 383, 112, '#');
        map.FillRect(191, 110, 194, 112, ',');
        map.Put(189, 108, 'P');
        map.Put(196, 108, 'P');

        // GATE COMMONS (x 120-260, y 113-150): open grass with 2x2 rock cover clumps, paths >= 6 wide.
        foreach (var (cx, cy) in new[] { (140, 122), (158, 136), (176, 146), (210, 124), (228, 138), (246, 122) })
        {
            map.FillRect(cx, cy, cx + 1, cy + 1, '#');
        }

        // WEST WING "Slime Hollow" (x 20-140, y 120-220): five pocket arenas (1-thick rock-finger
        // borders, ~15-21 tile interiors for telegraph dodging) — A-B-C-D chained west from the
        // commons on aligned 6-wide mouths at rows y 153-158, E a south pocket; D and E also mouth
        // into the open field so no arena is a dead end. A 3x3 dirt patch anchors each center.
        map.Border(104, 148, 120, 164, 1, '#'); // A (easternmost, mouths to the commons)
        map.Border(84, 148, 100, 164, 1, '#');  // B
        map.Border(64, 144, 80, 166, 1, '#');   // C (taller for variety)
        map.Border(44, 148, 60, 164, 1, '#');   // D (westernmost)
        map.Border(84, 124, 100, 140, 1, '#');  // E (south pocket)
        map.VLine(120, 153, 158, '.');          // A east mouth -> commons
        map.VLine(104, 153, 158, '.');          // A west mouth
        map.VLine(100, 153, 158, '.');          // B east mouth
        map.VLine(84, 153, 158, '.');           // B west mouth
        map.VLine(80, 153, 158, '.');           // C east mouth
        map.VLine(64, 153, 158, '.');           // C west mouth
        map.VLine(60, 153, 158, '.');           // D east mouth
        map.HLine(148, 48, 53, '.');            // D south mouth -> open field
        map.HLine(124, 88, 93, '.');            // E south mouth -> open field
        foreach (var (px, py) in new[] { (112, 156), (92, 156), (72, 155), (52, 156), (92, 132) })
        {
            map.FillRect(px - 1, py - 1, px + 1, py + 1, ',');
        }

        // EAST WING "Gnoll Scrubland" (x 250-364, y 120-220): staggered 2-thick east-west finger
        // walls forming 8-wide skirmish lanes open at alternating ends (west mouths at x 250-269,
        // east mouths at x 341-364 — all >= 6), with 2x2 cover clumps inside the lanes.
        map.FillRect(250, 140, 340, 141, '#');
        map.FillRect(270, 150, 364, 151, '#');
        map.FillRect(250, 160, 340, 161, '#');
        map.FillRect(270, 170, 364, 171, '#');
        foreach (var (cx, cy) in new[] { (300, 145), (330, 145), (280, 155), (320, 155), (290, 165), (310, 165) })
        {
            map.FillRect(cx, cy, cx + 1, cy + 1, '#');
        }

        // NORTH PASS (x 180-210, y 220-300): stepped 2-thick walls narrowing the corridor 19 -> 15 ->
        // 11 -> 8 wide, plus the full-width cross band (butted against the step-1 walls) that makes
        // the pass the only way north; the flanks north of the band stay reachable from the Verge.
        map.FillRect(184, 220, 185, 239, '#');
        map.FillRect(205, 220, 206, 239, '#');
        map.FillRect(186, 240, 187, 259, '#');
        map.FillRect(203, 240, 204, 259, '#');
        map.FillRect(188, 260, 189, 279, '#');
        map.FillRect(201, 260, 202, 279, '#');
        map.FillRect(189, 280, 190, 300, '#');
        map.FillRect(199, 280, 200, 300, '#');
        map.FillRect(0, 228, 185, 230, '#');
        map.FillRect(205, 228, 383, 230, '#');

        // THE VERGE (x 100-300, y 300-370): the tarn, the map's ONLY dead-end pockets (narrow 4-wide
        // mouths — deliberate; everywhere else mouths are >= 6), and scattered rock clumps.
        map.FillRect(190, 330, 210, 342, '~');
        map.Border(102, 308, 116, 322, 1, '#');
        map.VLine(116, 313, 316, '.');          // west-edge pocket, mouth east
        map.Border(284, 312, 298, 326, 1, '#');
        map.VLine(284, 317, 320, '.');          // east-edge pocket, mouth west
        map.Border(180, 354, 194, 368, 1, '#');
        map.HLine(354, 185, 188, '.');          // north pocket, mouth south
        map.Border(244, 352, 258, 366, 1, '#');
        map.HLine(352, 249, 252, '.');          // north-east pocket, mouth south
        foreach (var (cx, cy) in new[] { (130, 315), (155, 342), (170, 310), (215, 310), (225, 320), (255, 335), (140, 360), (270, 357) })
        {
            map.FillRect(cx, cy, cx + 1, cy + 1, '#');
        }

        // BOSS-1 (docs/boss-encounter-sunderer-design.md): THE SUNDERER ARENA — a 24x24 sealed room in the far
        // north-east corner (open grass today; north of the wings/cross-band, east of the Verge, diagonally opposite
        // town). A 1-tile wall ring (authored-stamp collision, predicted on both sides) around a 22x22 DungeonStone
        // floor. The floor is '-' (a NON-grass surface) ON PURPOSE: the node scatter classes are all Grass-only, so
        // authoring the interior as dungeon stone masks every resource node out of the arena for free (no explicit
        // exclusion rect) — flat, empty floor. The room is DELIBERATELY unconnected to the rest of the map (no mouth):
        // players only ever /boss-teleport in, so the reachability invariant carves this interior out (a sealed pocket
        // by design, pinned in TownAndFloor1MapTests). Coords live in BossArena so the server engine + this stamp + the
        // test can never drift. This edit moves the genVersion 2 ContentHash (re-pinned in the same commit) and — via
        // the masked-out interior tiles — the NodeCatalog CatalogHash (likewise re-pinned).
        map.Border(BossArena.ExteriorMinX, BossArena.ExteriorMinY, BossArena.ExteriorMaxX, BossArena.ExteriorMaxY, 1, '#');
        map.FillRect(BossArena.InteriorMinX, BossArena.InteriorMinY, BossArena.InteriorMaxX, BossArena.InteriorMaxY, '-');

        return map.Emit();
    }
}
