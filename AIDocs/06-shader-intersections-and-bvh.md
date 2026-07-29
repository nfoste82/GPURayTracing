# Shader Intersections And BVH

This document covers intersection flow and acceleration structures in `Assets/Scripts/RayTracingCompute.compute`.

## Intersection Flow

`GetNearestIntersection()` checks:

1. The finite procedural water volume, when enabled.
2. The top-level object BVH in `_TopLevelBvhNodes`, when the object count is high enough to justify it.
3. Flat sphere/light/mesh loops when the top-level BVH is disabled for small scenes.
4. Intersected sphere or light leaves directly.
5. Intersected mesh leaves through that mesh's per-mesh BVH nodes and leaf triangles.

Triangle meshes use a per-mesh AABB plus BVH traversal, so rays can skip whole meshes and large triangle groups before running expensive triangle tests.

## Per-Mesh BVH

Registered triangle meshes upload world-space triangles into `_Triangles`, object metadata into `_Meshes`, and per-mesh BVH nodes into `_BvhNodes`. `GameManager` builds an object-space BVH template once for each unique Unity `Mesh` and smooth-normal setting. Mesh instances reuse that topology; their triangles and node bounds are transformed into world space without rerunning the split builder. Moving an instance therefore performs linear transformation/upload work rather than rebuilding its BVH.

Each mesh has an object-level AABB in `_Meshes`. Once a ray enters a mesh, traversal walks that mesh's binary BVH and tests only leaf triangle ranges that survive AABB checks.

The `GameManager` inspector exposes `Bake BVH` above its diagnostics. A bake stores object-space per-mesh triangles and BVH nodes in generated assets and reports `Baked`, `Not baked`, or `Bake is out-of-date`. Its signature covers stable mesh asset GUID/local-file identities, smooth-normal topology mode, and source mesh dependency hashes. Runtime resolves those stable identities to the Play-mode mesh instances and populates its instance-ID cache; multiple runtime instances of the same imported mesh can therefore share one baked template. Runtime uses baked templates only when that signature is current; otherwise it falls back to the normal CPU builder. Transforms and material values are intentionally excluded because baked data is object-space and those values are applied while assembling runtime world-space triangle records.

Completing a manual or bake-on-exit operation saves the scene so its hidden bake-asset reference persists across scene reloads and editor sessions. Startup profiling includes a `baked mesh BVH load` phase whose parenthesized status confirms how many templates loaded or states why runtime rejected the bake.

After successfully writing a bake, the editor deletes obsolete metadata and binary bake files whose filename prefix belongs to that scene GUID. It preserves the new bake and any bake still referenced by another `GameManager` in the same scene, and never removes files belonging to another scene GUID.

`Bake upon exit` records whether a bake was current before entering Play mode. When enabled and no current bake existed, the editor preserves the templates built during that Play session as it exits and assigns them after the saved scene is restored. It skips this when the Play-mode mesh signature differs from the pre-Play signature, preventing unsaved Play-mode geometry changes from being paired with the saved scene.

Per-mesh template BVHs use balanced longest-axis median splits. This requires one centroid sort per node, guarantees shallow trees, and is substantially faster to construct than the previous exhaustive SAH builder, which sorted each node on all three axes and swept every candidate split. Top-level and shadow BVHs retain SAH because they contain far fewer objects and are inexpensive to rebuild. `ClampBvhSplitToDepth` constrains top-level SAH splits so no generated tree exceeds the shader's 32-entry traversal stack. Leaf nodes hold up to `BvhLeafTriangleCount` (4) triangles for per-mesh BVHs.

Traversal visits children near-first: each `IntersectAabbInverse` returns the AABB entry distance, and the traversal pushes the farther child first so the nearer child is popped and traversed first. A closer hit shrinks `bestHit.distance`, so the farther child's later AABB test fails and its subtree is skipped. `IntersectAabbInverse` also takes a precomputed inverse ray direction so each traversal computes the 3 reciprocals once per ray instead of once per node; `IntersectAabb` is a thin wrapper that computes the inverse and discards the entry distance. The traversal stack is fixed at 32 entries, matching the CPU builder's enforced maximum depth.

## Top-Level BVH

The scene uploads a top-level BVH over ray-traced spheres, emissive light spheres, and registered mesh AABBs. First-hit traversal uses this BVH to skip groups of objects before reaching object-specific tests.

The top-level BVH has traversal overhead, so small scenes can be faster with flat loops. It is best evaluated in high-object-count scenes such as `Benchmark_ManySpheres` and `Benchmark_ManyMeshes`.

## Shadow BVH

Shadow traversal uses a separate shadow-only BVH over regular spheres and mesh AABBs, excluding light spheres because lights are not shadow blockers.

Shadow rays traverse the shadow-only blocker BVH when enabled, or flat-loop blockers for small scenes. They test blockers against regular sphere and mesh leaves, but not light leaves. Opaque blockers early-out immediately. Transparent queries repeatedly find the nearest boundary before the light, attenuate the segment using the active medium, and update a fixed-capacity medium stack. Closed sphere and mesh blockers therefore use their actual internal ray distance, including properly nested volumes. Mesh boundaries without a paired exit use the explicit thin-surface fallback. Traversal is bounded and returns black if the crossing limit is exhausted.

When the scene has no transparent shadow blockers, `GetShadowTransmittance()` takes a cheaper pure-occlusion fast path through `IsShadowRayBlocked()`: a boolean traversal that returns black on the first opaque blocker and white otherwise, using `SphereOccludes()` and `MeshBvhOccludes()` (which avoid building a `RayHit` per leaf and skip transparent transmittance accumulation). The scene-level flag is uploaded from C# as `_HasTransparentShadowBlockers`; `GameManager` recomputes it each frame from regular sphere opacity (`UpdateSpheres`) and mesh material opacity (`UpdateMeshChangeCache`), treating opacity `< 1` as transparent. Lights are excluded because they are not shadow blockers.

Profiling with `Benchmark_ShadowBlockers` showed that this shadow-only BVH can improve shadow-heavy workloads when forced on with `shadowBvhMinObjectCount = 0`; setting the threshold above the blocker count forces the flat path for comparison.

## Runtime Thresholds

`topLevelBvhMinObjectCount` and `shadowBvhMinObjectCount` control whether each top-level structure is used. If the relevant object count is below the threshold, the matching shader node count is uploaded as `0`, and the shader uses lower-overhead flat object loops.

Set a threshold to `0` to force that BVH on. Set it above the relevant object/blocker count to force flat loops.
