namespace Mmo.Shared.Domain;

public readonly record struct WorldBounds(float MinX, float MaxX, float MinY, float MaxY)
{
    public WorldVector Clamp(WorldVector position)
    {
        return new WorldVector(
            Math.Clamp(position.X, MinX, MaxX),
            Math.Clamp(position.Y, MinY, MaxY));
    }
}
