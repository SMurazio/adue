# N — Minimap bake cost at 384×384 (M2 audit findings)

M2's minimap audit (review-request-terrain-painter-m2.md): per-frame cost is CLEAN (O(1) player +
O(AOI) objects — leave it alone). Two BUILD-time findings to fix when the 384 map is live:

1. **The bake scales tiles×scale² with per-pixel SetPixel/GetPixel interop** (~5M calls at 384) and
   re-fires on EVERY zoom click. Fix: bake once per zone into an Image at max resolution and rescale
   with Image.Resize on zoom (or draw from a cached base image), eliminating per-zoom re-bakes; batch
   pixel writes via raw data array instead of per-pixel calls if still slow.
2. **The base layer still reads terrain.png** — meaningless on authored (genVersion 2) zones. Feed it
   the AuthoredMap SurfaceCategory palette instead (same colors as AuthoredSurfaceVisuals so minimap
   matches the ground truth the player walks on).

Also carry ONE M2 verification item into M4's live check: the MultiMesh.Buffer float ORDER
(rows-vs-columns of the transform) is pinned by a headless test but needs one live genVersion-1 launch
to confirm the floor/walls render identically to pre-M2 (implementer-flagged highest risk; a wrong
order is instantly visible as garbled geometry).

Standard band; client-only.
