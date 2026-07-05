using System.Collections.Generic;
using System.Text;
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

    // BOSS legibility (2026-07-05 feel-test): the "deflected" colour family — the SAME desaturated steel-grey as
    // BoxVisual.PlatingSteelTint (lightened slightly for text contrast), so a bounced-off number visually matches the
    // boss's own plating tint instead of reading as a normal (red) hit.
    private static readonly Color DeflectedColor = new(0.72f, 0.75f, 0.80f, 1f);
    private const string ImmuneText = "IMMUNE"; // P3 ward — genuinely 0 damage.
    private const string TurnedText = "TURNED"; // P1 plating — reduced (chip still lands), never a false "IMMUNE".

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
        var label = SpawnCore(worldPosition);
        label.Text = "-" + amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        label.Modulate = new Color(1f, 0.25f, 0.2f, 1f); // red, fully opaque to start.
    }

    // BOSS legibility (2026-07-05 feel-test): spawn a "deflected" indicator — the hit bounced off a PROTECTED boss
    // (P1 plating or P3 ward) instead of landing normally. `amount > 0` (a P1 chip hit did get a reduced number from
    // the server) renders the number struck-through in steel-grey so it still reads as a real-but-blunted hit;
    // `amount == 0` (no true amount to show — the LOCAL predicted path, since the attacker's own swing is never echoed
    // back with a server number) renders a WORD keyed to the phase: `warded` → "IMMUNE" (P3 ward, genuinely 0 damage),
    // else "TURNED" (P1 plating — the boss IS still taking chip, so a false "IMMUNE" would contradict its dropping
    // health bar; "TURNED" matches the "plating turns your blows" fiction and reads as reduced, not nullified). Same
    // pooled/rise/fade lifecycle as a normal number — only the text + colour differ.
    public void SpawnDeflected(Vector3 worldPosition, int amount, bool warded = false)
    {
        var label = SpawnCore(worldPosition);
        label.Text = amount > 0
            ? Strikethrough("-" + amount.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : (warded ? ImmuneText : TurnedText);
        label.Modulate = DeflectedColor;
    }

    // Shared spawn plumbing: recycle-if-at-cap, rent a pooled label, reset its outline alpha, anchor + show it, and
    // register it as active. Callers set Text/Modulate for their own look immediately after.
    private Label3D SpawnCore(Vector3 worldPosition)
    {
        if (_active.Count >= MaxActive)
        {
            // Recycle the oldest (index 0) rather than allocating beyond the cap.
            var oldest = _active[0];
            _active.RemoveAt(0);
            ReturnToPool(oldest.Label);
        }

        var label = Rent();
        label.OutlineModulate = new Color(0f, 0f, 0f, 1f); // reset the outline alpha (a recycled label may have faded).
        label.Position = worldPosition + new Vector3(0f, BaseHeight, 0f);
        label.Visible = true;

        _active.Add(new Active { Label = label, Anchor = worldPosition, Age = 0f });
        return label;
    }

    // Fakes a strikethrough by interleaving the Unicode combining long-stroke-overlay (U+0336) after every character
    // — Label3D has no native strikethrough style, and this reads clearly at the small on-screen size a floating
    // number renders at.
    private static string Strikethrough(string text)
    {
        var builder = new StringBuilder(text.Length * 2);
        foreach (var c in text)
        {
            builder.Append(c);
            builder.Append('̶'); // combining long stroke overlay
        }

        return builder.ToString();
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
