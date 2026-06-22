using Godot;

namespace Mmo.Client.Godot.UI;

// COMBAT-TUNING (radial cooldown): a slot overlay that renders a cooldown as a darkening PIE-SLICE sweep (a radial
// "clock wipe") plus a centered whole-second countdown number — the classic action-bar autoattack indicator. It is
// driven by an AUTHORITATIVE remaining FRACTION in [0,1] pushed every frame by SlotButton.ApplyRadial (sourced from
// MmoClient.AttackCooldownRemainingFraction, which reads the real attack cadence and the live, replicated
// combat.attackCooldownMs). Because the fraction is pushed rather than self-ticked, the sweep tracks the server
// cooldown exactly and reacts instantly to a live cooldown tweak — no local timer to drift.
//
// Presentation-only: it reads nothing but the fraction/seconds it is given and never touches game/movement state.
public partial class RadialCooldownOverlay : Control
{
    // The remaining sweep fraction (1.0 = full overlay just after firing, 0.0 = ready/empty). Redrawn on change.
    private float _fraction;
    private float _remainingSeconds;
    private Label? _countdown;

    // Semi-opaque dark wedge so the icon beneath still reads through it (matches the local-tick path's darken).
    private static readonly Color SweepColor = new(0f, 0f, 0f, 0.55f);

    public override void _Ready()
    {
        // Mask the sweep to the SQUARE slot: the pie-slice is drawn out to the corner radius so it can shade the
        // whole square, but a circular arc at that radius bulges past the slot edges between corners. ClipContents
        // clips this Control's drawing to its own rect (the square slot), so the darkening fills the square cleanly
        // instead of reading as an oversized disc.
        ClipContents = true;

        // Centered whole-second countdown number over the sweep.
        _countdown = new Label
        {
            Name = "Countdown",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _countdown.SetAnchorsPreset(LayoutPreset.FullRect);
        _countdown.AddThemeColorOverride("font_color", Colors.White);
        AddChild(_countdown);
        UpdateCountdownText();
    }

    // Push the authoritative remaining fraction (0..1) + remaining seconds. Triggers a redraw of the sweep and
    // refreshes the countdown number. A fraction <= 0 clears the overlay (ready) and blanks the number.
    public void Set(float fraction, float remainingSeconds)
    {
        _fraction = Mathf.Clamp(fraction, 0f, 1f);
        _remainingSeconds = remainingSeconds < 0f ? 0f : remainingSeconds;
        Visible = _fraction > 0f;
        UpdateCountdownText();
        QueueRedraw();
    }

    private void UpdateCountdownText()
    {
        if (_countdown is not null)
        {
            _countdown.Text = _fraction > 0f ? Mathf.CeilToInt(_remainingSeconds).ToString() : string.Empty;
        }
    }

    public override void _Draw()
    {
        if (_fraction <= 0f)
        {
            return;
        }

        var rect = GetRect();
        var center = rect.Size * 0.5f;
        // Radius covers the corners so the wedge fully shades the square slot.
        var radius = center.Length();

        // Sweep CLOCKWISE from straight up (12 o'clock) by `_fraction` of the full circle — the remaining cooldown.
        // -PI/2 is up; positive angles go clockwise in Godot's Y-down screen space.
        const float start = -Mathf.Pi / 2f;
        var sweep = _fraction * Mathf.Tau;

        const int segments = 32;
        var steps = Mathf.Max(1, Mathf.CeilToInt(segments * _fraction));
        var points = new Vector2[steps + 2];
        points[0] = center;
        for (var i = 0; i <= steps; i++)
        {
            var a = start + sweep * (i / (float)steps);
            points[i + 1] = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
        }

        DrawColoredPolygon(points, SweepColor);
    }
}
