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

    private static string FindGodotProject()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "Mmo.Client.Godot", "project.godot");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("Could not find Godot project.godot.");
    }
}
