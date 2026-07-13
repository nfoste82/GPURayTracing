using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GPURayTracing.Tests
{
    public class RayTracingComputeRegressionTests
    {
        private const string ComputeShaderPath = "Assets/Scripts/RayTracingCompute.compute";
        private const float Epsilon = 0.0001f;

        [Test]
        public void ProductionShader_ReflectionRefractionAndAbsorptionBaselines_AreStable()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Compute shaders are not supported by the active graphics device.");
            }

            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeShaderPath);
            Assert.That(shader, Is.Not.Null, $"Missing compute shader at {ComputeShaderPath}");
            if (!shader.HasKernel("CSRegressionProbe"))
            {
                Assert.Ignore("The active graphics device did not compile the GPU regression kernel. Run without -nographics to validate GPU probes.");
            }

            int kernel = shader.FindKernel("CSRegressionProbe");
            var buffer = new ComputeBuffer(34, sizeof(float) * 4);
            var sphereBuffer = new ComputeBuffer(1, 56);
            try
            {
                shader.SetInt("_NumSpheres", 0);
                shader.SetInt("_WaterEnabled", 1);
                shader.SetVector("_WaterCenter", Vector4.zero);
                shader.SetVector("_WaterSize", new Vector4(10.0f, 10.0f, 0.0f, 0.0f));
                shader.SetFloat("_WaterDepth", 5.0f);
                shader.SetVector("_WaterColor", new Vector4(0.17f, 0.45f, 0.52f, 0.0f));
                shader.SetFloat("_WaterOpacity", 0.18f);
                shader.SetFloat("_WaterAbsorptionStrength", 0.22f);
                shader.SetFloat("_WaterRefraction", 2.0f);
                shader.SetFloat("_WaterWaveAmplitude", 0.0f);
                shader.SetFloat("_FireflyClamp", 1.0f);
                shader.SetBuffer(kernel, "_Spheres", sphereBuffer);
                shader.SetBuffer(kernel, "RegressionResults", buffer);
                shader.Dispatch(kernel, 1, 1, 1);

                var results = new Vector4[34];
                buffer.GetData(results);

                AssertVector(results[0], new Vector4(0.70710677f, 0.70710677f, 0.0f, 1.0f), "reflection");
                AssertVector(results[1], new Vector4(0.47140452f, -0.8819171f, 0.0f, 1.0f), "air-to-glass refraction");
                AssertVector(results[2], Vector4.zero, "total internal reflection signal");
                AssertVector(results[3], new Vector4(0.042069275f, 0.04f, 0.0f, 1.0f), "Schlick Fresnel");
                AssertVector(results[4], new Vector4(0.17212175f, 0.39543194f, 0.64325213f, 1.0f), "glass absorption", 0.0002f);
                AssertVector(results[5], new Vector4(1.0f, 2.0f, 3.0f, 0.0f), "air-to-water medium transition");
                AssertVector(results[6], new Vector4(2.0f, 1.5f, 1.0f, 7.0f), "water-to-glass medium transition");
                AssertVector(results[7], new Vector4(1.5f, 2.0f, 1.0f, 1.0f), "glass-to-water medium transition");
                AssertVector(results[8], new Vector4(1.5f, 2.0f, 3.0f, 0.0f), "nested stack glass current and water parent");
                AssertVector(results[9], new Vector4(2.0f, 1.0f, 2.0f, 0.0f), "matching glass exit reveals water");
                AssertVector(results[10], new Vector4(1.0f, 0.0f, 1.0f, 0.0f), "matching water exit reveals air");
                AssertVector(results[11], new Vector4(3.0f, 2.0f, 2.0f, 1.0f), "unmatched exit preserves current medium");
                AssertVector(results[12], new Vector4(8.0f, 1.0f, 1.0f, 1.0f), "stack overflow is detectable");
                AssertVector(results[13], new Vector4(3.0f, 2.0f, 2.0f, 0.0f), "underwater path initialization");
                AssertVector(results[14], new Vector4(5.0f, 1.0f, 2.0f, 5.0f), "finite water segment distances", 0.001f);
                AssertVector(results[15], new Vector4(0.6950495f, 0.8005689f, 0.9084640f, 1.0f), "glass active-medium segment", 0.0002f);
                AssertVector(results[16], new Vector4(0.6940578f, 0.7850562f, 0.8096121f, 1.0f), "water active-medium segment", 0.0002f);
                AssertVector(results[17], new Vector4(1.0f, 1.0f, 1.0f, 0.0f), "air segment is neutral");
                AssertVector(results[18], new Vector4(4.0f, 1.0f, 4.0f, 1.0f), "water AABB bottom and side intersections", 0.001f);
                AssertVector(results[19], new Vector4(-1.0f, 0.0f, 0.0f, 1.0f), "water AABB side normal");
                AssertVector(results[20], new Vector4(2.0f, 1.5f, 1.0f, 1.0f), "production water-to-glass transition selection");
                AssertVector(results[21], new Vector4(0.9428090f, -0.3333333f, 0.0f, 0.0225197f), "production water-to-glass direction and Fresnel", 0.0002f);
                AssertVector(results[22], new Vector4(1.5f, 2.0f, 0.0f, 1.0f), "production glass-to-water transition avoids air TIR");
                AssertVector(results[23], new Vector4(0.6495190f, 0.7603453f, 0.0f, 3.0f), "production glass-to-water direction preserves stack until transmission", 0.0002f);
                AssertVector(results[24], new Vector4(1.0f, 1.0f, 0.0f, 0.0f), "overlapping sphere exit keeps active overlap medium");
                AssertVector(results[25], new Vector4(8.0f, 1.0f, 2.0f, 0.0f), "overlapping sphere exit removes non-current medium");
                AssertVector(results[26], new Vector4(0.2953915f, 0.1731606f, 0.1120451f, 0.7957747f), "Lambert and GGX mixture evaluation", 0.0002f);
                AssertVector(results[27], new Vector4(1.0185916f, 0.5092958f, 0.2546479f, 1.2732395f), "GGX metal evaluation", 0.0002f);
                AssertFinitePositiveSample(results[28], results[29]);
                AssertVector(results[30], new Vector4(0.0f, 0.4472136f, 0.8944272f, 1.0f), "interpolated shading and geometric normals", 0.0002f);
                AssertVector(results[31], new Vector4(0.2f, 0.8f, 0.5f, 1.0f), "MIS power heuristic");
                AssertVector(results[32], new Vector4(2.0f, 0.0245556f, 0.0245556f, 0.0245556f), "triangle-light PDF and water F0");
                AssertVector(results[33], new Vector4(1.6999575f, 0.8499787f, 0.4249894f, 1.0f), "firefly luminance clamp", 0.0002f);
            }
            finally
            {
                sphereBuffer.Release();
                buffer.Release();
            }
        }

        [Test]
        public void CausticsDisabled_DoesNotAllocateResourcesOrDispatchPhotonKernels()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            var gameObject = new GameObject("Disabled Caustics Test");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                FieldInfo enabledField = managerType.GetField("enableCaustics");
                PropertyInfo resourcesProperty = managerType.GetProperty("HasCausticResources");
                PropertyInfo dispatchProperty = managerType.GetProperty("CausticDispatchCount");
                MethodInfo updateMethod = managerType.GetMethod(
                    "UpdateCausticPhotonMap",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.That(enabledField, Is.Not.Null);
                Assert.That(resourcesProperty, Is.Not.Null);
                Assert.That(dispatchProperty, Is.Not.Null);
                Assert.That(updateMethod, Is.Not.Null);
                Assert.That(enabledField.GetValue(manager), Is.False, "Caustics must default to disabled");
                Assert.That(resourcesProperty.GetValue(manager), Is.False);
                Assert.That(dispatchProperty.GetValue(manager), Is.EqualTo(0));

                updateMethod.Invoke(manager, null);

                Assert.That(resourcesProperty.GetValue(manager), Is.False);
                Assert.That(dispatchProperty.GetValue(manager), Is.EqualTo(0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void CausticResourceCreation_AllocatesNonEmptyGrid()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            var gameObject = new GameObject("Caustic Grid Resource Test");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                MethodInfo ensureMethod = managerType.GetMethod(
                    "EnsureCausticResources",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo releaseMethod = managerType.GetMethod(
                    "ReleaseCausticResources",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                PropertyInfo resourcesProperty = managerType.GetProperty("HasCausticResources");
                PropertyInfo cellCountProperty = managerType.GetProperty("CausticGridCellCount");

                Assert.That(ensureMethod, Is.Not.Null);
                Assert.That(releaseMethod, Is.Not.Null);
                Assert.That(resourcesProperty, Is.Not.Null);
                Assert.That(cellCountProperty, Is.Not.Null);

                ensureMethod.Invoke(manager, null);

                Assert.That(resourcesProperty.GetValue(manager), Is.True);
                Assert.That(cellCountProperty.GetValue(manager), Is.GreaterThan(0));
                releaseMethod.Invoke(manager, null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        private static void AssertVector(Vector4 actual, Vector4 expected, string label, float tolerance = Epsilon)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(tolerance), $"{label} x");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(tolerance), $"{label} y");
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(tolerance), $"{label} z");
            Assert.That(actual.w, Is.EqualTo(expected.w).Within(tolerance), $"{label} w");
        }

        private static void AssertFinitePositiveSample(Vector4 directionAndPdf, Vector4 weightAndNormalDot)
        {
            foreach (float value in new[]
                     {
                         directionAndPdf.x, directionAndPdf.y, directionAndPdf.z, directionAndPdf.w,
                         weightAndNormalDot.x, weightAndNormalDot.y, weightAndNormalDot.z, weightAndNormalDot.w
                     })
            {
                Assert.That(float.IsNaN(value) || float.IsInfinity(value), Is.False, "BRDF sample must be finite");
            }

            Assert.That(directionAndPdf.w, Is.GreaterThan(0.0f), "BRDF sample PDF");
            Assert.That(weightAndNormalDot.x, Is.GreaterThanOrEqualTo(0.0f), "BRDF sample red weight");
            Assert.That(weightAndNormalDot.y, Is.GreaterThanOrEqualTo(0.0f), "BRDF sample green weight");
            Assert.That(weightAndNormalDot.z, Is.GreaterThanOrEqualTo(0.0f), "BRDF sample blue weight");
            Assert.That(weightAndNormalDot.w, Is.GreaterThan(0.0f), "BRDF sample must be above the surface");
        }
    }
}
