namespace Mmo.Server.Runtime;

internal sealed class ServerRuntimeGuard
{
    private readonly ServerMetrics _metrics;

    public ServerRuntimeGuard(ServerMetrics metrics)
    {
        _metrics = metrics;
    }

    public bool TryRun(string scope, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception)
        {
            _metrics.RecordRuntimeFault();
            Log.Error($"Runtime fault in {scope}.", exception);
            return false;
        }
    }
}
