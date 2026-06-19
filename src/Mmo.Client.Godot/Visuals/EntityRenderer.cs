using System.Collections.Generic;
using Godot;
using Mmo.Client.Core;

namespace Mmo.Client.Godot.Visuals;

// Owns every entity visual and reconciles them against the per-frame render states. Replaces MmoClientRoot's
// inline UpdateEntities loop: on each Sync it acquires a visual for a newly-seen entity (from the factory or
// a pooled, released one), updates the rest, and releases the visuals whose entity dropped out of AOI.
//
// Pooling: a released visual is reset and parked in a per-archetype pool rather than QueueFree-d, so constant
// AOI-boundary churn doesn't thrash skinned-GLB instancing. Reset() releases everything (a future zone change).
//
// Presentation-only: it consumes the computed EntityRenderState list; it owns no game state.
public sealed class EntityRenderer
{
    private readonly Node3D _entityRoot;
    private readonly VisualTuning _tuning;
    private readonly EntityVisualFactory _factory;

    private readonly Dictionary<uint, EntityVisual> _active = [];
    private readonly Dictionary<VisualArchetype, Stack<EntityVisual>> _pools = [];
    private readonly HashSet<uint> _seen = [];
    private readonly List<uint> _stale = [];

    public EntityRenderer(Node3D entityRoot, VisualTuning tuning)
    {
        _entityRoot = entityRoot;
        _tuning = tuning;
        _factory = new EntityVisualFactory(tuning);
    }

    // Per-frame reconcile. `now` is the elapsed-seconds clock the visuals use for the walk-hold latch.
    public void Sync(IReadOnlyList<EntityRenderState> renderStates, double now)
    {
        _seen.Clear();
        for (var i = 0; i < renderStates.Count; i++)
        {
            var state = renderStates[i];
            _seen.Add(state.NetworkId);
            if (!_active.TryGetValue(state.NetworkId, out var visual))
            {
                visual = AcquireVisual(state);
                _active[state.NetworkId] = visual;
            }

            visual.UpdateFrom(state, now);
        }

        _stale.Clear();
        foreach (var (networkId, _) in _active)
        {
            if (!_seen.Contains(networkId))
            {
                _stale.Add(networkId);
            }
        }

        foreach (var networkId in _stale)
        {
            ReleaseVisual(networkId);
        }
    }

    // Push the live label tuning (pixel size + player label height) onto every active visual so an F4-panel
    // apply is visible without a respawn (parity with the old ApplyLabelTuningToExisting).
    public void ApplyLabelTuningToExisting()
    {
        foreach (var visual in _active.Values)
        {
            visual.ApplyLabelTuning();
        }
    }

    // Release-all seam for a future zone change: park every active visual back in its pool, leaving the
    // renderer clean for a rebuild. Wired now even though no zone change drives it yet.
    public void Reset()
    {
        _stale.Clear();
        foreach (var (networkId, _) in _active)
        {
            _stale.Add(networkId);
        }

        foreach (var networkId in _stale)
        {
            ReleaseVisual(networkId);
        }
    }

    private EntityVisual AcquireVisual(EntityRenderState state)
    {
        var archetype = EntityVisualFactory.ChooseArchetype(state);
        if (_pools.TryGetValue(archetype, out var pool) && pool.Count > 0)
        {
            var pooled = pool.Pop();
            _entityRoot.AddChild(pooled);
            pooled.Reset(state);
            return pooled;
        }

        var visual = _factory.Create(archetype, state);
        _entityRoot.AddChild(visual);
        visual.Acquire(state);
        return visual;
    }

    private void ReleaseVisual(uint networkId)
    {
        if (!_active.Remove(networkId, out var visual))
        {
            return;
        }

        // Release detaches the wrapper from the entity root; park it in its archetype's pool for reuse.
        visual.Release();
        if (!_pools.TryGetValue(visual.Archetype, out var pool))
        {
            pool = new Stack<EntityVisual>();
            _pools[visual.Archetype] = pool;
        }

        pool.Push(visual);
    }
}
