using System;
using System.Collections.Generic;
using Mmo.Shared.Domain;

namespace Mmo.Client.Core;

// ADUE P2-B (todo/S-p2-onboarding-verb-hints.md, docs/duo-p2-demo-plan.md): the PURE, headless-testable brain of the
// in-context onboarding layer. It answers the one question "given (am-I-in-the-practice-room, am-I-paired,
// which-verbs-have-I-used-yet), which hints should be on screen right now?" and hands back a plain view-model the
// Godot layer renders verbatim. No Godot types here on purpose — every teaching-copy string and every show/hide
// decision lives in this file so it can be unit-tested in Mmo.Client.Core.Tests (the Godot label layout/legibility is
// a HUMAN feel-test; this class only decides WHAT to show, never how it looks).
//
// The two teaching surfaces:
//   * a PAIRING prompt — shown whenever the local player is unpaired, so two strangers pair without being told
//     (Adue's four verbs are all two-player; solo you can press them but nothing fuses/links);
//   * the VERB layer — the four duo verbs (Q/R/G/V), shown only while inside the practice room (the sealed rehearsal
//     pocket, PracticeRoom.ContainsInterior), each line naming its key + what it does, with the Q line teaching the
//     signature CROSS→fuse. A verb dims once the local player has used it (MarkUsed) — a light "learned it" nicety.
//
// Tone: calm, honest, non-flashing — Law 7 (honest telegraphs) applied to teaching, matching the existing boss
// teach-label voice ("PLATED — cross your skillshots to shatter!"). The copy deliberately mirrors that "cross" verb.

// The four duo verbs, in the fixed display order Q, R, G, V. Distinct from DuoAbilityKind because Q (the fusion
// skillshot) is NOT a DuoAbilityKind — it rides its own fire message — so onboarding needs a superset that also
// covers it. FromDuoAbility maps the three discrete-press abilities onto their verb.
public enum OnboardingVerb : byte
{
    Fusion = 0,   // Q — the fusion skillshot (hold-aim / release), the signature CROSS mechanic
    Shield = 1,   // R — Unison Shield          (DuoAbilityKind.Shield)
    Tether = 2,   // G — Laser Tether toggle     (DuoAbilityKind.TetherToggle)
    Detonate = 3, // V — Midpoint Detonation      (DuoAbilityKind.Detonate)
}

// One rendered verb row. Key is the keyboard letter to draw ("Q"), Name the short verb name, Teach the one-line
// what-it-does, Used = the local player has performed it at least once (render it dimmed / retired).
public readonly record struct OnboardingVerbHint(OnboardingVerb Verb, string Key, string Name, string Teach, bool Used);

// The whole on-screen onboarding view for one frame. Immutable snapshot the renderer reads top-to-bottom; when
// AnyVisible is false the renderer hides its overlay entirely.
public readonly record struct OnboardingHintView(
    bool ShowPairingPrompt,
    string PairingPrompt,
    bool ShowVerbHints,
    string VerbHeading,
    IReadOnlyList<OnboardingVerbHint> VerbHints)
{
    public static readonly OnboardingHintView Hidden =
        new(false, string.Empty, false, string.Empty, Array.Empty<OnboardingVerbHint>());

    // True iff anything at all should be on screen — lets the renderer early-out to a fully hidden overlay.
    public bool AnyVisible => ShowPairingPrompt || ShowVerbHints;
}

// Holds the tiny bit of session state onboarding needs (which verbs the local player has used) and builds the frame
// view from it plus the two live booleans. The used-set is the ONLY mutable state; Build is otherwise a pure
// function of (inPracticeRoom, isPaired, used-set) — see the static Select the unit tests drive directly.
public sealed class OnboardingCoach
{
    // ---- copy (single source; the Godot layer renders these verbatim) ------------------------------------

    // Shown while unpaired. ADUE P2 (todo/S-p2-auto-pair-and-duo-reveal.md): pairing is no longer a typed input — in
    // the demo the server AUTO-PAIRS the two players the moment both are online, so this prompt is only realistically
    // seen in the brief pre-join gap before the partner connects. It stopped telling the player to type `/pair` (a
    // fake choice — there is only ever one possible partner) and instead just reassures them the partner is coming;
    // pair formation itself is celebrated by the one-shot duo-card reveal, not petitioned here.
    // The bare "waiting for your partner" line — the calm reassurance shared with the P2 landing screen
    // (LandingScreen.ConnectingPrompt) so the pre-login/connecting copy lives in exactly one place.
    public const string WaitingForPartnerLine = "Waiting for your partner…";

    public const string PairingPromptText =
        WaitingForPartnerLine + "\nAdue's four verbs are built for two.";

    // Heading over the verb list inside the practice room.
    public const string VerbHeadingText = "PRACTICE ROOM — your four duo verbs:";

    // The canonical verb table (order = display order Q, R, G, V). Copy is calm/honest and, for Q, teaches the CROSS
    // that a pair must discover — the whole point of the practice room. The Godot renderer never hard-codes any of
    // this; it walks the Build() output.
    private static readonly (OnboardingVerb Verb, string Key, string Name, string Teach)[] Verbs =
    {
        (OnboardingVerb.Fusion, "Q", "Fusion Skillshot",
            "Hold to aim, release to fire. CROSS your shot with your partner's to FUSE — that's the one to learn."),
        (OnboardingVerb.Shield, "R", "Unison Shield",
            "A shared bubble that soaks a few hits. Time it together."),
        (OnboardingVerb.Tether, "G", "Laser Tether",
            "Toggle a beam between the two of you — mind the distance."),
        (OnboardingVerb.Detonate, "V", "Midpoint Detonation",
            "Charge, then detonate at the point halfway between you."),
    };

    // The verbs the local player has performed at least once this session — drives the dim/retire nicety. A small set
    // (four possible members); a HashSet is plenty and keeps Build allocation-light.
    private readonly HashSet<OnboardingVerb> _used = new();

    // Record that the local player just used a verb. Returns true iff this was the FIRST use (a state change worth
    // reacting to — e.g. the renderer can play a one-time "got it" beat). Idempotent thereafter.
    public bool MarkUsed(OnboardingVerb verb) => _used.Add(verb);

    // Record a used DUO ABILITY (R/G/V) via its wire selector. Q (fusion) is not a DuoAbilityKind — call
    // MarkUsed(OnboardingVerb.Fusion) directly on the fire path.
    public bool MarkUsed(DuoAbilityKind ability) => MarkUsed(FromDuoAbility(ability));

    public bool HasUsed(OnboardingVerb verb) => _used.Contains(verb);

    // Forget all use-progress (e.g. re-entering the practice room for a fresh rehearsal, or a new session). Optional
    // for callers; the renderer works fine without ever resetting.
    public void Reset() => _used.Clear();

    // Build this frame's view from the live triggers + the accumulated used-set.
    public OnboardingHintView Build(bool inPracticeRoom, bool isPaired) => Select(inPracticeRoom, isPaired, _used);

    // The PURE decision function (no instance state) the unit tests drive across every (room × paired × used)
    // combination. Rules:
    //   * pairing prompt shows whenever UNPAIRED (any location) — it's the unmissable "you need a partner" nudge;
    //   * the verb layer shows only INSIDE the practice room, regardless of paired (you can rehearse solo; the Q line
    //     still tells you the cross needs a partner, which motivates pairing);
    //   * each verb row carries its Used flag from the set so the renderer can dim/retire it.
    // usedVerbs may be null (treated as empty). Never returns null; returns OnboardingHintView.Hidden when nothing
    // should show.
    public static OnboardingHintView Select(bool inPracticeRoom, bool isPaired, IReadOnlySet<OnboardingVerb>? usedVerbs)
    {
        var showPairing = !isPaired;

        if (!inPracticeRoom && !showPairing)
        {
            return OnboardingHintView.Hidden;
        }

        IReadOnlyList<OnboardingVerbHint> rows;
        if (inPracticeRoom)
        {
            var list = new OnboardingVerbHint[Verbs.Length];
            for (var i = 0; i < Verbs.Length; i++)
            {
                var v = Verbs[i];
                var used = usedVerbs is not null && usedVerbs.Contains(v.Verb);
                list[i] = new OnboardingVerbHint(v.Verb, v.Key, v.Name, v.Teach, used);
            }

            rows = list;
        }
        else
        {
            rows = Array.Empty<OnboardingVerbHint>();
        }

        return new OnboardingHintView(
            ShowPairingPrompt: showPairing,
            PairingPrompt: showPairing ? PairingPromptText : string.Empty,
            ShowVerbHints: inPracticeRoom,
            VerbHeading: inPracticeRoom ? VerbHeadingText : string.Empty,
            VerbHints: rows);
    }

    // Map a duo-ability wire selector (R/G/V) onto its onboarding verb. Q (fusion) has no DuoAbilityKind, so it is
    // never produced here.
    public static OnboardingVerb FromDuoAbility(DuoAbilityKind ability) => ability switch
    {
        DuoAbilityKind.Shield => OnboardingVerb.Shield,
        DuoAbilityKind.TetherToggle => OnboardingVerb.Tether,
        DuoAbilityKind.Detonate => OnboardingVerb.Detonate,
        _ => throw new ArgumentOutOfRangeException(nameof(ability), ability, "Unknown DuoAbilityKind."),
    };
}
