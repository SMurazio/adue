using Xunit;

namespace Mmo.Server.Tests;

public sealed class WebClientAssetTests
{
    [Fact]
    public void WebClientUsesBufferedRemoteEntityInterpolation()
    {
        var app = File.ReadAllText(FindWebAsset("app.js"));

        Assert.Contains("const snapshotInterpolationDelayMs = 150;", app);
        Assert.Contains("const maxEntitySnapshotBuffer = 8;", app);
        Assert.Contains("function interpolateRemoteEntityPosition", app);
        Assert.Contains("addEntitySnapshotSample(entry, tick, sequence", app);
    }

    private static string FindWebAsset(string fileName)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "Mmo.Client.Web", "wwwroot", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not find web asset {fileName}.");
    }
}
