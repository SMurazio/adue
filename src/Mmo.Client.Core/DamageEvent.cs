namespace Mmo.Client.Core;

// COMBAT-QOL: a cosmetic damage event drained by the presentation layer to float a "-Amount" number over the victim.
// NetworkId is the victim entity (the renderer looks up its live visual to place the number); Amount is the HP removed
// by the hit; Health is the victim's new current HP after the hit (carried for completeness — the authoritative bar
// still rides the snapshot). Presentation-only: the server stays authoritative on HP.
public readonly record struct DamageEvent(uint NetworkId, int Amount, ushort Health);
