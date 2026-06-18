namespace Mmo.Server.Runtime;

public enum TickBudgetCategory
{
    Movement = 0,
    Aoi = 1,
    Serialize = 2,
    Network = 3,
    Persistence = 4,
    Other = 5
}
