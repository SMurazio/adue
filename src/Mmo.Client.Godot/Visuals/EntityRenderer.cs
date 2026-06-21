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
            if (_active.TryGetValue(state.NetworkId, out var visual))
            {
                // Self-heal a recycled NetworkId. Under packet loss a despawn can be dropped, leaving a stale
                // visual parked under a NetworkId the server then RECYCLES (the NetworkIdPool reuses freed ids)
                // for a DIFFERENT entity — e.g. a resource landing on a departed player's id would otherwise
                // keep showing the player's cat. If the archetype the entity now needs no longer matches the
                // parked visual, release it and acquire the right one. A stable entity yields the same archetype
                // every frame, so this never rebuilds in normal operation (only on a genuine id reuse / toggle).
                var want = EntityVisualFactory.ChooseArchetype(state, _tuning.DebugFacingBox, _tuning.DebugCatoSprite);
                if (visual.Archetype != want)
                {
                    ReleaseVisual(state.NetworkId);
                    visual = AcquireVisual(state);
                    _active[state.NetworkId] = visual;
                }
            }
            else
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

    // S65: push the live per-archetype model scale (rock / tree / plant) onto every active visual so an F5-panel
    // apply is visible instantly without a respawn. Pooled (parked) visuals re-read the scale on their next
    // acquire, so this only needs to walk the active set.
    public void ApplyModelScaleToExisting()
    {
        foreach (var visual in _active.Values)
        {
            visual.ApplyModelScale();
        }
    }

    // S99: push the live Cato placement (pixel size + X/Y offset) onto every active visual so an F5-panel apply
    // resizes/moves the spawned Cato sprites instantly without a respawn. Only CatoSpriteVisual reacts; the rest
    // no-op. Pooled (parked) visuals re-seed from Tuning on their next acquire, so this only walks the active set.
    public void ApplyCatoPlacementToExisting()
    {
        foreach (var visual in _active.Values)
        {
            visual.ApplyCatoPlacement();
        }
    }

    // S73/S96: an F5 player-visual toggle flipped (Debug facing box or Cato sprite); rebuild every already-spawned
    // player so the swap (model rig <-> debug box+arrow <-> Cato sprite) is immediate, not just for future spawns. Release the
    // active player/debug-box visuals (parking them in their archetype pool) and clear them from the active set;
    // the next Sync re-acquires each from the factory/pool under the NEW flag, so they reappear in the chosen
    // form on the very next frame. Resources/NPCs are untouched.
    public void RebuildPlayerVisuals()
    {
        _stale.Clear();
        foreach (var (networkId, visual) in _active)
        {
            if (visual.Archetype is VisualArchetype.Player or VisualArchetype.DebugFacingBox or VisualArchetype.CatoSprite)
            {
                _stale.Add(networkId);
            }
        }

        foreach (var networkId in _stale)
        {
            ReleaseVisual(networkId);
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
        var archetype = EntityVisualFactory.ChooseArchetype(state, _tuning.DebugFacingBox, _tuning.DebugCatoSprite);
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
