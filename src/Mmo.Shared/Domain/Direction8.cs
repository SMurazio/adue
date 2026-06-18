namespace Mmo.Shared.Domain;

public enum Direction8 : byte
{
    N = 0,
    NE = 1,
    E = 2,
    SE = 3,
    S = 4,
    SW = 5,
    W = 6,
    NW = 7
}

public static class Direction8Extensions
{
    public static TileCoord Delta(this Direction8 direction)
    {
        return direction switch
        {
            Direction8.N => new TileCoord(0, -1),
            Direction8.NE => new TileCoord(1, -1),
            Direction8.E => new TileCoord(1, 0),
            Direction8.SE => new TileCoord(1, 1),
            Direction8.S => new TileCoord(0, 1),
            Direction8.SW => new TileCoord(-1, 1),
            Direction8.W => new TileCoord(-1, 0),
            Direction8.NW => new TileCoord(-1, -1),
            _ => TileCoord.Zero
        };
    }
}
