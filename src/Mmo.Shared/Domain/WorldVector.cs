namespace Mmo.Shared.Domain;

public readonly record struct WorldVector(float X, float Y)
{
    public static readonly WorldVector Zero = new(0, 0);

    public float LengthSquared => (X * X) + (Y * Y);

    public WorldVector NormalizeOrZero()
    {
        if (LengthSquared <= 0.0001f)
        {
            return Zero;
        }

        var inverseLength = 1f / MathF.Sqrt(LengthSquared);
        return new WorldVector(X * inverseLength, Y * inverseLength);
    }

    public static WorldVector operator +(WorldVector left, WorldVector right)
    {
        return new WorldVector(left.X + right.X, left.Y + right.Y);
    }

    public static WorldVector operator *(WorldVector vector, float scalar)
    {
        return new WorldVector(vector.X * scalar, vector.Y * scalar);
    }
}
