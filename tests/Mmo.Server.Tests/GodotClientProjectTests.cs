using Xunit;

namespace Mmo.Server.Tests;

public sealed class GodotClientProjectTests
{
    [Fact]
    public void GodotClientUsesCompatibilityRenderer()
    {
        var project = File.ReadAllText(FindGodotProject());

        // config/features must list the Compatibility renderer; don't pin the engine version — it changes on
        // Godot upgrades (was "4.6"; after the 4.7 migration it is "4.7", "C#", "GL Compatibility").
        Assert.Contains("\"GL Compatibility\")", project);
        Assert.Contains("renderer/rendering_method=\"gl_compatibility\"", project);
        Assert.Contains("renderer/rendering_method.mobile=\"gl_compatibility\"", project);
        Assert.DoesNotContain("Forward Plus", project);
        Assert.DoesNotContain("forward_plus", project);
        Assert.DoesNotContain("rendering_device/driver.windows=\"d3d12\"", project);
    }

    [Fact]
    public void GodotClientHasToggleablePerformanceHud()
    {
        var root = File.ReadAllText(FindGodotSource("MmoClientRoot.cs"));
        var graph = File.ReadAllText(FindGodotSource("FrameTimeGraph.cs"));

        // The perf HUD now lives on the consolidated debug panel's Perf tab (the default tab, reached by the F1
        // toggle). Assert the HUD content itself is present.
        Assert.Contains("PerfHud", root);
        Assert.Contains("FrameTimeGraph", root);
        Assert.Contains("Performance.GetMonitor(Performance.Monitor.TimeFps)", root);
        Assert.Contains("Performance.GetMonitor(Performance.Monitor.TimeProcess)", root);
        Assert.Contains("Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess)", root);
        Assert.Contains("Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)", root);
        Assert.Contains("Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame)", root);
        Assert.Contains("Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame)", root);
        Assert.Contains("Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed)", root);
        Assert.Contains("Performance.GetMonitor(Performance.Monitor.MemoryStatic)", root);
        Assert.Contains("Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)", root);
        Assert.Contains("GC.GetTotalMemory(false)", root);
        Assert.Contains("_nextPerfHudAt = now.TotalSeconds + 0.1d;", root);
        // Interpolation queue depth / cadence is surfaced in the perf HUD (no env flag needed).
        Assert.Contains("interp q=", root);
        Assert.Contains("SampleCount = 120", graph);
        Assert.Contains("DrawLine", graph);
        Assert.Contains("QueueRedraw", graph);
    }

    [Fact]
    public void GodotClientConsolidatesDebugSurfacesIntoOneTabbedPanel()
    {
        var root = File.ReadAllText(FindGodotSource("MmoClientRoot.cs"));

        // SINGLE hotkey: F1 toggles the consolidated panel (matched as "Key.F1)" so it doesn't collide with the
        // F11 fullscreen handler). The six former debug/tuning F-key handlers (F3–F8) are gone.
        Assert.Contains("Key.F1)", root);
        Assert.Contains("ToggleDebugPanel", root);
        Assert.Contains("OpenDebugPanelOnPerfTab", root);
        Assert.DoesNotContain("Key.F3", root);
        Assert.DoesNotContain("Key.F4", root);
        Assert.DoesNotContain("Key.F5", root);
        Assert.DoesNotContain("Key.F6", root);
        Assert.DoesNotContain("Key.F7", root);
        Assert.DoesNotContain("Key.F8", root);

        // Built as a TabContainer with the six thematic tabs (the page Control's Name is the tab title).
        Assert.Contains("new TabContainer", root);
        Assert.Contains("AddDebugTab(tabs, \"Perf\")", root);
        Assert.Contains("AddDebugTab(tabs, \"Visual\")", root);
        Assert.Contains("AddDebugTab(tabs, \"Movement\")", root);
        Assert.Contains("AddDebugTab(tabs, \"Combat\")", root);
        Assert.Contains("AddDebugTab(tabs, \"Server\")", root);
        Assert.Contains("AddDebugTab(tabs, \"Vitals\")", root);

        // Admin gating maps to tabs: only the Perf tab is built unconditionally; the rest are built lazily on the
        // first Admin open (the role is unknown at construction), so a non-admin only ever sees Perf.
        Assert.Contains("BuildPerfTab(tabs)", root);
        Assert.Contains("EnsureAdminTabsBuilt", root);
        Assert.Contains("_client?.Role != ClientRole.Admin", root);

        // Every migrated control's Apply/handler wiring is still present (a sample across the tabs).
        Assert.Contains("OnTuningApplyPressed", root);   // Server tab
        Assert.Contains("OnVisualApplyPressed", root);   // Visual tab
        Assert.Contains("OnMovementApplyPressed", root); // Movement tab
        Assert.Contains("OnCombatApplyPressed", root);   // Combat tab
        Assert.Contains("OnStatApplyPressed", root);     // Vitals tab
        Assert.Contains("ApplyFpsUncap", root);          // Perf tab toggle
        Assert.Contains("ApplyFrameCsvDump", root);      // Perf tab toggle
    }

    [Fact]
    public void GodotClientHasLiveAntiAliasingControls()
    {
        var root = File.ReadAllText(FindGodotSource("MmoClientRoot.cs"));

        // FXAA defaults ON (seeded in _Ready) and is now a live Visual-tab checkbox driving ScreenSpaceAA; the MSAA
        // dropdown drives Msaa3D across Disabled/2x/4x/8x. Both applied live at runtime (no restart).
        Assert.Contains("GetViewport().ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Fxaa", root);
        Assert.Contains("ApplyFxaa", root);
        Assert.Contains("ApplyMsaaSelected", root);
        Assert.Contains("Viewport.Msaa.Msaa2X", root);
        Assert.Contains("Viewport.Msaa.Msaa4X", root);
        Assert.Contains("Viewport.Msaa.Msaa8X", root);
        Assert.Contains("GetViewport().Msaa3D", root);
    }

    [Fact]
    public void GodotClientBatchesStaticWallsAndReusesEntityResources()
    {
        var root = File.ReadAllText(FindGodotSource("MmoClientRoot.cs"));

        Assert.Contains("MultiMeshInstance3D", root);
        Assert.Contains("new MultiMesh", root);
        Assert.Contains("InstanceCount = wallTiles.Count", root);
        Assert.Contains("SetInstanceTransform", root);
        Assert.Contains("Name = \"WallTiles\"", root);
        Assert.DoesNotContain("Name = $\"Wall_{tile.X}_{tile.Y}\"", root);
        Assert.Contains("private readonly BoxMesh _wallMesh", root);

        // Entity-resource reuse moved into the visual classes (S61): BoxVisual caches its mesh + materials
        // as STATIC readonly fields shared across every instance — never allocated per entity/frame.
        var box = File.ReadAllText(FindGodotSource(Path.Combine("Visuals", "BoxVisual.cs")));
        Assert.Contains("static readonly CapsuleMesh EntityMesh", box);
        Assert.Contains("static readonly BoxMesh ResourceMesh", box);
        Assert.Contains("static readonly StandardMaterial3D LocalEntityMaterial", box);
        Assert.Contains("static readonly StandardMaterial3D RemoteEntityMaterial", box);
        Assert.Contains("static readonly StandardMaterial3D ResourceAvailableMaterial", box);
        Assert.Contains("static readonly StandardMaterial3D ResourceDepletedMaterial", box);
        Assert.Contains("_body.Mesh = _isResource ? ResourceMesh : EntityMesh", box);
    }

    [Fact]
    public void GodotClientDrawsGridWithAShaderOnAPlane()
    {
        var root = File.ReadAllText(FindGodotSource("MmoClientRoot.cs"));

        Assert.Contains("shader_type spatial", root);
        Assert.Contains("new PlaneMesh", root);
        // The old per-line ImmediateMesh grid geometry is gone.
        Assert.DoesNotContain("Mesh.PrimitiveType.Lines", root);
    }

    private static string FindGodotProject()
    {
        return FindGodotSource("project.godot");
    }

    private static string FindGodotSource(string fileName)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "Mmo.Client.Godot", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not find Godot source {fileName}.");
    }
}
