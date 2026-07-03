namespace Mmo.Shared.Domain;

/// <summary>
/// The authored ASCII maps (town-blockout D1). Shared because the client REGENERATES the map from
/// ZoneInfo's (seed, genVersion) descriptor — shared code is the only place both sides can see, and
/// the ContentHash drift check hard-fails if they ever disagree.
///
/// Editing rules: one char per tile, alphabet per <see cref="AuthoredMap"/> (D3), every row the same
/// length (pad with spaces), every walkable tile reachable from every `S` (the no-orphan-pockets
/// test enforces it). ANY char change changes the genVersion 2 ContentHash — server and client must
/// ship the same rows (that is the point).
/// </summary>
public static class AuthoredMaps
{
    /// <summary>
    /// The genVersion 2 map. M1 ships a PLACEHOLDER 12x12 grid that exercises every alphabet char
    /// (walls, all four surfaces, water, a spawn anchor, all four markers, out-of-world padding) so
    /// the whole substrate is under test; M3 replaces these rows with the real 192x192 town+floor-1
    /// layout (town-blockout §4) — same parser, same hash contract, nothing else changes.
    /// Do NOT mutate the array at runtime (it is content, not state).
    /// </summary>
    public static readonly string[] TownAndFloor1 =
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
}
