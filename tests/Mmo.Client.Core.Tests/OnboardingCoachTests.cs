using System.Collections.Generic;
using System.Linq;
using Mmo.Client.Core;
using Mmo.Shared.Domain;
using Xunit;

namespace Mmo.Client.Core.Tests;

// ADUE P2-B (todo/S-p2-onboarding-verb-hints.md): the pure hint-selection brain. These tests pin the WHAT-shows
// decisions (pairing prompt vs. verb layer vs. nothing), the verb table's shape/copy invariants, and the used-verb
// dimming — everything the Godot renderer trusts. The visual/feel of the labels is a HUMAN feel-test, not covered here.
public sealed class OnboardingCoachTests
{
    // ---- the four (room × paired) quadrants --------------------------------------------------------------

    [Fact]
    public void Select_OutsideRoom_Paired_ShowsNothing()
    {
        var view = OnboardingCoach.Select(inPracticeRoom: false, isPaired: true, usedVerbs: null);

        Assert.False(view.AnyVisible);
        Assert.False(view.ShowPairingPrompt);
        Assert.False(view.ShowVerbHints);
        Assert.Empty(view.VerbHints);
    }

    [Fact]
    public void Select_OutsideRoom_Unpaired_ShowsOnlyPairingPrompt()
    {
        // The lobby / anywhere-outside case: the unmissable "get a partner" nudge, but NO verb layer (you only see the
        // verbs once you're in the practice room).
        var view = OnboardingCoach.Select(inPracticeRoom: false, isPaired: false, usedVerbs: null);

        Assert.True(view.AnyVisible);
        Assert.True(view.ShowPairingPrompt);
        Assert.Equal(OnboardingCoach.PairingPromptText, view.PairingPrompt);
        Assert.False(view.ShowVerbHints);
        Assert.Empty(view.VerbHints);
    }

    [Fact]
    public void Select_InRoom_Paired_ShowsVerbsOnly()
    {
        var view = OnboardingCoach.Select(inPracticeRoom: true, isPaired: true, usedVerbs: null);

        Assert.True(view.AnyVisible);
        Assert.False(view.ShowPairingPrompt);
        Assert.Equal(string.Empty, view.PairingPrompt);
        Assert.True(view.ShowVerbHints);
        Assert.Equal(4, view.VerbHints.Count);
        Assert.Equal(OnboardingCoach.VerbHeadingText, view.VerbHeading);
    }

    [Fact]
    public void Select_InRoom_Unpaired_ShowsBothPromptAndVerbs()
    {
        // The most important case: a stranger duo standing in the practice room, not yet paired — they must see BOTH
        // the pairing nudge AND the verb list (whose Q line tells them the cross needs a partner).
        var view = OnboardingCoach.Select(inPracticeRoom: true, isPaired: false, usedVerbs: null);

        Assert.True(view.ShowPairingPrompt);
        Assert.True(view.ShowVerbHints);
        Assert.Equal(4, view.VerbHints.Count);
    }

    // ---- the verb table: order, keys, and the signature CROSS copy ---------------------------------------

    [Fact]
    public void Select_VerbHints_AreQrgvInOrder()
    {
        var view = OnboardingCoach.Select(true, true, null);

        Assert.Equal(
            new[] { OnboardingVerb.Fusion, OnboardingVerb.Shield, OnboardingVerb.Tether, OnboardingVerb.Detonate },
            view.VerbHints.Select(h => h.Verb).ToArray());
        Assert.Equal(
            new[] { "Q", "R", "G", "V" },
            view.VerbHints.Select(h => h.Key).ToArray());
    }

    [Fact]
    public void Select_EveryVerbHint_HasNonEmptyNameAndTeach()
    {
        var view = OnboardingCoach.Select(true, true, null);

        Assert.All(view.VerbHints, h =>
        {
            Assert.False(string.IsNullOrWhiteSpace(h.Name));
            Assert.False(string.IsNullOrWhiteSpace(h.Teach));
        });
    }

    [Fact]
    public void Select_FusionHint_TeachesTheCross()
    {
        // Law-7 tone + the signature mechanic: the Q line must actually teach the CROSS→fuse, not just "shoot".
        var fusion = OnboardingCoach.Select(true, true, null).VerbHints.Single(h => h.Verb == OnboardingVerb.Fusion);

        Assert.Contains("CROSS", fusion.Teach);
        Assert.Contains("FUSE", fusion.Teach);
    }

    // ---- used-verb dimming (the "learned it" nicety) -----------------------------------------------------

    [Fact]
    public void Select_MarksOnlyUsedVerbs_AsUsed()
    {
        var used = new HashSet<OnboardingVerb> { OnboardingVerb.Shield, OnboardingVerb.Tether };
        var view = OnboardingCoach.Select(true, true, used);

        Assert.False(view.VerbHints.Single(h => h.Verb == OnboardingVerb.Fusion).Used);
        Assert.True(view.VerbHints.Single(h => h.Verb == OnboardingVerb.Shield).Used);
        Assert.True(view.VerbHints.Single(h => h.Verb == OnboardingVerb.Tether).Used);
        Assert.False(view.VerbHints.Single(h => h.Verb == OnboardingVerb.Detonate).Used);
    }

    // ---- the stateful coach wrapper ----------------------------------------------------------------------

    [Fact]
    public void Coach_Build_ReflectsMarkedVerbs()
    {
        var coach = new OnboardingCoach();
        Assert.True(coach.MarkUsed(OnboardingVerb.Fusion));   // first use -> true
        Assert.False(coach.MarkUsed(OnboardingVerb.Fusion));  // idempotent -> false

        var view = coach.Build(inPracticeRoom: true, isPaired: true);
        Assert.True(view.VerbHints.Single(h => h.Verb == OnboardingVerb.Fusion).Used);
        Assert.True(coach.HasUsed(OnboardingVerb.Fusion));
    }

    [Fact]
    public void Coach_Reset_ClearsUsedProgress()
    {
        var coach = new OnboardingCoach();
        coach.MarkUsed(OnboardingVerb.Detonate);
        coach.Reset();

        Assert.False(coach.HasUsed(OnboardingVerb.Detonate));
        Assert.False(coach.Build(true, true).VerbHints.Single(h => h.Verb == OnboardingVerb.Detonate).Used);
    }

    [Theory]
    [InlineData(DuoAbilityKind.Shield, OnboardingVerb.Shield)]
    [InlineData(DuoAbilityKind.TetherToggle, OnboardingVerb.Tether)]
    [InlineData(DuoAbilityKind.Detonate, OnboardingVerb.Detonate)]
    public void FromDuoAbility_MapsEachWireSelector(DuoAbilityKind ability, OnboardingVerb expected)
    {
        Assert.Equal(expected, OnboardingCoach.FromDuoAbility(ability));

        var coach = new OnboardingCoach();
        Assert.True(coach.MarkUsed(ability));
        Assert.True(coach.HasUsed(expected));
    }

    [Fact]
    public void Select_NullUsedSet_TreatedAsNothingUsed()
    {
        var view = OnboardingCoach.Select(true, true, usedVerbs: null);
        Assert.All(view.VerbHints, h => Assert.False(h.Used));
    }
}
