using Xunit;

namespace Mmo.Server.Tests;

public sealed class LaunchScriptTests
{
    [Fact]
    public void StartServerSupportsVisibleFileLogging()
    {
        var script = File.ReadAllText(FindScript("start-server.ps1"));
        var windowScript = File.ReadAllText(FindScript("run-server-window.ps1"));

        Assert.Contains("[switch]$LogToFile", script);
        Assert.Contains("server.log", script);
        Assert.Contains("server.err.log", script);
        Assert.Contains("run-server-window.ps1", script);
        Assert.Contains("ProcessWindowStyle]::Normal", script);
        Assert.DoesNotContain("WindowStyle Hidden", script);
        Assert.Contains("MMO_SERVER_LOG_FILE", windowScript);
        Assert.Contains("MMO_SERVER_ERR_LOG_FILE", windowScript);
    }

    [Fact]
    public void CommandWrappersDoNotBypassExecutionPolicy()
    {
        foreach (var script in Directory.GetFiles(FindScriptsDirectory(), "*.cmd"))
        {
            var text = File.ReadAllText(script);

            Assert.DoesNotContain("-ExecutionPolicy Bypass", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("-WindowStyle Hidden", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindScript(string fileName)
    {
        var candidate = Path.Combine(FindScriptsDirectory(), fileName);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new FileNotFoundException($"Could not find script {fileName}.");
    }

    private static string FindScriptsDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, ".shared", "skills", "mmo-dev", "scripts");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find mmo-dev scripts directory.");
    }
}
