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
            var buffer = new ComputeBuffer(8, sizeof(float) * 4);
            try
            {
                shader.SetBuffer(kernel, "RegressionResults", buffer);
                shader.Dispatch(kernel, 1, 1, 1);

                var results = new Vector4[8];
                buffer.GetData(results);

                AssertVector(results[0], new Vector4(0.70710677f, 0.70710677f, 0.0f, 1.0f), "reflection");
                AssertVector(results[1], new Vector4(0.47140452f, -0.8819171f, 0.0f, 1.0f), "air-to-glass refraction");
                AssertVector(results[2], Vector4.zero, "total internal reflection signal");
                AssertVector(results[3], new Vector4(0.042069275f, 0.04f, 0.0f, 1.0f), "Schlick Fresnel");
                AssertVector(results[4], new Vector4(0.17212175f, 0.39543194f, 0.64325213f, 1.0f), "glass absorption", 0.0002f);
                AssertVector(results[5], new Vector4(1.0f, 2.0f, 3.0f, 0.0f), "air-to-water medium transition");
                AssertVector(results[6], new Vector4(2.0f, 1.5f, 1.0f, 7.0f), "water-to-glass medium transition");
                AssertVector(results[7], new Vector4(1.5f, 2.0f, 1.0f, 1.0f), "glass-to-water medium transition");
            }
            finally
            {
                buffer.Release();
            }
        }

        private static void AssertVector(Vector4 actual, Vector4 expected, string label, float tolerance = Epsilon)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(tolerance), $"{label} x");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(tolerance), $"{label} y");
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(tolerance), $"{label} z");
            Assert.That(actual.w, Is.EqualTo(expected.w).Within(tolerance), $"{label} w");
        }
    }
}
