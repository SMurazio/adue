namespace Mmo.Server.Runtime;

internal sealed class NetworkIdPool
{
    private readonly Queue<uint> _available = [];
    private readonly HashSet<uint> _availableSet = [];
    private uint _next = 1;

    public uint Rent()
    {
        while (_available.TryDequeue(out var recycled))
        {
            if (_availableSet.Remove(recycled))
            {
                return recycled;
            }
        }

        if (_next > ushort.MaxValue)
        {
            throw new InvalidOperationException("No snapshot-safe network ids are available.");
        }

        return _next++;
    }

    public void Return(uint networkId)
    {
        if (networkId == 0 || networkId > ushort.MaxValue || networkId >= _next)
        {
            return;
        }

        if (_availableSet.Add(networkId))
        {
            _available.Enqueue(networkId);
        }
    }
}
