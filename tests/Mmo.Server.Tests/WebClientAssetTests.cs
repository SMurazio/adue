using Xunit;

namespace Mmo.Server.Tests;

public sealed class WebClientAssetTests
{
    [Fact]
    public void WebClientUsesTileTweenedMoveSteps()
    {
        var app = File.ReadAllText(FindWebAsset("app.js"));

        Assert.Contains("let tileGridWidth = 128;", app);
        Assert.Contains("let tileGridHeight = 128;", app);
        Assert.Contains("const defaultTileStepTweenMs = 140;", app);
        Assert.Contains("let tileStepTweenMs = defaultTileStepTweenMs;", app);
        Assert.Contains("const remoteInterpolationCadenceMultiplier = 2;", app);
        Assert.Contains("let movementInterpolationDelayMs = tileStepTweenMs * remoteInterpolationCadenceMultiplier;", app);
        Assert.Contains("const defaultDebugVisibilityRadius = 40;", app);
        Assert.Contains("let debugVisibilityRadius = defaultDebugVisibilityRadius;", app);
        Assert.Contains("function setStepCooldownMs", app);
        Assert.Contains("setStepCooldownMs(message.stepCooldownMs, message.tickRate);", app);
        Assert.Contains("function computeEffectiveStepCadenceMs", app);
        Assert.Contains("Math.ceil(cooldownMs / tickIntervalMs)", app);
        Assert.Contains("movementInterpolationDelayMs = tileStepTweenMs * remoteInterpolationCadenceMultiplier;", app);
        Assert.Contains("function setInterestRadiusTiles", app);
        Assert.Contains("setInterestRadiusTiles(message.interestRadiusTiles);", app);
        Assert.Contains("debugVisibilityRadius = Number.isFinite(parsed) && parsed > 0", app);
        Assert.Contains("const selfMovementInterpolationDelayMs = 0;", app);
        Assert.Contains("const stepRetryMs = 50;", app);
        Assert.Contains("const movementChordDelayMs = 70;", app);
        Assert.Contains("const entityRegistryMaxEntries = 2048;", app);
        Assert.Contains("const screenInputStepDirections = new Map([", app);
        Assert.Contains("[\"0,1\", \"NW\"]", app);
        Assert.Contains("[\"1,1\", \"N\"]", app);
        Assert.Contains("[\"1,0\", \"NE\"]", app);
        Assert.Contains("[\"1,-1\", \"E\"]", app);
        Assert.Contains("function updateEntityTileTween", app);
        Assert.Contains("function screenInputToStepDirection", app);
        Assert.Contains("return screenInputStepDirections.get(`${x},${y}`) ?? null;", app);
        Assert.Contains("const movementKeysByCode = new Map([", app);
        Assert.Contains("[\"KeyS\", \"s\"]", app);
        Assert.Contains("[\"KeyD\", \"d\"]", app);
        Assert.Contains("function movementKeyFromEvent", app);
        Assert.Contains("function setMovementKeyDown", app);
        Assert.Contains("keysDown.delete(\"w\");", app);
        Assert.Contains("keysDown.delete(\"a\");", app);
        Assert.Contains("now - heldMoveChangedAt < movementChordDelayMs", app);
        Assert.Contains("function rememberEntityMetadata", app);
        Assert.Contains("function pruneEntityRegistry", app);
        Assert.Contains("const snapshotEntities = message.entities ?? [];", app);
        Assert.Contains("Math.abs(entry.tileX - entity.x) > 1", app);
        Assert.Contains("entity.characterId === state.selfCharacterId", app);
        Assert.Contains("function mergeSnapshotEntities", app);
        Assert.Contains("case \"zoneInfo\":", app);
        Assert.Contains("function handleZoneInfo", app);
        Assert.Contains("blockedTiles = new Set((message.blockedTiles ?? []).map(tile => `${tile.x},${tile.y}`));", app);
        Assert.Contains("function rebuildWorldMap", app);
        Assert.Contains("function sendMoveStep", app);
        Assert.Contains("type: \"moveStep\"", app);
        Assert.Contains("confirmedStepQueue", app);
        Assert.Contains("function startNextConfirmedStep", app);
        Assert.Contains("function movementInterpolationDelayForEntry", app);
        Assert.Contains("return entry.isSelf ? selfMovementInterpolationDelayMs : movementInterpolationDelayMs;", app);
        Assert.Contains("nowMs - next.receivedAt < movementInterpolationDelayForEntry(entry)", app);
        Assert.Contains("entry.renderPosition.lerpVectors(step.from, step.to, alpha);", app);
        Assert.Contains("desiredFocus.copy(self.renderPosition);", app);
        Assert.DoesNotContain("const debugVisibilityRadius = 96;", app);
        Assert.DoesNotContain("const eased = alpha * alpha", app);
        Assert.DoesNotContain("function buildBlockedTileSet", app);
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
