using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace GPURayTracing.Tests
{
    public class BvhRegressionTests
    {
        private const int BvhStackSize = 64;
        private const int LeafSize = 4;

        private struct Primitive
        {
            public Vector3 min;
            public Vector3 max;
            public int index;
        }

        private struct Node
        {
            public Vector3 min;
            public Vector3 max;
            public int left;
            public int right;
            public int start;
            public int count;
        }

        [Test]
        public void PerMeshBvh_DeterministicRandomTriangles_MatchesBruteForce()
        {
            var random = new System.Random(4172);
            var triangles = new List<Vector3[]>(96);
            for (int i = 0; i < 96; i++)
            {
                Vector3 center = RandomVector(random, -6.0f, 6.0f);
                Vector3 edge0 = RandomVector(random, -0.8f, 0.8f);
                Vector3 edge1 = RandomVector(random, -0.8f, 0.8f);
                if (Vector3.Cross(edge0, edge1).sqrMagnitude < 0.01f)
                {
                    edge1 += Vector3.up;
                }
                triangles.Add(new[] { center, center + edge0, center + edge1 });
            }

            List<Primitive> primitives = TrianglePrimitives(triangles);
            List<Node> nodes = BuildBvh(primitives, out int maxDepth);
            Assert.That(maxDepth, Is.LessThanOrEqualTo(BvhStackSize));

            for (int i = 0; i < 512; i++)
            {
                Ray ray = RandomRay(random);
                float brute = BruteTriangleHit(ray, triangles);
                float accelerated = TraverseTriangleBvh(ray, triangles, primitives, nodes);
                AssertDistancesEqual(brute, accelerated, $"triangle ray {i}");
            }
        }

        [Test]
        public void TopLevelAndShadowBvhs_DeterministicRandomSpheres_MatchBruteForce()
        {
            var random = new System.Random(90210);
            var centers = new List<Vector3>(128);
            var radii = new List<float>(128);
            var primitives = new List<Primitive>(128);
            for (int i = 0; i < 128; i++)
            {
                Vector3 center = RandomVector(random, -9.0f, 9.0f);
                float radius = Mathf.Lerp(0.1f, 1.2f, (float)random.NextDouble());
                centers.Add(center);
                radii.Add(radius);
                Vector3 extent = Vector3.one * radius;
                primitives.Add(new Primitive { min = center - extent, max = center + extent, index = i });
            }

            var topLevelPrimitives = new List<Primitive>(primitives);
            List<Node> topLevelNodes = BuildBvh(topLevelPrimitives, out int topLevelDepth);
            var shadowPrimitives = primitives.FindAll(primitive => primitive.index % 3 != 0);
            List<Node> shadowNodes = BuildBvh(shadowPrimitives, out int shadowDepth);
            Assert.That(topLevelDepth, Is.LessThanOrEqualTo(BvhStackSize));
            Assert.That(shadowDepth, Is.LessThanOrEqualTo(BvhStackSize));

            for (int i = 0; i < 512; i++)
            {
                Ray ray = RandomRay(random);
                float maxDistance = Mathf.Lerp(1.0f, 30.0f, (float)random.NextDouble());
                AssertDistancesEqual(
                    BruteSphereHit(ray, centers, radii, null, float.PositiveInfinity),
                    TraverseSphereBvh(ray, centers, radii, topLevelPrimitives, topLevelNodes, float.PositiveInfinity),
                    $"top-level ray {i}");

                Predicate<int> castsShadow = index => index % 3 != 0;
                bool bruteBlocked = BruteSphereHit(ray, centers, radii, castsShadow, maxDistance) < maxDistance;
                bool bvhBlocked = TraverseSphereBvh(ray, centers, radii, shadowPrimitives, shadowNodes, maxDistance) < maxDistance;
                Assert.That(bvhBlocked, Is.EqualTo(bruteBlocked), $"shadow ray {i}");
            }
        }

        private static List<Node> BuildBvh(List<Primitive> primitives, out int maxDepth)
        {
            var nodes = new List<Node>();
            maxDepth = 0;
            BuildNode(primitives, nodes, 0, primitives.Count, 1, ref maxDepth);
            return nodes;
        }

        private static int BuildNode(List<Primitive> primitives, List<Node> nodes, int start, int count, int depth, ref int maxDepth)
        {
            if (depth > BvhStackSize)
            {
                throw new InvalidOperationException("BVH exceeds traversal stack capacity.");
            }
            maxDepth = Mathf.Max(maxDepth, depth);

            Vector3 min = primitives[start].min;
            Vector3 max = primitives[start].max;
            for (int i = start + 1; i < start + count; i++)
            {
                min = Vector3.Min(min, primitives[i].min);
                max = Vector3.Max(max, primitives[i].max);
            }

            int nodeIndex = nodes.Count;
            nodes.Add(new Node { min = min, max = max, left = -1, right = -1, start = start, count = count });
            if (count <= LeafSize)
            {
                return nodeIndex;
            }

            Vector3 size = max - min;
            int axis = size.x >= size.y && size.x >= size.z ? 0 : (size.y >= size.z ? 1 : 2);
            primitives.Sort(start, count, Comparer<Primitive>.Create((a, b) =>
                ((a.min + a.max) * 0.5f)[axis].CompareTo(((b.min + b.max) * 0.5f)[axis])));
            int leftCount = count / 2;
            int left = BuildNode(primitives, nodes, start, leftCount, depth + 1, ref maxDepth);
            int right = BuildNode(primitives, nodes, start + leftCount, count - leftCount, depth + 1, ref maxDepth);
            nodes[nodeIndex] = new Node { min = min, max = max, left = left, right = right, start = -1, count = 0 };
            return nodeIndex;
        }

        private static List<Primitive> TrianglePrimitives(List<Vector3[]> triangles)
        {
            var primitives = new List<Primitive>(triangles.Count);
            for (int i = 0; i < triangles.Count; i++)
            {
                Vector3 min = Vector3.Min(triangles[i][0], Vector3.Min(triangles[i][1], triangles[i][2]));
                Vector3 max = Vector3.Max(triangles[i][0], Vector3.Max(triangles[i][1], triangles[i][2]));
                primitives.Add(new Primitive { min = min, max = max, index = i });
            }
            return primitives;
        }

        private static float TraverseTriangleBvh(Ray ray, List<Vector3[]> triangles, List<Primitive> primitives, List<Node> nodes)
        {
            float nearest = float.PositiveInfinity;
            var stack = new int[BvhStackSize];
            int stackCount = 0;
            stack[stackCount++] = 0;
            while (stackCount > 0)
            {
                Node node = nodes[stack[--stackCount]];
                if (!IntersectAabb(ray, node.min, node.max, nearest)) continue;
                if (node.count > 0)
                {
                    for (int i = 0; i < node.count; i++)
                    {
                        float hit = IntersectTriangle(ray, triangles[primitives[node.start + i].index]);
                        if (hit > 0.0f && hit < nearest) nearest = hit;
                    }
                }
                else
                {
                    stack[stackCount++] = node.left;
                    stack[stackCount++] = node.right;
                }
            }
            return nearest;
        }

        private static float TraverseSphereBvh(Ray ray, List<Vector3> centers, List<float> radii, List<Primitive> primitives, List<Node> nodes, float maxDistance)
        {
            float nearest = maxDistance;
            var stack = new int[BvhStackSize];
            int stackCount = 0;
            stack[stackCount++] = 0;
            while (stackCount > 0)
            {
                Node node = nodes[stack[--stackCount]];
                if (!IntersectAabb(ray, node.min, node.max, nearest)) continue;
                if (node.count > 0)
                {
                    for (int i = 0; i < node.count; i++)
                    {
                        int index = primitives[node.start + i].index;
                        float hit = IntersectSphere(ray, centers[index], radii[index]);
                        if (hit > 0.0f && hit < nearest) nearest = hit;
                    }
                }
                else
                {
                    stack[stackCount++] = node.left;
                    stack[stackCount++] = node.right;
                }
            }
            return nearest;
        }

        private static float BruteTriangleHit(Ray ray, List<Vector3[]> triangles)
        {
            float nearest = float.PositiveInfinity;
            foreach (Vector3[] triangle in triangles)
            {
                float hit = IntersectTriangle(ray, triangle);
                if (hit > 0.0f && hit < nearest) nearest = hit;
            }
            return nearest;
        }

        private static float BruteSphereHit(Ray ray, List<Vector3> centers, List<float> radii, Predicate<int> include, float maxDistance)
        {
            float nearest = maxDistance;
            for (int i = 0; i < centers.Count; i++)
            {
                if (include != null && !include(i)) continue;
                float hit = IntersectSphere(ray, centers[i], radii[i]);
                if (hit > 0.0f && hit < nearest) nearest = hit;
            }
            return nearest;
        }

        private static float IntersectSphere(Ray ray, Vector3 center, float radius)
        {
            Vector3 offset = center - ray.origin;
            float projected = Vector3.Dot(ray.direction, offset);
            float discriminant = projected * projected - Vector3.Dot(offset, offset) + radius * radius;
            if (discriminant < 0.0f) return -1.0f;
            float root = Mathf.Sqrt(discriminant);
            float near = projected - root;
            float far = projected + root;
            return near > 0.001f ? near : (far > 0.001f ? far : -1.0f);
        }

        private static float IntersectTriangle(Ray ray, Vector3[] triangle)
        {
            Vector3 edge0 = triangle[1] - triangle[0];
            Vector3 edge1 = triangle[2] - triangle[0];
            Vector3 p = Vector3.Cross(ray.direction, edge1);
            float determinant = Vector3.Dot(edge0, p);
            if (Mathf.Abs(determinant) < 0.000001f) return -1.0f;
            float inverse = 1.0f / determinant;
            Vector3 t = ray.origin - triangle[0];
            float u = Vector3.Dot(t, p) * inverse;
            if (u < 0.0f || u > 1.0f) return -1.0f;
            Vector3 q = Vector3.Cross(t, edge0);
            float v = Vector3.Dot(ray.direction, q) * inverse;
            if (v < 0.0f || u + v > 1.0f) return -1.0f;
            float distance = Vector3.Dot(edge1, q) * inverse;
            return distance > 0.001f ? distance : -1.0f;
        }

        private static bool IntersectAabb(Ray ray, Vector3 min, Vector3 max, float maxDistance)
        {
            float entry = float.NegativeInfinity;
            float exit = float.PositiveInfinity;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Mathf.Abs(ray.direction[axis]) < 0.000001f)
                {
                    if (ray.origin[axis] < min[axis] || ray.origin[axis] > max[axis]) return false;
                    continue;
                }
                float a = (min[axis] - ray.origin[axis]) / ray.direction[axis];
                float b = (max[axis] - ray.origin[axis]) / ray.direction[axis];
                entry = Mathf.Max(entry, Mathf.Min(a, b));
                exit = Mathf.Min(exit, Mathf.Max(a, b));
            }
            return exit >= Mathf.Max(0.0f, entry) && entry < maxDistance;
        }

        private static Ray RandomRay(System.Random random)
        {
            Vector3 direction = RandomVector(random, -1.0f, 1.0f);
            if (direction.sqrMagnitude < 0.001f) direction = Vector3.forward;
            return new Ray(RandomVector(random, -12.0f, 12.0f), direction.normalized);
        }

        private static Vector3 RandomVector(System.Random random, float min, float max)
        {
            return new Vector3(RandomFloat(random, min, max), RandomFloat(random, min, max), RandomFloat(random, min, max));
        }

        private static float RandomFloat(System.Random random, float min, float max)
        {
            return Mathf.Lerp(min, max, (float)random.NextDouble());
        }

        private static void AssertDistancesEqual(float expected, float actual, string message)
        {
            if (float.IsPositiveInfinity(expected))
            {
                Assert.That(float.IsPositiveInfinity(actual), Is.True, message);
            }
            else
            {
                Assert.That(actual, Is.EqualTo(expected).Within(0.0001f), message);
            }
        }
    }
}
