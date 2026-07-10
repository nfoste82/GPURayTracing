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

        [StructLayout(LayoutKind.Sequential)]
        private struct MeshTriangleData
        {
            public Vector3 vertex0;
            public Vector3 vertex1;
            public Vector3 vertex2;
            public Vector3 normal;
            public Vector3 color;
            public float smoothness;
            public Vector2 uv0;
            public Vector2 uv1;
            public Vector2 uv2;
            public float opacity;
            public Vector3 emission;
            public float refraction;
            public int materialType;
            public int meshIndex;
            public int textureIndex;
            public int padding0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MeshInfoData
        {
            public Vector3 boundsMin;
            public int rootNodeIndex;
            public Vector3 boundsMax;
            public int triangleStart;
            public int triangleCount;
            public int meshIndex;
            public int isLight;
            public int padding1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BvhNodeData
        {
            public Vector3 boundsMin;
            public int leftChildIndex;
            public Vector3 boundsMax;
            public int rightChildIndex;
            public int triangleStart;
            public int triangleCount;
            public int padding0;
            public int padding1;
        }

        [Test]
        public void ReflectionScene_CurrentImageBaseline_IsStable()
        {
            Vector4[] signature = RenderSignature(new[]
            {
                Sphere(new Vector3(0.0f, 1.0f, 0.5f), new Vector3(0.75f, 0.25f, 0.12f), 1.0f, 1.0f, 1.0f, 1.0f, 1)
            }, false, new Vector3(0.0f, 1.6f, -4.5f), Quaternion.Euler(4.0f, 0.0f, 0.0f));

            AssertSignature("reflection", signature, ReflectionBaseline);
        }

        [Test]
        public void GlassSphereScene_CurrentImageBaseline_IsStable()
        {
            Vector4[] signature = RenderSignature(new[]
            {
                Sphere(new Vector3(0.0f, 1.0f, 0.5f), new Vector3(0.35f, 0.7f, 0.95f), 1.0f, 1.0f, 0.35f, 1.5f, 2),
                Sphere(new Vector3(0.0f, 1.0f, 2.5f), new Vector3(0.9f, 0.2f, 0.12f), 0.65f, 0.2f, 1.0f, 1.0f, 0)
            }, false, new Vector3(0.0f, 1.6f, -4.5f), Quaternion.Euler(4.0f, 0.0f, 0.0f));

            AssertSignature("glass sphere", signature, GlassBaseline);
        }

        [Test]
        public void WaterScene_CurrentImageBaseline_IsStable()
        {
            Vector4[] signature = RenderSignature(new[]
            {
                Sphere(new Vector3(0.0f, 0.35f, 1.5f), new Vector3(0.95f, 0.32f, 0.08f), 0.35f, 0.2f, 1.0f, 1.0f, 0)
            }, true, new Vector3(0.0f, 1.6f, -4.5f), Quaternion.Euler(10.0f, 0.0f, 0.0f));

            AssertSignature("water", signature, WaterBaseline);
        }

        [Test]
        public void NestedWaterAndGlassScene_CurrentImageBaseline_IsStable()
        {
            Vector4[] signature = RenderSignature(new[]
            {
                Sphere(new Vector3(0.0f, 0.35f, 1.35f), new Vector3(0.42f, 0.78f, 0.95f), 0.32f, 1.0f, 0.25f, 1.5f, 2),
                Sphere(new Vector3(0.0f, 0.35f, 2.05f), new Vector3(0.95f, 0.2f, 0.08f), 0.24f, 0.2f, 1.0f, 1.0f, 0)
            }, true, new Vector3(0.0f, 1.6f, -4.5f), Quaternion.Euler(10.0f, 0.0f, 0.0f));

            AssertSignature("nested water and glass", signature, NestedWaterGlassBaseline);
        }

        [Test]
        public void CameraStartingUnderwaterScene_CurrentImageBaseline_IsStable()
        {
            Vector4[] signature = RenderSignature(new[]
            {
                Sphere(new Vector3(0.0f, 0.35f, 1.35f), new Vector3(0.42f, 0.78f, 0.95f), 0.32f, 1.0f, 0.25f, 1.5f, 2),
                Sphere(new Vector3(0.0f, 0.35f, 2.05f), new Vector3(0.95f, 0.2f, 0.08f), 0.24f, 0.2f, 1.0f, 1.0f, 0)
            }, true, new Vector3(0.0f, 0.35f, -4.5f), Quaternion.identity);

            AssertSignature("camera starting underwater", signature, UnderwaterCameraBaseline);
        }

        [Test]
        public void ClosedMeshGlassScene_CurrentImageBaseline_IsStable()
        {
            CreateGlassCube(out MeshTriangleData[] triangles, out MeshInfoData[] meshes, out BvhNodeData[] bvhNodes);
            Vector4[] signature = RenderSignature(new[]
            {
                Sphere(new Vector3(0.0f, 1.0f, 2.25f), new Vector3(0.92f, 0.18f, 0.08f), 0.55f, 0.2f, 1.0f, 1.0f, 0)
            }, false, new Vector3(0.0f, 1.6f, -4.5f), Quaternion.Euler(4.0f, 0.0f, 0.0f), triangles, meshes, bvhNodes);

            AssertSignature("closed mesh glass", signature, ClosedMeshGlassBaseline);
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
            new Vector4(0.10158380f, 0.19616780f, 0.32085890f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f), new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f), new Vector4(0.03365663f, 0.08516176f, 0.19658320f, 1.0f),
            new Vector4(0.02643724f, 0.08810403f, 0.11557450f, 1.0f), new Vector4(0.01617341f, 0.07468688f, 0.20154250f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static readonly Vector4[] NestedWaterGlassBaseline =
        {
            new Vector4(0.10155270f, 0.19598400f, 0.32057390f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f), new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f), new Vector4(0.03365663f, 0.08516176f, 0.19658320f, 1.0f),
            new Vector4(0.01132319f, 0.02900140f, 0.07406791f, 1.0f), new Vector4(0.01617341f, 0.07468688f, 0.20154250f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static readonly Vector4[] UnderwaterCameraBaseline =
        {
            new Vector4(0.00145278f, 0.01631414f, 0.05592640f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f), new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f), new Vector4(0.01138539f, 0.08646619f, 0.24626650f, 1.0f),
            new Vector4(0.00038464f, 0.00242334f, 0.00816626f, 1.0f), new Vector4(0.01115748f, 0.08535092f, 0.24409440f, 1.0f),
            new Vector4(0.00246564f, 0.02287684f, 0.07859048f, 1.0f), new Vector4(0.00112369f, 0.01974659f, 0.07859048f, 1.0f)
        };

        private static readonly Vector4[] ClosedMeshGlassBaseline =
        {
            new Vector4(0.20406680f, 0.38505650f, 0.59335350f, 1.0f),
            new Vector4(0.20471280f, 0.38236340f, 0.59162720f, 1.0f), new Vector4(0.03254014f, 0.11041520f, 0.27753210f, 1.0f),
            new Vector4(0.20471280f, 0.38236340f, 0.59162720f, 1.0f), new Vector4(0.20471280f, 0.38236340f, 0.59162720f, 1.0f),
            new Vector4(0.10642110f, 0.14467890f, 0.28724830f, 1.0f), new Vector4(0.20471280f, 0.38236340f, 0.59162720f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
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

        private static Vector4[] RenderSignature(
            SphereData[] spheres,
            bool waterEnabled,
            Vector3 cameraPosition,
            Quaternion cameraRotation,
            MeshTriangleData[] triangles = null,
            MeshInfoData[] meshes = null,
            BvhNodeData[] bvhNodes = null)
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
            triangles = triangles ?? Array.Empty<MeshTriangleData>();
            meshes = meshes ?? Array.Empty<MeshInfoData>();
            bvhNodes = bvhNodes ?? Array.Empty<BvhNodeData>();
            var result = CreateRenderTexture(RenderTextureFormat.ARGBFloat);
            var accumulation = CreateRenderTexture(RenderTextureFormat.ARGBFloat);
            var skybox = CreateSolidTexture(new Color(0.18f, 0.32f, 0.58f, 1.0f));
            var meshTextures = CreateMeshTextureArray();
            ComputeBuffer sphereBuffer = CreateBuffer(spheres, 56);
            ComputeBuffer lightBuffer = CreateDummyBuffer(72);
            ComputeBuffer triangleBuffer = CreateBuffer(triangles, 124);
            ComputeBuffer meshBuffer = CreateBuffer(meshes, 48);
            ComputeBuffer bvhBuffer = CreateBuffer(bvhNodes, 48);
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
                    cameraPosition,
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
                shader.SetInt("_NumTriangles", triangles.Length);
                shader.SetInt("_NumMeshes", meshes.Length);
                shader.SetInt("_NumTopLevelBvhNodes", 0);
                shader.SetInt("_NumShadowBvhNodes", 0);
                bool hasTransparentSphere = spheres.Length > 0 && Array.Exists(spheres, sphere => sphere.opacity < 1.0f);
                bool hasTransparentMesh = triangles.Length > 0 && Array.Exists(triangles, triangle => triangle.opacity < 1.0f);
                shader.SetInt("_HasTransparentShadowBlockers", hasTransparentSphere || hasTransparentMesh ? 1 : 0);
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

        private static void CreateGlassCube(out MeshTriangleData[] triangles, out MeshInfoData[] meshes, out BvhNodeData[] bvhNodes)
        {
            Vector3 min = new Vector3(-0.8f, 0.2f, -0.3f);
            Vector3 max = new Vector3(0.8f, 1.8f, 1.3f);
            Vector3 p000 = new Vector3(min.x, min.y, min.z);
            Vector3 p001 = new Vector3(min.x, min.y, max.z);
            Vector3 p010 = new Vector3(min.x, max.y, min.z);
            Vector3 p011 = new Vector3(min.x, max.y, max.z);
            Vector3 p100 = new Vector3(max.x, min.y, min.z);
            Vector3 p101 = new Vector3(max.x, min.y, max.z);
            Vector3 p110 = new Vector3(max.x, max.y, min.z);
            Vector3 p111 = new Vector3(max.x, max.y, max.z);

            triangles = new[]
            {
                Triangle(p000, p010, p110, Vector3.back), Triangle(p000, p110, p100, Vector3.back),
                Triangle(p001, p101, p111, Vector3.forward), Triangle(p001, p111, p011, Vector3.forward),
                Triangle(p000, p001, p011, Vector3.left), Triangle(p000, p011, p010, Vector3.left),
                Triangle(p100, p110, p111, Vector3.right), Triangle(p100, p111, p101, Vector3.right),
                Triangle(p000, p100, p101, Vector3.down), Triangle(p000, p101, p001, Vector3.down),
                Triangle(p010, p011, p111, Vector3.up), Triangle(p010, p111, p110, Vector3.up)
            };
            meshes = new[]
            {
                new MeshInfoData
                {
                    boundsMin = min,
                    rootNodeIndex = 0,
                    boundsMax = max,
                    triangleStart = 0,
                    triangleCount = triangles.Length,
                    meshIndex = 0,
                    isLight = 0
                }
            };
            bvhNodes = new[]
            {
                new BvhNodeData
                {
                    boundsMin = min,
                    leftChildIndex = -1,
                    boundsMax = max,
                    rightChildIndex = -1,
                    triangleStart = 0,
                    triangleCount = triangles.Length
                }
            };
        }

        private static MeshTriangleData Triangle(Vector3 vertex0, Vector3 vertex1, Vector3 vertex2, Vector3 normal)
        {
            return new MeshTriangleData
            {
                vertex0 = vertex0,
                vertex1 = vertex1,
                vertex2 = vertex2,
                normal = normal,
                color = new Vector3(0.38f, 0.72f, 0.94f),
                smoothness = 1.0f,
                opacity = 0.3f,
                emission = Vector3.zero,
                refraction = 1.5f,
                materialType = 2,
                meshIndex = 0,
                textureIndex = -1
            };
        }

        private static void SetWater(ComputeShader shader, bool enabled)
        {
            shader.SetInt("_WaterEnabled", enabled ? 1 : 0);
            shader.SetVector("_WaterCenter", new Vector4(0.0f, 0.75f, 1.0f, 0.0f));
            shader.SetVector("_WaterSize", new Vector4(12.0f, 12.0f, 0.0f, 0.0f));
            shader.SetFloat("_WaterDepth", 6.0f);
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
