using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

public static class ScreenRelativeDirectionMapper
{
    public static Direction8? FromInputAxes(int x, int y)
    {
        return (Math.Sign(x), Math.Sign(y)) switch
        {
            (0, -1) => Direction8.NW,
            (1, -1) => Direction8.N,
            (1, 0) => Direction8.NE,
            (1, 1) => Direction8.E,
            (0, 1) => Direction8.SE,
            (-1, 1) => Direction8.S,
            (-1, 0) => Direction8.SW,
            (-1, -1) => Direction8.W,
            _ => null
        };
    }
}
