using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GPURayTracing.Tests
{
    public class RayTracingImageRegressionTests
    {
        private const string ComputeShaderPath = "Assets/Scripts/RayTracingCompute.compute";
        private const int ImageSize = 32;
        private const float SignatureTolerance = 0.002f;

        [StructLayout(LayoutKind.Sequential)]
        private struct SphereData
        {
            public Vector3 position;
            public Vector3 color;
            public Vector3 emission;
            public float radius;
            public float smoothness;
            public float opacity;
            public float refraction;
            public int materialType;
        }

        [Test]
        public void ReflectionScene_CurrentImageBaseline_IsStable()
        {
            Vector4[] signature = RenderSignature(new[]
            {
                Sphere(new Vector3(0.0f, 1.0f, 0.5f), new Vector3(0.75f, 0.25f, 0.12f), 1.0f, 1.0f, 1.0f, 1.0f, 1)
            }, false, Quaternion.Euler(4.0f, 0.0f, 0.0f));

            AssertSignature("reflection", signature, ReflectionBaseline);
        }

        [Test]
        public void GlassSphereScene_CurrentImageBaseline_IsStable()
        {
            Vector4[] signature = RenderSignature(new[]
            {
                Sphere(new Vector3(0.0f, 1.0f, 0.5f), new Vector3(0.35f, 0.7f, 0.95f), 1.0f, 1.0f, 0.35f, 1.5f, 2),
                Sphere(new Vector3(0.0f, 1.0f, 2.5f), new Vector3(0.9f, 0.2f, 0.12f), 0.65f, 0.2f, 1.0f, 1.0f, 0)
            }, false, Quaternion.Euler(4.0f, 0.0f, 0.0f));

            AssertSignature("glass sphere", signature, GlassBaseline);
        }

        [Test]
        public void WaterScene_CurrentImageBaseline_IsStable()
        {
            Vector4[] signature = RenderSignature(new[]
            {
                Sphere(new Vector3(0.0f, 0.35f, 1.5f), new Vector3(0.95f, 0.32f, 0.08f), 0.35f, 0.2f, 1.0f, 1.0f, 0)
            }, true, Quaternion.Euler(10.0f, 0.0f, 0.0f));

            AssertSignature("water", signature, WaterBaseline);
        }

        // Average HDR color followed by eight fixed pixel probes. These values intentionally lock
        // current output, including approximations; update only after reviewing an expected change.
        private static readonly Vector4[] ReflectionBaseline =
        {
            new Vector4(0.21251380f, 0.33433500f, 0.48829080f, 1.0f),
            new Vector4(0.20471280f, 0.38236340f, 0.59162720f, 1.0f),
            new Vector4(0.06344359f, 0.01816801f, 0.01279860f, 1.0f),
            new Vector4(0.20471280f, 0.38236340f, 0.59162720f, 1.0f),
            new Vector4(0.20471280f, 0.38236340f, 0.59162720f, 1.0f),
            new Vector4(0.18866170f, 0.09106690f, 0.07388087f, 1.0f),
            new Vector4(0.20471280f, 0.38236340f, 0.59162720f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static readonly Vector4[] GlassBaseline =
        {
            new Vector4(0.20013470f, 0.37145960f, 0.57635200f, 1.0f),
            new Vector4(0.20471280f, 0.38236340f, 0.59162720f, 1.0f),
            new Vector4(0.07164505f, 0.11224990f, 0.26490100f, 1.0f),
            new Vector4(0.20471280f, 0.38236340f, 0.59162720f, 1.0f),
            new Vector4(0.20471280f, 0.38236340f, 0.59162720f, 1.0f),
            new Vector4(0.12077810f, 0.19441330f, 0.38392310f, 1.0f),
            new Vector4(0.20471280f, 0.38236340f, 0.59162720f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static readonly Vector4[] WaterBaseline =
        {
            new Vector4(0.10132060f, 0.19449600f, 0.31670610f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
            new Vector4(0.03365663f, 0.08516176f, 0.19658320f, 1.0f),
            new Vector4(0.02369369f, 0.08810403f, 0.11749600f, 1.0f),
            new Vector4(0.01617341f, 0.07468688f, 0.20154250f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static SphereData Sphere(Vector3 position, Vector3 color, float radius, float smoothness, float opacity, float refraction, int materialType)
        {
            return new SphereData
            {
                position = position,
                color = color,
                emission = Vector3.zero,
                radius = radius,
                smoothness = smoothness,
                opacity = opacity,
                refraction = refraction,
                materialType = materialType
            };
        }

        private static Vector4[] RenderSignature(SphereData[] spheres, bool waterEnabled, Quaternion cameraRotation)
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Compute shaders are not supported by the active graphics device.");
            }

            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeShaderPath);
            Assert.That(shader, Is.Not.Null);
            if (!shader.HasKernel("CSMain"))
            {
                Assert.Ignore("The active graphics device did not compile CSMain. Run without -nographics.");
            }

            int kernel = shader.FindKernel("CSMain");
            var result = CreateRenderTexture(RenderTextureFormat.ARGBFloat);
            var accumulation = CreateRenderTexture(RenderTextureFormat.ARGBFloat);
            var skybox = CreateSolidTexture(new Color(0.18f, 0.32f, 0.58f, 1.0f));
            var meshTextures = CreateMeshTextureArray();
            ComputeBuffer sphereBuffer = CreateBuffer(spheres, 56);
            ComputeBuffer lightBuffer = CreateDummyBuffer(72);
            ComputeBuffer triangleBuffer = CreateDummyBuffer(124);
            ComputeBuffer meshBuffer = CreateDummyBuffer(48);
            ComputeBuffer bvhBuffer = CreateDummyBuffer(48);
            ComputeBuffer topLevelBuffer = CreateDummyBuffer(48);
            ComputeBuffer shadowBuffer = CreateDummyBuffer(48);

            try
            {
                shader.DisableKeyword("DEBUG_RENDER");
                shader.SetTexture(kernel, "Result", result);
                shader.SetTexture(kernel, "AccumulationResult", accumulation);
                shader.SetTexture(kernel, "_SkyboxTexture", skybox);
                shader.SetTexture(kernel, "_MeshAlbedoTextures", meshTextures);
                shader.SetBuffer(kernel, "_Spheres", sphereBuffer);
                shader.SetBuffer(kernel, "_Lights", lightBuffer);
                shader.SetBuffer(kernel, "_Triangles", triangleBuffer);
                shader.SetBuffer(kernel, "_Meshes", meshBuffer);
                shader.SetBuffer(kernel, "_BvhNodes", bvhBuffer);
                shader.SetBuffer(kernel, "_TopLevelBvhNodes", topLevelBuffer);
                shader.SetBuffer(kernel, "_ShadowBvhNodes", shadowBuffer);

                // Unity view-space camera rays point down -Z; cameraToWorld includes that handedness
                // conversion, unlike Transform.localToWorldMatrix.
                Matrix4x4 cameraToWorld = Matrix4x4.TRS(
                    new Vector3(0.0f, 1.6f, -4.5f),
                    cameraRotation,
                    new Vector3(1.0f, 1.0f, -1.0f));
                shader.SetMatrix("_CameraToWorld", cameraToWorld);
                shader.SetMatrix("_CameraInverseProjection", Matrix4x4.Perspective(48.0f, 1.0f, 0.1f, 100.0f).inverse);
                shader.SetVector("_SkyboxLight", Vector4.one);
                shader.SetInt("_Seed", 1);
                shader.SetInt("_SampleOffset", 0);
                shader.SetInt("_NumberOfPasses", 8);
                shader.SetInt("_NumBounces", 6);
                shader.SetInt("_DebugRenderMode", 0);
                shader.SetInt("_UseFrameAccumulation", 0);
                shader.SetInt("_AccumulatedFrameCount", 0);
                shader.SetInt("_MaxLightSamples", 0);
                shader.SetInt("_LightSamplingStrategy", 0);
                shader.SetInt("_LightSampleCount", 1);
                shader.SetInt("_ShadowQuality", 0);
                shader.SetFloat("_ShadowRandomness", 0.0f);
                shader.SetFloat("_LightFalloffScale", 0.16f);
                shader.SetFloat("_FocalDistance", 100.0f);
                shader.SetFloat("_GroundSmoothness", 0.9f);
                shader.SetFloat("_Exposure", 1.0f);
                shader.SetInt("_NumSpheres", spheres.Length);
                shader.SetInt("_NumLights", 0);
                shader.SetInt("_NumTriangles", 0);
                shader.SetInt("_NumMeshes", 0);
                shader.SetInt("_NumTopLevelBvhNodes", 0);
                shader.SetInt("_NumShadowBvhNodes", 0);
                shader.SetInt("_HasTransparentShadowBlockers", spheres.Length > 0 && Array.Exists(spheres, sphere => sphere.opacity < 1.0f) ? 1 : 0);
                SetWater(shader, waterEnabled);

                shader.Dispatch(kernel, ImageSize / 8, ImageSize / 8, 1);
                return ReadSignature(result);
            }
            finally
            {
                sphereBuffer.Release();
                lightBuffer.Release();
                triangleBuffer.Release();
                meshBuffer.Release();
                bvhBuffer.Release();
                topLevelBuffer.Release();
                shadowBuffer.Release();
                result.Release();
                accumulation.Release();
                UnityEngine.Object.DestroyImmediate(skybox);
                UnityEngine.Object.DestroyImmediate(meshTextures);
            }
        }

        private static void SetWater(ComputeShader shader, bool enabled)
        {
            shader.SetInt("_WaterEnabled", enabled ? 1 : 0);
            shader.SetVector("_WaterCenter", new Vector4(0.0f, 0.75f, 1.0f, 0.0f));
            shader.SetVector("_WaterSize", new Vector4(12.0f, 12.0f, 0.0f, 0.0f));
            shader.SetVector("_WaterColor", new Vector4(0.17f, 0.45f, 0.52f, 0.0f));
            shader.SetFloat("_WaterSmoothness", 0.96f);
            shader.SetFloat("_WaterOpacity", 0.18f);
            shader.SetFloat("_WaterAbsorptionStrength", 0.22f);
            shader.SetFloat("_WaterRefraction", 1.33f);
            shader.SetFloat("_WaterWaveAmplitude", 0.0f);
            shader.SetFloat("_WaterWaveScale", 0.55f);
            shader.SetFloat("_WaterWaveSpeed", 0.0f);
            shader.SetFloat("_WaterTime", 0.0f);
            shader.SetInt("_WaterMarchSteps", 28);
            shader.SetInt("_WaterRefinementSteps", 5);
        }

        private static Vector4[] ReadSignature(RenderTexture source)
        {
            var texture = new Texture2D(ImageSize, ImageSize, TextureFormat.RGBAFloat, false, true);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = source;
            texture.ReadPixels(new Rect(0, 0, ImageSize, ImageSize), 0, 0);
            texture.Apply(false, false);
            RenderTexture.active = previous;
            Color[] pixels = texture.GetPixels();
            UnityEngine.Object.DestroyImmediate(texture);

            var signature = new Vector4[9];
            Vector4 average = Vector4.zero;
            foreach (Color pixel in pixels)
            {
                average += (Vector4)pixel;
            }
            signature[0] = average / pixels.Length;

            int[,] probes = { { 8, 8 }, { 16, 8 }, { 24, 8 }, { 8, 16 }, { 16, 16 }, { 24, 16 }, { 12, 24 }, { 20, 24 } };
            for (int i = 0; i < probes.GetLength(0); i++)
            {
                signature[i + 1] = pixels[probes[i, 1] * ImageSize + probes[i, 0]];
            }
            return signature;
        }

        private static void AssertSignature(string scene, Vector4[] actual, Vector4[] expected)
        {
            if (expected.Length == 0)
            {
                Assert.Fail($"Capture {scene} baseline:\n{FormatSignature(actual)}");
            }

            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(actual[i].x, Is.EqualTo(expected[i].x).Within(SignatureTolerance), $"{scene} signature[{i}].r");
                Assert.That(actual[i].y, Is.EqualTo(expected[i].y).Within(SignatureTolerance), $"{scene} signature[{i}].g");
                Assert.That(actual[i].z, Is.EqualTo(expected[i].z).Within(SignatureTolerance), $"{scene} signature[{i}].b");
            }
        }

        private static string FormatSignature(Vector4[] signature)
        {
            return string.Join(",\n", Array.ConvertAll(signature, value =>
                $"new Vector4({value.x:F8}f, {value.y:F8}f, {value.z:F8}f, {value.w:F8}f)"));
        }

        private static RenderTexture CreateRenderTexture(RenderTextureFormat format)
        {
            var texture = new RenderTexture(ImageSize, ImageSize, 0, format) { enableRandomWrite = true };
            texture.Create();
            return texture;
        }

        private static Texture2D CreateSolidTexture(Color color)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBAFloat, false, true);
            texture.SetPixels(new[] { color, color, color, color });
            texture.Apply(false, false);
            return texture;
        }

        private static Texture2DArray CreateMeshTextureArray()
        {
            var texture = new Texture2DArray(1, 1, 1, TextureFormat.RGBA32, false, true);
            texture.SetPixels(new[] { Color.white }, 0);
            texture.Apply(false, false);
            return texture;
        }

        private static ComputeBuffer CreateBuffer<T>(T[] values, int stride) where T : struct
        {
            var buffer = new ComputeBuffer(Mathf.Max(1, values.Length), stride);
            buffer.SetData(values.Length == 0 ? new T[1] : values);
            return buffer;
        }

        private static ComputeBuffer CreateDummyBuffer(int stride)
        {
            return new ComputeBuffer(1, stride);
        }
    }
}
