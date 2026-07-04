namespace Mmo.Shared.Domain;

// DUO-WAVE2 (exp/duo-abilities): the byte discriminators for the co-op abilities 2-4 wire messages. SHARED (not
// server-only) because the codec range-validates them on decode and the client switches on them for rendering. Kept
// tiny + explicit-valued so the wire byte is stable and a hostile/corrupt value fails loudly (ProtocolException),
// mirroring AttackKind / TelegraphShapeKind.

// The single client->server duo-ability TRIGGER selector (DuoAbilityMessage.Ability): which co-op verb the key press
// fired. R = Shield (ability 2), G = TetherToggle (ability 3), V = Detonate (ability 4). One message + this selector
// keeps the three discrete key presses on ONE dedup stream (the NET6 lesson: its own cursor, independent of move/
// attack/action/fire).
public enum DuoAbilityKind : byte
{
    Shield = 1,
    TetherToggle = 2,
    Detonate = 3,
}

// The server->partner ECHO-CUE kind (EchoCueMessage.Cue): what the brief flash+ring on the partner's character means,
// so the partner can REACT rather than pre-plan. Ability 2's shield press and ability 4's initiate/confirm all reuse
// this one relay message with a differing kind byte (the orchestrator's "reuse ability 2's cue message with a type
// byte" decision).
public enum EchoCueKind : byte
{
    ShieldPress = 1,
    DetonateInitiate = 2,
    DetonateConfirm = 3,
}

// The server->both-partners TETHER state (TetherStatusMessage.State): Off = not linked (drop the beam), On = linked
// (draw the beam; the CLIENT colours it by the live distance band it computes from the two known player positions —
// no band byte rides the wire), Broken = overstretched-and-snapped (a brief broken cue, then Off). The client needs
// only on/off/broken; the damage bands are resolved server-side.
public enum TetherState : byte
{
    Off = 0,
    On = 1,
    Broken = 2,
}
