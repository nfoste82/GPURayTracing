using NUnit.Framework;
using UnityEngine;

namespace GPURayTracing.Tests
{
    public class RayMathRegressionTests
    {
        private const float Epsilon = 0.00001f;

        [Test]
        public void SphereIntersection_OutsideRay_ReturnsNearSurface()
        {
            float distance = IntersectSphere(Vector3.zero, Vector3.forward, new Vector3(0.0f, 0.0f, 5.0f), 1.0f);

            Assert.That(distance, Is.EqualTo(4.0f).Within(Epsilon));
        }

        [Test]
        public void SphereIntersection_InsideRay_ReturnsExitSurface()
        {
            float distance = IntersectSphere(Vector3.zero, Vector3.right, Vector3.zero, 2.0f);

            Assert.That(distance, Is.EqualTo(2.0f).Within(Epsilon));
        }

        [Test]
        public void SphereIntersection_OutsideRayPointingAway_Misses()
        {
            float distance = IntersectSphere(Vector3.zero, Vector3.back, new Vector3(0.0f, 0.0f, 5.0f), 1.0f);

            Assert.That(distance, Is.LessThan(0.0f));
        }

        [Test]
        public void TriangleIntersection_InterpolatesExpectedBarycentricCoordinates()
        {
            bool hit = IntersectTriangle(
                new Vector3(0.25f, 0.25f, -1.0f),
                Vector3.forward,
                Vector3.zero,
                Vector3.right,
                Vector3.up,
                out float distance,
                out Vector2 barycentric);

            Assert.That(hit, Is.True);
            Assert.That(distance, Is.EqualTo(1.0f).Within(Epsilon));
            Assert.That(barycentric.x, Is.EqualTo(0.25f).Within(Epsilon));
            Assert.That(barycentric.y, Is.EqualTo(0.25f).Within(Epsilon));
        }

        [Test]
        public void TriangleIntersection_OutsideTriangle_Misses()
        {
            bool hit = IntersectTriangle(
                new Vector3(0.75f, 0.75f, -1.0f),
                Vector3.forward,
                Vector3.zero,
                Vector3.right,
                Vector3.up,
                out _,
                out _);

            Assert.That(hit, Is.False);
        }

        [Test]
        public void AabbIntersection_AxisAlignedRay_HitsAtExpectedDistance()
        {
            bool hit = IntersectAabb(
                new Vector3(0.0f, 0.0f, -2.0f),
                Vector3.forward,
                -Vector3.one,
                Vector3.one,
                100.0f,
                out float entryDistance);

            Assert.That(hit, Is.True);
            Assert.That(entryDistance, Is.EqualTo(1.0f).Within(Epsilon));
        }

        [Test]
        public void AabbIntersection_ParallelRayOutsideSlab_Misses()
        {
            bool hit = IntersectAabb(
                new Vector3(2.0f, 0.0f, -2.0f),
                Vector3.forward,
                -Vector3.one,
                Vector3.one,
                100.0f,
                out _);

            Assert.That(hit, Is.False);
        }

        [Test]
        public void Reflection_AtFortyFiveDegrees_PreservesAngle()
        {
            Vector3 incident = new Vector3(1.0f, -1.0f, 0.0f).normalized;
            Vector3 reflected = Vector3.Reflect(incident, Vector3.up);

            AssertVector(reflected, new Vector3(0.70710677f, 0.70710677f, 0.0f));
            Assert.That(Vector3.Angle(-incident, Vector3.up), Is.EqualTo(Vector3.Angle(reflected, Vector3.up)).Within(0.0001f));
        }

        [Test]
        public void Refraction_AirToGlassAtFortyFiveDegrees_MatchesCurrentSnellBehavior()
        {
            Vector3 incident = new Vector3(1.0f, -1.0f, 0.0f).normalized;

            bool refracted = RefractSnell(incident, 1.0f, 1.5f, Vector3.up, out Vector3 direction);

            Assert.That(refracted, Is.True);
            AssertVector(direction, new Vector3(0.47140452f, -0.8819171f, 0.0f));
        }

        [Test]
        public void Refraction_GlassToAirAboveCriticalAngle_ReportsTotalInternalReflection()
        {
            Vector3 incident = new Vector3(0.8660254f, 0.5f, 0.0f).normalized;

            bool refracted = RefractSnell(incident, 1.5f, 1.0f, Vector3.down, out Vector3 direction);

            Assert.That(refracted, Is.False);
            AssertVector(direction, Vector3.zero);
            AssertVector(Vector3.Reflect(incident, Vector3.up), new Vector3(0.8660254f, -0.5f, 0.0f));
        }

        [Test]
        public void SchlickFresnel_CurrentGlassBaseline_IsStable()
        {
            float normalIncidence = GetFresnelReflectance(Vector3.down, Vector3.up, 1.5f);
            float fortyFiveDegrees = GetFresnelReflectance(new Vector3(1.0f, -1.0f, 0.0f).normalized, Vector3.up, 1.5f);
            float grazing = GetFresnelReflectance(Vector3.right, Vector3.up, 1.5f);

            Assert.That(normalIncidence, Is.EqualTo(0.04f).Within(Epsilon));
            Assert.That(fortyFiveDegrees, Is.EqualTo(0.042069275f).Within(Epsilon));
            Assert.That(grazing, Is.EqualTo(1.0f).Within(Epsilon));
        }

        [Test]
        public void GlassAbsorption_CurrentApproximationBaseline_IsStable()
        {
            Vector3 transmittance = GetAbsorptionTransmittance(new Vector3(0.25f, 0.5f, 0.75f), 0.6f, 2.0f);

            AssertVector(transmittance, new Vector3(0.17212175f, 0.39543194f, 0.64325213f), 0.00002f);
        }

        private static float IntersectSphere(Vector3 rayOrigin, Vector3 rayDirection, Vector3 center, float radius)
        {
            Vector3 direction = rayDirection.normalized;
            Vector3 d = center - rayOrigin;
            float p1 = Vector3.Dot(direction, d);
            float radiusSquared = radius * radius;
            float distanceToCenterSquared = Vector3.Dot(d, d);
            if (p1 < 0.0f && distanceToCenterSquared > radiusSquared)
            {
                return -1.0f;
            }

            float p2Squared = p1 * p1 - distanceToCenterSquared + radiusSquared;
            if (p2Squared < 0.0f)
            {
                return -1.0f;
            }

            float p2 = Mathf.Sqrt(p2Squared);
            float distance = p1 - p2 > 0.0f ? p1 - p2 : p1 + p2;
            return distance > 0.0f ? distance : -1.0f;
        }

        private static bool IntersectTriangle(
            Vector3 rayOrigin,
            Vector3 rayDirection,
            Vector3 vertex0,
            Vector3 vertex1,
            Vector3 vertex2,
            out float hitDistance,
            out Vector2 barycentric)
        {
            Vector3 edge1 = vertex1 - vertex0;
            Vector3 edge2 = vertex2 - vertex0;
            Vector3 p = Vector3.Cross(rayDirection, edge2);
            float determinant = Vector3.Dot(edge1, p);
            hitDistance = float.PositiveInfinity;
            barycentric = Vector2.zero;
            if (Mathf.Abs(determinant) < 0.000001f)
            {
                return false;
            }

            float inverseDeterminant = 1.0f / determinant;
            Vector3 t = rayOrigin - vertex0;
            float u = Vector3.Dot(t, p) * inverseDeterminant;
            if (u < 0.0f || u > 1.0f)
            {
                return false;
            }

            Vector3 q = Vector3.Cross(t, edge1);
            float v = Vector3.Dot(rayDirection, q) * inverseDeterminant;
            if (v < 0.0f || u + v > 1.0f)
            {
                return false;
            }

            hitDistance = Vector3.Dot(edge2, q) * inverseDeterminant;
            barycentric = new Vector2(u, v);
            return hitDistance > 0.001f;
        }

        private static bool IntersectAabb(
            Vector3 rayOrigin,
            Vector3 rayDirection,
            Vector3 boundsMin,
            Vector3 boundsMax,
            float maxDistance,
            out float entryDistance)
        {
            float tMin = float.NegativeInfinity;
            float tMax = float.PositiveInfinity;
            for (int axis = 0; axis < 3; axis++)
            {
                float origin = rayOrigin[axis];
                float direction = rayDirection[axis];
                if (Mathf.Abs(direction) < 0.000001f)
                {
                    if (origin < boundsMin[axis] || origin > boundsMax[axis])
                    {
                        entryDistance = 0.0f;
                        return false;
                    }

                    continue;
                }

                float inverseDirection = 1.0f / direction;
                float near = (boundsMin[axis] - origin) * inverseDirection;
                float far = (boundsMax[axis] - origin) * inverseDirection;
                if (near > far)
                {
                    float swap = near;
                    near = far;
                    far = swap;
                }

                tMin = Mathf.Max(tMin, near);
                tMax = Mathf.Min(tMax, far);
                if (tMax < tMin)
                {
                    entryDistance = 0.0f;
                    return false;
                }
            }

            entryDistance = Mathf.Max(0.0f, tMin);
            return tMax >= entryDistance && tMin < maxDistance;
        }

        private static bool RefractSnell(
            Vector3 sourceDirection,
            float sourceRefraction,
            float targetRefraction,
            Vector3 surfaceNormal,
            out Vector3 refractedDirection)
        {
            Vector3 incident = sourceDirection.normalized;
            Vector3 normal = surfaceNormal.normalized;
            float sourceIndex = Mathf.Max(0.001f, sourceRefraction);
            float targetIndex = Mathf.Max(0.001f, targetRefraction);
            float eta = sourceIndex / targetIndex;
            float cosIncident = Mathf.Clamp(Vector3.Dot(-incident, normal), -1.0f, 1.0f);
            if (cosIncident < 0.0f)
            {
                normal = -normal;
                cosIncident = -cosIncident;
            }

            float sinTransmittedSquared = eta * eta * Mathf.Max(0.0f, 1.0f - cosIncident * cosIncident);
            if (sinTransmittedSquared > 1.0f)
            {
                refractedDirection = Vector3.zero;
                return false;
            }

            float cosTransmitted = Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - sinTransmittedSquared));
            refractedDirection = (eta * incident + (eta * cosIncident - cosTransmitted) * normal).normalized;
            return true;
        }

        private static float GetFresnelReflectance(Vector3 rayDirection, Vector3 normal, float refraction)
        {
            float index = Mathf.Max(1.0f, refraction);
            float r0 = (1.0f - index) / (1.0f + index);
            r0 *= r0;
            float cosTheta = Mathf.Clamp01(Mathf.Abs(Vector3.Dot(-rayDirection.normalized, normal.normalized)));
            return r0 + (1.0f - r0) * Mathf.Pow(1.0f - cosTheta, 5.0f);
        }

        private static Vector3 GetAbsorptionTransmittance(Vector3 filterColor, float opacity, float distanceThroughMedium)
        {
            float densityDistance = Mathf.Max(0.0f, distanceThroughMedium) * Mathf.Clamp01(opacity);
            return new Vector3(
                GetAbsorptionChannel(filterColor.x, densityDistance),
                GetAbsorptionChannel(filterColor.y, densityDistance),
                GetAbsorptionChannel(filterColor.z, densityDistance));
        }

        private static float GetAbsorptionChannel(float filterColor, float densityDistance)
        {
            float spectralFilter = Mathf.Pow(Mathf.Max(Mathf.Clamp01(filterColor), 0.001f), densityDistance);
            float neutralLoss = Mathf.Exp(-0.08f * densityDistance);
            return Mathf.Clamp01(spectralFilter * neutralLoss);
        }

        private static void AssertVector(Vector3 actual, Vector3 expected, float tolerance = Epsilon)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(tolerance), "x");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(tolerance), "y");
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(tolerance), "z");
        }
    }
}
