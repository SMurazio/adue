using System.Text.RegularExpressions;
using Mmo.Shared.Protocol;
using Xunit;

namespace Mmo.Shared.Tests;

// DOCS DRIFT GATE (todo/N-docs-hygiene-resync): docs/protocol.md documents the shipped wire and MUST carry the same
// version number as ProtocolCodec.Version. The doc drifted silently from v35 to v42 once; this test makes the drift a
// red build instead of a stale parenthetical. It pins ONLY the canonical envelope version line (the doc's prose is
// reviewed by humans):
//
//     - `byte` version: `43` (current shipped — keep in sync with `ProtocolCodec.Version`)
//
// The repo root is located by walking up from the test output directory (the same walk-up pattern as
// GameServerMonsterSaveTests.ReadShippedManifest). A missing doc is a FAILURE, not a skip — deleting or moving the doc
// without updating this gate is itself drift.
public sealed class ProtocolDocVersionTests
{
    // Matches the canonical envelope line: "`byte` version: `<N>`". Anchored to the backtick framing so an incidental
    // "version: 43" elsewhere in prose can neither satisfy nor confuse the gate.
    private static readonly Regex VersionLine = new(@"`byte`\s+version:\s*`(\d+)`", RegexOptions.Compiled);

    [Fact]
    public void ProtocolDocVersionMatchesShippedCodecVersion()
    {
        var docPath = FindProtocolDoc();
        var doc = File.ReadAllText(docPath);

        var match = VersionLine.Match(doc);
        Assert.True(
            match.Success,
            $"docs/protocol.md ({docPath}) no longer contains the canonical envelope version line " +
            "(expected a line like: - `byte` version: `" + ProtocolCodec.Version + "` ...). " +
            "Restore the canonical line — the drift gate pins the doc to ProtocolCodec.Version through it.");

        var documented = int.Parse(match.Groups[1].Value);
        Assert.True(
            documented == ProtocolCodec.Version,
            $"docs/protocol.md documents protocol version {documented} but ProtocolCodec.Version is {ProtocolCodec.Version}. " +
            "Update docs/protocol.md (the envelope version line AND a Version History entry for the new version) in the " +
            "SAME unit of work as the version bump — the doc ships with the wire, never behind it.");
    }

    // Walk up from the test output directory to the repo root, identified by docs/protocol.md itself (mirrors
    // GameServerMonsterSaveTests.ReadShippedManifest). FAIL (throw) if the walk-up exhausts every parent — a missing
    // doc must be a red test, not a silent pass.
    private static string FindProtocolDoc()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "protocol.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate docs/protocol.md walking up from " + System.AppContext.BaseDirectory +
            ". The protocol doc must exist — it is drift-gated against ProtocolCodec.Version.");
    }
}
