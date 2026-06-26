using System.Collections.Concurrent;
using Mmo.Server.Data;
using Mmo.Shared.Domain;

namespace Mmo.Server.Runtime;

internal sealed class PersistenceWriteBehindWorker : IAsyncDisposable
{
    private static readonly TimeSpan DefaultFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly ICharacterRepository _characters;
    private readonly ConcurrentQueue<IPersistenceSaveRequest> _queue = new();
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

    // CONTINUOUS MIGRATION (Phase 10): enqueue the character's CONTINUOUS position for write-behind persistence
    // (the float pos_x/pos_y + the derived tile). Replaces the former tile-only EnqueuTile — the dirty-position
    // checkpoint now carries the exact WorldVector so a relog restores the sub-tile spot, not the rounded centre.
    public void EnqueuePosition(Guid characterId, string displayName, WorldVector position)
    {
        if (characterId == Guid.Empty)
        {
            return;
        }

        Enqueue(new PositionSaveRequest(characterId, displayName, position));
    }

    public void EnqueueItems(Guid characterId, string displayName, IReadOnlyList<ItemStack> changes)
    {
        if (characterId == Guid.Empty || changes.Count == 0)
        {
            return;
        }

        Enqueue(new ItemsSaveRequest(characterId, displayName, changes));
    }

    private void Enqueue(IPersistenceSaveRequest request)
    {
        lock (_idleLock)
        {
            if (_pendingWrites == 0)
            {
                _idleWaiter = null;
            }

            _pendingWrites++;
        }

        _queue.Enqueue(request);
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

    private async Task SaveAsync(IPersistenceSaveRequest request)
    {
        try
        {
            await request.PersistAsync(_characters, _shutdown.Token);
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

    private interface IPersistenceSaveRequest
    {
        string DisplayName { get; }

        Task PersistAsync(ICharacterRepository characters, CancellationToken cancellationToken);
    }

    private sealed record PositionSaveRequest(Guid CharacterId, string DisplayName, WorldVector Position) : IPersistenceSaveRequest
    {
        public Task PersistAsync(ICharacterRepository characters, CancellationToken cancellationToken)
        {
            return characters.SavePositionAsync(CharacterId, Position, cancellationToken);
        }
    }

    private sealed record ItemsSaveRequest(Guid CharacterId, string DisplayName, IReadOnlyList<ItemStack> Changes) : IPersistenceSaveRequest
    {
        public Task PersistAsync(ICharacterRepository characters, CancellationToken cancellationToken)
        {
            return characters.SaveItemsAsync(CharacterId, Changes, cancellationToken);
        }
    }
}
