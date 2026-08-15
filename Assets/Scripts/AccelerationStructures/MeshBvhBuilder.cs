using System;
using System.Collections.Generic;
using PathTracing.Shapes;
using UnityEngine;

namespace PathTracing.AccelerationStructures
{
    public static class MeshBvhBuilder
    {
        public static MeshBvhTemplate Build(Mesh mesh, bool interpolateNormals, int leafTriangleCount, int stackSize, float boundsPadding)
        {
            var vertices = mesh.vertices;
            var uvs = mesh.uv;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            bool useInterpolatedNormals = interpolateNormals && normals.Length == vertices.Length;
            bool hasTangents = tangents.Length == vertices.Length;
            var sourceTriangles = new List<Triangle>();

            for (int submeshIndex = 0; submeshIndex < mesh.subMeshCount; submeshIndex++)
            {
                if (mesh.GetTopology(submeshIndex) != MeshTopology.Triangles)
                {
                    continue;
                }

                int[] indices = mesh.GetIndices(submeshIndex);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int index0 = indices[i];
                    int index1 = indices[i + 1];
                    int index2 = indices[i + 2];
                    Vector3 vertex0 = vertices[index0];
                    Vector3 vertex1 = vertices[index1];
                    Vector3 vertex2 = vertices[index2];
                    Vector3 normal = Vector3.Cross(vertex1 - vertex0, vertex2 - vertex0).normalized;
                    Vector3 normal0 = useInterpolatedNormals ? normals[index0].normalized : normal;
                    Vector3 normal1 = useInterpolatedNormals ? normals[index1].normalized : normal;
                    Vector3 normal2 = useInterpolatedNormals ? normals[index2].normalized : normal;
                    sourceTriangles.Add(new Triangle
                    {
                        vertex0 = vertex0,
                        vertex1 = vertex1,
                        vertex2 = vertex2,
                        normal = normal,
                        normal0 = normal0,
                        normal1 = normal1,
                        normal2 = normal2,
                        tangent0 = GetLocalTangent(tangents, index0, normal0, hasTangents),
                        tangent1 = GetLocalTangent(tangents, index1, normal1, hasTangents),
                        tangent2 = GetLocalTangent(tangents, index2, normal2, hasTangents),
                        uv0 = GetUv(uvs, index0),
                        uv1 = GetUv(uvs, index1),
                        uv2 = GetUv(uvs, index2),
                        interpolateNormals = useInterpolatedNormals ? 1 : 0
                    });
                }
            }

            var template = new MeshBvhTemplate();
            if (sourceTriangles.Count > 0)
            {
                BuildNode(sourceTriangles, template.triangles, template.nodes, 0, sourceTriangles.Count,
                    leafTriangleCount, stackSize, boundsPadding);
            }

            return template;
        }

        private static int BuildNode(List<Triangle> source, List<Triangle> outputTriangles, List<BvhNode> outputNodes,
            int start, int count, int leafTriangleCount, int stackSize, float boundsPadding, int depth = 1)
        {
            if (depth > stackSize)
            {
                throw new InvalidOperationException($"Mesh BVH depth {depth} exceeds traversal stack capacity {stackSize}.");
            }

            int nodeIndex = outputNodes.Count;
            Vector3 boundsMin = GetBoundsMin(source[start]);
            Vector3 boundsMax = GetBoundsMax(source[start]);
            for (int i = start + 1; i < start + count; i++)
            {
                boundsMin = Vector3.Min(boundsMin, GetBoundsMin(source[i]));
                boundsMax = Vector3.Max(boundsMax, GetBoundsMax(source[i]));
            }

            Vector3 padding = Vector3.one * boundsPadding;
            boundsMin -= padding;
            boundsMax += padding;
            outputNodes.Add(new BvhNode { boundsMin = boundsMin, boundsMax = boundsMax, leftChildIndex = -1, rightChildIndex = -1, triangleStart = -1 });

            if (count <= leafTriangleCount)
            {
                int triangleStart = outputTriangles.Count;
                for (int i = start; i < start + count; i++) outputTriangles.Add(source[i]);
                outputNodes[nodeIndex] = new BvhNode { boundsMin = boundsMin, boundsMax = boundsMax, leftChildIndex = -1, rightChildIndex = -1, triangleStart = triangleStart, triangleCount = count };
                return nodeIndex;
            }

            int axis = GetLongestAxis(boundsMax - boundsMin);
            source.Sort(start, count, Comparer<Triangle>.Create((a, b) => GetCentroid(a)[axis].CompareTo(GetCentroid(b)[axis])));
            int leftCount = count / 2;
            int left = BuildNode(source, outputTriangles, outputNodes, start, leftCount, leafTriangleCount, stackSize, boundsPadding, depth + 1);
            int right = BuildNode(source, outputTriangles, outputNodes, start + leftCount, count - leftCount, leafTriangleCount, stackSize, boundsPadding, depth + 1);
            outputNodes[nodeIndex] = new BvhNode { boundsMin = boundsMin, boundsMax = boundsMax, leftChildIndex = left, rightChildIndex = right, triangleStart = -1 };
            return nodeIndex;
        }

        private static Vector4 GetLocalTangent(Vector4[] tangents, int index, Vector3 normal, bool hasTangents)
        {
            if (!hasTangents) return Vector4.zero;
            Vector4 source = tangents[index];
            Vector3 tangent = Vector3.ProjectOnPlane(new Vector3(source.x, source.y, source.z), normal).normalized;
            return new Vector4(tangent.x, tangent.y, tangent.z, source.w < 0.0f ? -1.0f : 1.0f);
        }

        private static Vector2 GetUv(Vector2[] uvs, int index) => uvs != null && index >= 0 && index < uvs.Length ? uvs[index] : Vector2.zero;
        private static Vector3 GetCentroid(Triangle triangle) => (triangle.vertex0 + triangle.vertex1 + triangle.vertex2) / 3.0f;
        private static Vector3 GetBoundsMin(Triangle triangle) => Vector3.Min(triangle.vertex0, Vector3.Min(triangle.vertex1, triangle.vertex2));
        private static Vector3 GetBoundsMax(Triangle triangle) => Vector3.Max(triangle.vertex0, Vector3.Max(triangle.vertex1, triangle.vertex2));

        private static int GetLongestAxis(Vector3 size)
        {
            if (size.x >= size.y && size.x >= size.z) return 0;
            return size.y >= size.z ? 1 : 2;
        }
    }

    public sealed class TopLevelBvhBuilder
    {
        private readonly TopLevelBvhBuildItemComparer _comparer = new();
        private float[] _suffixAreas = Array.Empty<float>();

        public void Build(List<TopLevelBvhBuildItem> items, List<TopLevelBvhNode> nodes, int stackSize)
        {
            nodes.Clear();
            if (items.Count > 0) BuildNode(items, nodes, 0, items.Count, 1, stackSize);
        }

        private int BuildNode(List<TopLevelBvhBuildItem> items, List<TopLevelBvhNode> nodes, int start, int count, int depth, int stackSize)
        {
            if (depth > stackSize) throw new InvalidOperationException($"Top-level BVH depth {depth} exceeds traversal stack capacity {stackSize}.");
            int nodeIndex = nodes.Count;
            Vector3 min = items[start].boundsMin;
            Vector3 max = items[start].boundsMax;
            for (int i = start + 1; i < start + count; i++) { min = Vector3.Min(min, items[i].boundsMin); max = Vector3.Max(max, items[i].boundsMax); }
            nodes.Add(new TopLevelBvhNode { boundsMin = min, boundsMax = max, leftChildIndex = -1, rightChildIndex = -1, objectType = -1, objectIndex = -1 });
            if (count == 1)
            {
                nodes[nodeIndex] = new TopLevelBvhNode { boundsMin = min, boundsMax = max, leftChildIndex = -1, rightChildIndex = -1, objectType = items[start].objectType, objectIndex = items[start].objectIndex };
                return nodeIndex;
            }
            int leftCount = ClampSplit(FindSahSplit(items, start, count), count, depth, stackSize);
            int left = BuildNode(items, nodes, start, leftCount, depth + 1, stackSize);
            int right = BuildNode(items, nodes, start + leftCount, count - leftCount, depth + 1, stackSize);
            nodes[nodeIndex] = new TopLevelBvhNode { boundsMin = min, boundsMax = max, leftChildIndex = left, rightChildIndex = right, objectType = -1, objectIndex = -1 };
            return nodeIndex;
        }

        private int FindSahSplit(List<TopLevelBvhBuildItem> items, int start, int count)
        {
            int bestAxis = -1;
            int bestSplit = count / 2;
            float bestCost = float.MaxValue;
            if (_suffixAreas.Length < count) _suffixAreas = new float[count];
            for (int axis = 0; axis < 3; axis++)
            {
                _comparer.Axis = axis;
                items.Sort(start, count, _comparer);
                Vector3 suffixMin = items[start + count - 1].boundsMin;
                Vector3 suffixMax = items[start + count - 1].boundsMax;
                _suffixAreas[count - 1] = HalfSurfaceArea(suffixMax - suffixMin);
                for (int i = count - 2; i >= 0; i--)
                {
                    suffixMin = Vector3.Min(suffixMin, items[start + i].boundsMin);
                    suffixMax = Vector3.Max(suffixMax, items[start + i].boundsMax);
                    _suffixAreas[i] = HalfSurfaceArea(suffixMax - suffixMin);
                }
                Vector3 prefixMin = items[start].boundsMin;
                Vector3 prefixMax = items[start].boundsMax;
                for (int leftCount = 1; leftCount < count; leftCount++)
                {
                    float cost = HalfSurfaceArea(prefixMax - prefixMin) * leftCount + _suffixAreas[leftCount] * (count - leftCount);
                    if (cost < bestCost) { bestCost = cost; bestAxis = axis; bestSplit = leftCount; }
                    prefixMin = Vector3.Min(prefixMin, items[start + leftCount].boundsMin);
                    prefixMax = Vector3.Max(prefixMax, items[start + leftCount].boundsMax);
                }
            }
            _comparer.Axis = bestAxis >= 0 ? bestAxis : GetLongestAxis(items[start].boundsMax - items[start].boundsMin);
            items.Sort(start, count, _comparer);
            return Mathf.Clamp(bestSplit, 1, count - 1);
        }

        private static int ClampSplit(int leftCount, int count, int depth, int stackSize)
        {
            int remainingDepth = stackSize - depth - 1;
            int maxChildCount = remainingDepth >= 30 ? int.MaxValue : 1 << remainingDepth;
            return Mathf.Clamp(leftCount, Mathf.Max(1, count - maxChildCount), Mathf.Min(count - 1, maxChildCount));
        }

        private static float HalfSurfaceArea(Vector3 size)
        {
            if (size.x <= 0f && size.y <= 0f && size.z <= 0f) return 0f;
            float x = Mathf.Max(0f, size.x); float y = Mathf.Max(0f, size.y); float z = Mathf.Max(0f, size.z);
            return x * y + y * z + z * x;
        }

        private static int GetLongestAxis(Vector3 size) => size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
    }
}
