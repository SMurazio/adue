namespace Mmo.Shared.Domain;

// A compact reference to owned items: which template, and how many. InstanceId is a reserved seam for
// future non-stackable uniques (equipment with durability, etc.); it is present in the type but left
// null for stackable resources and carries no behavior yet.
public readonly record struct ItemStack(string TemplateKey, int Quantity, Guid? InstanceId = null)
{
    public ItemStack WithQuantity(int quantity)
    {
        return this with { Quantity = quantity };
    }
}
