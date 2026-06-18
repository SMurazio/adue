using Xunit;

namespace Mmo.Server.Tests;

public sealed class WebClientAssetTests
{
    [Fact]
    public void WebClientUsesTileTweenedMoveSteps()
    {
        var app = File.ReadAllText(FindWebAsset("app.js"));

        Assert.Contains("const tileStepTweenMs = 200;", app);
        Assert.Contains("const movementInterpolationDelayMs = tileStepTweenMs;", app);
        Assert.Contains("const stepRetryMs = 50;", app);
        Assert.Contains("const entityRegistryMaxEntries = 2048;", app);
        Assert.Contains("function updateEntityTileTween", app);
        Assert.Contains("function screenInputToStepDirection", app);
        Assert.Contains("return \"E\";", app);
        Assert.Contains("function rememberEntityMetadata", app);
        Assert.Contains("function pruneEntityRegistry", app);
        Assert.Contains("const snapshotEntities = message.entities ?? [];", app);
        Assert.Contains("Math.abs(entry.tileX - entity.x) > 1", app);
        Assert.Contains("entity.characterId === state.selfCharacterId", app);
        Assert.Contains("function mergeSnapshotEntities", app);
        Assert.Contains("function sendMoveStep", app);
        Assert.Contains("type: \"moveStep\"", app);
        Assert.Contains("confirmedStepQueue", app);
        Assert.Contains("function startNextConfirmedStep", app);
        Assert.Contains("entry.renderPosition.lerpVectors(step.from, step.to, alpha);", app);
        Assert.Contains("desiredFocus.copy(self.renderPosition);", app);
        Assert.DoesNotContain("const eased = alpha * alpha", app);
        Assert.DoesNotContain("function interpolateRemoteEntityPosition", app);
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
