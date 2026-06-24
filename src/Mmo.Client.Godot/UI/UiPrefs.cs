using Godot;

namespace Mmo.Client.Godot.UI;

// Minimal client-side UI preferences store, backed by a Godot ConfigFile at user://ui_prefs.cfg (the per-user
// app-data dir Godot manages — survives relaunch/relog). Introduced for the loot-window placement fix (the
// loot window must reopen where the user last left it, across corpses AND across restarts). No prefs mechanism
// existed before this, so this is the new canonical home for small persisted UI state — keep it tiny and
// presentation-only (no gameplay/protocol state).
//
// Save-on-change is cheap (a handful of bytes) so callers can persist eagerly (e.g. on drag-end / window close).
// A failed load (first run / missing file) is silently treated as "no value" so callers fall back to defaults.
public static class UiPrefs
{
    private const string PrefsPath = "user://ui_prefs.cfg";

    // Persist a window's top-left position under a stable key (e.g. "loot_window"). Stored as two floats.
    public static void SaveWindowPosition(string key, Vector2 position)
    {
        var config = new ConfigFile();
        // Load existing contents first so we don't clobber other sections; ignore the result (missing == empty).
        config.Load(PrefsPath);
        config.SetValue(key, "x", position.X);
        config.SetValue(key, "y", position.Y);
        config.Save(PrefsPath);
    }

    // Read a previously-saved window position. Returns false (and Vector2.Zero) when nothing was stored yet, so
    // the caller can fall back to its default placement (e.g. screen centre).
    public static bool TryLoadWindowPosition(string key, out Vector2 position)
    {
        position = Vector2.Zero;
        var config = new ConfigFile();
        if (config.Load(PrefsPath) != Error.Ok)
        {
            return false;
        }

        if (!config.HasSectionKey(key, "x") || !config.HasSectionKey(key, "y"))
        {
            return false;
        }

        var x = (float)config.GetValue(key, "x", 0f);
        var y = (float)config.GetValue(key, "y", 0f);
        position = new Vector2(x, y);
        return true;
    }
}
