using System.Collections.Concurrent;
using Mmo.Server.Data;
using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

internal sealed class PersistenceWriteBehindWorker : IAsyncDisposable
{
    private static readonly TimeSpan DefaultFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly ICharacterRepository _characters;
    private readonly ConcurrentQueue<PersistenceSaveRequest> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _workerTask;
    private readonly object _idleLock = new();

    private long _pendingWrites;
    private TaskCompletionSource? _idleWaiter;

    public PersistenceWriteBehindWorker(ICharacterRepository characters)
    {
        _characters = characters;
        _workerTask = Task.Run(ProcessAsync);
    }

    public void EnqueueTile(Guid characterId, string displayName, TileCoord tile)
    {
        if (characterId == Guid.Empty)
        {
            return;
        }

        lock (_idleLock)
        {
            if (_pendingWrites == 0)
            {
                _idleWaiter = null;
            }

            _pendingWrites++;
        }

        _queue.Enqueue(new PersistenceSaveRequest(characterId, displayName, tile));
        _signal.Release();
    }

    public Task FlushAsync(CancellationToken cancellationToken)
    {
        return FlushAsync(DefaultFlushTimeout, cancellationToken);
    }

    public async Task FlushAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task waitTask;
        lock (_idleLock)
        {
            if (_pendingWrites == 0)
            {
                return;
            }

            _idleWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waitTask = _idleWaiter.Task;
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await waitTask.WaitAsync(linkedSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Log.Warn($"Timed out while flushing persistence queue after {timeout.TotalSeconds:0.#}s.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync(CancellationToken.None);
        _shutdown.Cancel();
        _signal.Release();

        try
        {
            await _workerTask;
        }
        catch (OperationCanceledException)
        {
        }

        _signal.Dispose();
        _shutdown.Dispose();
    }

    private async Task ProcessAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }

            while (_queue.TryDequeue(out var request))
            {
                await SaveAsync(request);
            }
        }
    }

    private async Task SaveAsync(PersistenceSaveRequest request)
    {
        try
        {
            await _characters.SaveTileAsync(request.CharacterId, request.Tile, _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to persist {request.DisplayName}", exception);
        }
        finally
        {
            MarkWriteCompleted();
        }
    }

    private void MarkWriteCompleted()
    {
        TaskCompletionSource? idleWaiter = null;
        lock (_idleLock)
        {
            _pendingWrites--;
            if (_pendingWrites == 0)
            {
                idleWaiter = _idleWaiter;
            }
        }

        idleWaiter?.TrySetResult();
    }

    private readonly record struct PersistenceSaveRequest(Guid CharacterId, string DisplayName, TileCoord Tile);
}
