# Shader Intersections And BVH

This document covers intersection flow and acceleration structures in `Assets/Scripts/RayTracingCompute.compute`.

## Intersection Flow

`GetNearestIntersection()` checks:

1. `IntersectGroundPlane()` for an infinite plane at world `y = 0`.
2. The top-level object BVH in `_TopLevelBvhNodes`, when the object count is high enough to justify it.
3. Flat sphere/light/mesh loops when the top-level BVH is disabled for small scenes.
4. Intersected sphere or light leaves directly.
5. Intersected mesh leaves through that mesh's per-mesh BVH nodes and leaf triangles.

Triangle meshes use a per-mesh AABB plus BVH traversal, so rays can skip whole meshes and large triangle groups before running expensive triangle tests.

## Per-Mesh BVH

Registered triangle meshes upload world-space triangles into `_Triangles`, object metadata into `_Meshes`, and per-mesh BVH nodes into `_BvhNodes`.

Each mesh has an object-level AABB in `_Meshes`. Once a ray enters a mesh, traversal walks that mesh's binary BVH and tests only leaf triangle ranges that survive AABB checks.

All BVHs in this project (per-mesh, top-level, and shadow) are built CPU-side with a surface area heuristic (SAH) split: for each axis the build sorts items by centroid, sweeps every candidate split, and scores it as `SA(left) * leftCount + SA(right) * rightCount`, choosing the lowest-cost split across all three axes. `HalfSurfaceArea` provides the SA term, `FindTriangleSahSplit` drives per-mesh splits, and `FindTopLevelSahSplit` drives top-level/shadow splits; both fall back to a longest-axis median split if no positive-area split is found, and reuse a shared `_sahSuffixArea` scratch buffer (grown via `EnsureSahScratch`) to avoid per-node allocations. Leaf nodes hold up to `BvhLeafTriangleCount` (4) triangles for per-mesh BVHs.

Traversal visits children near-first: each `IntersectAabbInverse` returns the AABB entry distance, and the traversal pushes the farther child first so the nearer child is popped and traversed first. A closer hit shrinks `bestHit.distance`, so the farther child's later AABB test fails and its subtree is skipped. `IntersectAabbInverse` also takes a precomputed inverse ray direction so each traversal computes the 3 reciprocals once per ray instead of once per node; `IntersectAabb` is a thin wrapper that computes the inverse and discards the entry distance. The traversal stack is fixed at 64 entries.

## Top-Level BVH

The scene uploads a top-level BVH over ray-traced spheres, emissive light spheres, and registered mesh AABBs. First-hit traversal uses this BVH to skip groups of objects before reaching object-specific tests.

The top-level BVH has traversal overhead, so small scenes can be faster with flat loops. It is best evaluated in high-object-count scenes such as `Benchmark_ManySpheres` and `Benchmark_ManyMeshes`.

## Shadow BVH

Shadow traversal uses a separate shadow-only BVH over regular spheres and mesh AABBs, excluding light spheres because lights are not shadow blockers.

Shadow rays traverse the shadow-only blocker BVH when enabled, or flat-loop blockers for small scenes. They test blockers against regular sphere and mesh leaves, but not light leaves. Opaque blockers early-out immediately, while transparent blockers use the nearest transparent hit before the light distance to tint transmitted shadow light.

When the scene has no transparent shadow blockers, `GetShadowTransmittance()` takes a cheaper pure-occlusion fast path through `IsShadowRayBlocked()`: a boolean traversal that returns black on the first opaque blocker and white otherwise, using `SphereOccludes()` and `MeshBvhOccludes()` (which avoid building a `RayHit` per leaf and skip the nearest-transparent-blocker bookkeeping). The scene-level flag is uploaded from C# as `_HasTransparentShadowBlockers`; `GameManager` recomputes it each frame from regular sphere opacity (`UpdateSpheres`) and mesh material opacity (`UpdateMeshChangeCache`), treating opacity `< 1` as transparent. Lights are excluded because they are not shadow blockers.

Profiling with `Benchmark_ShadowBlockers` showed that this shadow-only BVH can improve shadow-heavy workloads when forced on with `shadowBvhMinObjectCount = 0`; setting the threshold above the blocker count forces the flat path for comparison.

## Runtime Thresholds

`topLevelBvhMinObjectCount` and `shadowBvhMinObjectCount` control whether each top-level structure is used. If the relevant object count is below the threshold, the matching shader node count is uploaded as `0`, and the shader uses lower-overhead flat object loops.

Set a threshold to `0` to force that BVH on. Set it above the relevant object/blocker count to force flat loops.
