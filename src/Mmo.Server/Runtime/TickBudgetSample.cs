namespace Mmo.Server.Runtime;

public readonly record struct TickBudgetSample(
    double MovementMs,
    double AoiMs,
    double SerializeMs,
    double NetworkMs,
    double PersistenceMs,
    double OtherMs)
{
    public static TickBudgetSample Zero { get; } = new(0, 0, 0, 0, 0, 0);

    public double TotalMs => MovementMs + AoiMs + SerializeMs + NetworkMs + PersistenceMs + OtherMs;

    public double Get(TickBudgetCategory category)
    {
        return category switch
        {
            TickBudgetCategory.Movement => MovementMs,
            TickBudgetCategory.Aoi => AoiMs,
            TickBudgetCategory.Serialize => SerializeMs,
            TickBudgetCategory.Network => NetworkMs,
            TickBudgetCategory.Persistence => PersistenceMs,
            TickBudgetCategory.Other => OtherMs,
            _ => 0
        };
    }
}
