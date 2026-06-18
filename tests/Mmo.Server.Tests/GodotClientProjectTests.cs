using Xunit;

namespace Mmo.Server.Tests;

public sealed class GodotClientProjectTests
{
    [Fact]
    public void GodotClientUsesCompatibilityRenderer()
    {
        var project = File.ReadAllText(FindGodotProject());

        Assert.Contains("config/features=PackedStringArray(\"4.6\", \"GL Compatibility\")", project);
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

        Assert.Contains("Key.F3", root);
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
        // Interpolation queue depth / cadence is surfaced in the F3 HUD (no env flag needed).
        Assert.Contains("interp q=", root);
        Assert.Contains("SampleCount = 120", graph);
        Assert.Contains("DrawLine", graph);
        Assert.Contains("QueueRedraw", graph);
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
        Assert.Contains("private readonly CapsuleMesh _entityMesh", root);
        Assert.Contains("MaterialOverride = state.IsLocal ? _localEntityMaterial : _remoteEntityMaterial", root);
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
