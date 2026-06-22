using System.Collections.Generic;
using Godot;

namespace Mmo.Client.Godot.Visuals;

// COMBAT-QOL: spawns short-lived floating "-N" damage numbers above entities. Presentation only — the server stays
// authoritative on HP; this just visualises a DamageEvent. Each number is a billboarded Label3D that rises ~0.6
// world units and fades to transparent over ~0.8 s, then is RETURNED TO A POOL (not freed) so a rapid stream of hits
// never churns the scene tree or leaks nodes. A hard cap bounds the live count; once reached, the OLDEST active
// number is recycled for the new one (the freshest hits always show).
//
// Coordinates: numbers are parented to the SAME entity root the EntityVisuals live under, so a victim visual's local
// Position is the world position to anchor the number at (offset upward by the label height). The caller resolves the
// victim's live position from the EntityRenderer and passes it in; if the victim isn't currently rendered, the caller
// simply drops the event (no number).
public sealed class FloatingTextManager
{
    // Lifetime + motion of a single number. Tuned for a quick, readable pop that doesn't linger over the target.
    private const float LifetimeSeconds = 0.8f;
    private const float RiseWorldUnits = 0.6f;
    // How high above the anchor a number starts (roughly the overhead-label band so it reads as "the entity's" damage).
    private const float BaseHeight = 2.0f;
    private const float FontSizePixels = 0.012f;

    // Hard cap on simultaneously-active numbers. Plenty for normal play; bounds memory under a hostile/burst flood.
    private const int MaxActive = 64;

    private readonly Node3D _parent;
    private readonly List<Active> _active = new();
    private readonly Stack<Label3D> _pool = new();

    public FloatingTextManager(Node3D parent)
    {
        _parent = parent;
    }

    // A live floating number: its label, the world anchor (entity position at spawn — numbers do NOT track the entity
    // after spawn; they rise from where the hit landed), and how long it has been alive.
    private struct Active
    {
        public Label3D Label;
        public Vector3 Anchor;
        public float Age;
    }

    // Spawn a "-amount" number anchored at `worldPosition` (the victim visual's position). Recycles the oldest active
    // number if the cap is reached so the buffer never grows unbounded.
    public void Spawn(Vector3 worldPosition, int amount)
    {
        if (_active.Count >= MaxActive)
        {
            // Recycle the oldest (index 0) rather than allocating beyond the cap.
            var oldest = _active[0];
            _active.RemoveAt(0);
            ReturnToPool(oldest.Label);
        }

        var label = Rent();
        label.Text = "-" + amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        label.Modulate = new Color(1f, 0.25f, 0.2f, 1f);     // red, fully opaque to start.
        label.OutlineModulate = new Color(0f, 0f, 0f, 1f);   // reset the outline alpha (a recycled label may have faded).
        label.Position = worldPosition + new Vector3(0f, BaseHeight, 0f);
        label.Visible = true;

        _active.Add(new Active { Label = label, Anchor = worldPosition, Age = 0f });
    }

    // Advance every active number by `delta` seconds: rise + fade, and recycle any that have outlived LifetimeSeconds.
    // Iterates back-to-front so an in-place removal doesn't skip an entry.
    public void Update(double delta)
    {
        var dt = (float)delta;
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var entry = _active[i];
            entry.Age += dt;
            if (entry.Age >= LifetimeSeconds)
            {
                ReturnToPool(entry.Label);
                _active.RemoveAt(i);
                continue;
            }

            var t = entry.Age / LifetimeSeconds; // 0..1 over the lifetime.
            entry.Label.Position = entry.Anchor + new Vector3(0f, BaseHeight + (RiseWorldUnits * t), 0f);
            // Fade out: full alpha until ~40% of life, then ramp to 0 by the end (a brief readable hold, then fade).
            var alpha = t < 0.4f ? 1f : Mathf.Clamp(1f - ((t - 0.4f) / 0.6f), 0f, 1f);
            var c = entry.Label.Modulate;
            c.A = alpha;
            entry.Label.Modulate = c;
            // Fade the outline alpha in lockstep so the black halo doesn't linger after the red text fades.
            var oc = entry.Label.OutlineModulate;
            oc.A = alpha;
            entry.Label.OutlineModulate = oc;
            _active[i] = entry;
        }
    }

    private Label3D Rent()
    {
        if (_pool.Count > 0)
        {
            return _pool.Pop();
        }

        var label = new Label3D
        {
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            FontSize = 64,
            OutlineSize = 12,
            OutlineModulate = new Color(0f, 0f, 0f, 1f), // black outline so the red number reads over any terrain.
            PixelSize = FontSizePixels,
            // Render on top of the world like the name labels / HP bars.
            RenderPriority = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _parent.AddChild(label);
        return label;
    }

    private void ReturnToPool(Label3D label)
    {
        label.Visible = false;
        _pool.Push(label);
    }
}
