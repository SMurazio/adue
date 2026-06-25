using System.Globalization;
using Mmo.Server.Configuration;
using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

internal sealed class ServerMovementTrace
{
    private readonly ServerOptions _options;
    private readonly Action<string> _write;

    public ServerMovementTrace(ServerOptions options, Action<string>? write = null)
    {
        _options = options;
        _write = write ?? Log.Info;
    }

    public bool Enabled => _options.DebugMovement;

    public bool ShouldTrace(ClientSession session)
    {
        if (!Enabled || !session.IsAuthenticated)
        {
            return false;
        }

        return session.Role == ClientRole.Admin
            || _options.DebugMovementWatchNames.Contains(session.DisplayName)
            || _options.DebugMovementWatchNames.Contains(session.CharacterId.ToString());
    }

    public bool ShouldTrace(WorldEntity entity)
    {
        return entity.OwnerSession is not null && ShouldTrace(entity.OwnerSession);
    }

    public void TickHitch(
        uint serverTick,
        TimeSpan interTickGap,
        TimeSpan tickDuration,
        TimeSpan scheduleDrift,
        TickBudgetSample budget,
        int catchUpTicks,
        GcCollectionSample gc,
        TimeSpan tickInterval)
    {
        if (!Enabled)
        {
            return;
        }

        var gapThresholdMs = tickInterval.TotalMilliseconds * _options.DebugMovementHitchThresholdMultiplier;
        var gapTriggered = interTickGap.TotalMilliseconds >= gapThresholdMs;
        var durationTriggered = tickDuration.TotalMilliseconds >= _options.DebugMovementTickDurationThresholdMs;
        if (!gapTriggered && !durationTriggered)
        {
            return;
        }

        var trigger = gapTriggered && durationTriggered
            ? "gap+duration"
            : gapTriggered ? "gap" : "duration";
        var unbudgetedMs = Math.Max(0, tickDuration.TotalMilliseconds - budget.TotalMs);

        _write(
            "mmo_trace side=server event=tick_hitch" +
            $" ts={Timestamp()} tick={serverTick.ToString(CultureInfo.InvariantCulture)}" +
            $" trigger={trigger}" +
            $" interMs={FormatMs(interTickGap)} durationMs={FormatMs(tickDuration)} driftMs={FormatMs(scheduleDrift)}" +
            $" catchUpTicks={catchUpTicks.ToString(CultureInfo.InvariantCulture)}" +
            $" gc0={gc.Gen0.ToString(CultureInfo.InvariantCulture)} gc1={gc.Gen1.ToString(CultureInfo.InvariantCulture)} gc2={gc.Gen2.ToString(CultureInfo.InvariantCulture)}" +
            $" moveMs={FormatMs(budget.MovementMs)} aoiMs={FormatMs(budget.AoiMs)} serMs={FormatMs(budget.SerializeMs)}" +
            $" netMs={FormatMs(budget.NetworkMs)} persistMs={FormatMs(budget.PersistenceMs)} otherMs={FormatMs(budget.OtherMs)}" +
            $" unbudgetedMs={FormatMs(unbudgetedMs)}");
    }

    public void MoveStep(ClientSession session, uint sequence, MovementStepResult result, uint serverTick)
    {
        if (!ShouldTrace(session))
        {
            return;
        }

        _write(
            "mmo_trace side=server event=move_step" +
            $" ts={Timestamp()} tick={serverTick.ToString(CultureInfo.InvariantCulture)} player={Quote(session.DisplayName)}" +
            $" seq={sequence.ToString(CultureInfo.InvariantCulture)} dir={result.Direction}" +
            $" from={FormatTile(result.From)} target={FormatTile(result.Target)} result={FormatTile(result.Result)}" +
            $" cooldown={FormatBool(result.CooldownElapsed)} walkable={FormatBool(result.TargetWalkable)}" +
            $" accepted={FormatBool(result.Accepted)} reason={result.Reason}");
    }

    // DIAG1: emit the server side of the 3-link recovery chain for a watched entity after a commit was processed.
    // srvSeq = the entity's ACTUAL accepted StepSequence (did the server APPLY the delivered commit?); recvCommits
    // = commit attempts that reached the gate, so a recovered lost commit counts (is delivery recovering the lost
    // commit at all — link 1?); the two
    // reject tallies = commits the server REFUSED (link 2 — the anti-speedhack future-cap "too_early", or a wall
    // "blocked"). Read against the client's pred/conf/lead per docs/movement-loss-degradation-tiers.md to localise
    // the stuck link. Emitted on EVERY commit (accept AND reject) so the future-cap reject — which carries
    // CooldownElapsed=false and is therefore NOT surfaced by MoveStep — is still visible. Measurement only.
    public void CommitCounters(ClientSession session, WorldEntity entity, string lastResult, uint serverTick)
    {
        if (!ShouldTrace(session))
        {
            return;
        }

        _write(
            "mmo_trace side=server event=commit_counters" +
            $" ts={Timestamp()} tick={serverTick.ToString(CultureInfo.InvariantCulture)} player={Quote(session.DisplayName)}" +
            $" srvSeq={entity.StepSequence.ToString(CultureInfo.InvariantCulture)} recvCommits={entity.RecvCommits.ToString(CultureInfo.InvariantCulture)}" +
            $" rejectTooEarly={entity.RejectsCommitTooEarly.ToString(CultureInfo.InvariantCulture)} rejectBlocked={entity.RejectsBlocked.ToString(CultureInfo.InvariantCulture)}" +
            $" lastResult={lastResult}");
    }

    public void SnapshotCarried(ClientSession recipient, WorldEntity entity, uint snapshotSequence, uint serverTick, int chunkIndex, int chunkCount)
    {
        if (!ShouldTrace(entity))
        {
            return;
        }

        _write(
            "mmo_trace side=server event=snapshot_carry" +
            $" ts={Timestamp()} tick={serverTick.ToString(CultureInfo.InvariantCulture)} snapshot={snapshotSequence.ToString(CultureInfo.InvariantCulture)}" +
            $" player={Quote(entity.DisplayName)} recipient={Quote(recipient.DisplayName)} networkId={entity.NetworkId.ToString(CultureInfo.InvariantCulture)}" +
            $" tile={FormatTile(entity.TileCoord)} facing={entity.Facing} chunk={chunkIndex + 1}/{chunkCount}");
    }

    private static string Timestamp()
    {
        return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }

    private static string FormatMs(TimeSpan value)
    {
        return FormatMs(value.TotalMilliseconds);
    }

    private static string FormatMs(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string FormatTile(TileCoord tile)
    {
        return $"{tile.X.ToString(CultureInfo.InvariantCulture)},{tile.Y.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string FormatBool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
