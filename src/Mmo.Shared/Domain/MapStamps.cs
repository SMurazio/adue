namespace Mmo.Shared.Domain;

/// <summary>
/// The authored-map STAMP EXPANDER (town-blockout D2a): a tiny ordered op set — fill / border / line /
/// put of alphabet chars onto a char canvas — deterministically expanded into the same quoted
/// <c>string[]</c> rows the <see cref="AuthoredMap"/> parser consumes. Hand-writing 384 raw 384-char
/// rows is where character-level authoring stops being reliable (for humans and models both); a stamp
/// program makes a layout edit "widen the west arena by 4" — one number in one op — instead of surgery
/// across 40 text rows. The ContentHash covers the EXPANDED grid, so the drift guard is unchanged:
/// stamps → ASCII → parser, and the M1 parser and its tests are untouched.
///
/// Determinism contract (same as <see cref="TerrainGenerator"/> / <see cref="AuthoredMap"/>): every op
/// is a pure array write applied in call order — no RNG, no clocks, no culture-sensitive APIs — so the
/// same program emits byte-identical rows on every platform/runtime (asserted by test). Coordinates
/// are INCLUSIVE on both ends (matching how the layout brief specifies rects) and must lie inside the
/// canvas — an out-of-bounds or inverted stamp THROWS, because a silently clipped stamp is an authored
/// layout that quietly differs from its program. Chars are NOT validated here; the parser is the
/// alphabet gate (an off-alphabet stamp fails loudly at the parse, boot/test time).
/// </summary>
public sealed class MapStamps
{
    private readonly char[] _canvas; // Row-major: index = y * Width + x — the parser's own layout.
    private readonly int _width;
    private readonly int _height;

    /// <summary>Creates a canvas of <paramref name="width"/>×<paramref name="height"/> seeded with <paramref name="fill"/>.</summary>
    public MapStamps(int width, int height, char fill)
    {
        if (width < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        }

        if (height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        }

        _width = width;
        _height = height;
        _canvas = new char[width * height];
        Array.Fill(_canvas, fill);
    }

    /// <summary>Stamps every tile of the inclusive rect (x0, y0)-(x1, y1) with <paramref name="c"/>.</summary>
    public MapStamps FillRect(int x0, int y0, int x1, int y1, char c)
    {
        ValidateRect(x0, y0, x1, y1);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                _canvas[(y * _width) + x] = c;
            }
        }

        return this;
    }

    /// <summary>
    /// Stamps a <paramref name="thickness"/>-tile-thick border ring just inside the inclusive rect
    /// (x0, y0)-(x1, y1) — four overlapping edge bands; the interior is left untouched.
    /// </summary>
    public MapStamps Border(int x0, int y0, int x1, int y1, int thickness, char c)
    {
        ValidateRect(x0, y0, x1, y1);
        if (thickness < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(thickness), "Border thickness must be positive.");
        }

        FillRect(x0, y0, x1, Math.Min(y1, y0 + thickness - 1), c); // south band (low y)
        FillRect(x0, Math.Max(y0, y1 - thickness + 1), x1, y1, c); // north band (high y)
        FillRect(x0, y0, Math.Min(x1, x0 + thickness - 1), y1, c); // west band
        FillRect(Math.Max(x0, x1 - thickness + 1), y0, x1, y1, c); // east band
        return this;
    }

    /// <summary>Stamps the horizontal run (x0..x1 inclusive) on row <paramref name="y"/>.</summary>
    public MapStamps HLine(int y, int x0, int x1, char c)
    {
        return FillRect(x0, y, x1, y, c);
    }

    /// <summary>Stamps the vertical run (y0..y1 inclusive) on column <paramref name="x"/>.</summary>
    public MapStamps VLine(int x, int y0, int y1, char c)
    {
        return FillRect(x, y0, x, y1, c);
    }

    /// <summary>Stamps a single tile.</summary>
    public MapStamps Put(int x, int y, char c)
    {
        return FillRect(x, y, x, y, c);
    }

    /// <summary>
    /// Emits the canvas as the parser's row form: one string per row, rows[y][x] = the tile char —
    /// exactly the <see cref="AuthoredMap.Parse"/> input shape (row 0 = y 0 = the SOUTH edge).
    /// </summary>
    public string[] Emit()
    {
        var rows = new string[_height];
        for (var y = 0; y < _height; y++)
        {
            rows[y] = new string(_canvas, y * _width, _width);
        }

        return rows;
    }

    private void ValidateRect(int x0, int y0, int x1, int y1)
    {
        if (x0 < 0 || y0 < 0 || x1 >= _width || y1 >= _height || x0 > x1 || y0 > y1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x0),
                $"Stamp rect ({x0}, {y0})-({x1}, {y1}) is not a valid inclusive rect inside the {_width}x{_height} canvas.");
        }
    }
}
