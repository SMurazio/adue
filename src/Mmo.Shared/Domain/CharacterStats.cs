namespace Mmo.Shared.Domain;

// COMBAT-S1: which vital a value refers to. Lives in Shared so the wire (AdminSetStatMessage.Stat byte), the
// server setter (WorldEntity.TrySetStatCurrent), and the client dev-set window all name the SAME canonical values
// (0=Health, 1=Mana, 2=Stamina). The codec validates the byte range on decode.
public enum StatKind : byte
{
    Health = 0,
    Mana = 1,
    Stamina = 2,
}

// COMBAT-S1 (Stage 1 of docs/combat-design.md): the three character vitals — health, mana, stamina — each as a
// current + max pair. Server-authoritative truth lives on WorldEntity; this value type is the wire/transport shape
// (it rides the owner-only PlayerStatsMessage) and the unit under test for the clamp logic.
//
// No damage / regen / death logic here yet — that is later stages. This stage only models the values existing,
// being clamped to [0, max], and being replicated. Each pair is an int (whole points); max defaults to 100.
public readonly record struct CharacterStats(
    int Health,
    int MaxHealth,
    int Mana,
    int MaxMana,
    int Stamina,
    int MaxStamina)
{
    // The Stage-1 default vitals: full 100/100 each, server-authoritative on spawn.
    public static CharacterStats Default => new(100, 100, 100, 100, 100, 100);

    // Returns a copy with Health set, clamped to [0, MaxHealth]. Max is never moved by this stage's dev setter
    // (clamp the current value into the existing max).
    public CharacterStats WithHealth(int value) => this with { Health = Clamp(value, MaxHealth) };

    public CharacterStats WithMana(int value) => this with { Mana = Clamp(value, MaxMana) };

    public CharacterStats WithStamina(int value) => this with { Stamina = Clamp(value, MaxStamina) };

    // Clamp a current value into [0, max]. A non-positive max (degenerate) yields 0.
    public static int Clamp(int value, int max)
    {
        if (max <= 0)
        {
            return 0;
        }

        if (value < 0)
        {
            return 0;
        }

        return value > max ? max : value;
    }
}
