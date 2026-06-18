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
        Assert.Contains("SampleCount = 120", graph);
        Assert.Contains("DrawLine", graph);
        Assert.Contains("QueueRedraw", graph);
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
