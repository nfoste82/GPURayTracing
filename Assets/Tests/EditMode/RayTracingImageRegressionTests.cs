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
        private struct LightData
        {
            public Vector3 position;
            public Vector3 emission;
            public Vector3 u;
            public float radius;
            public Vector3 v;
            public float area;
            public Vector3 normal;
            public int type;
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
        public void CameraInsideTranslucentGlassSphere_CurrentImageBaseline_IsStable()
        {
            Vector4[] signature = RenderSignature(new[]
            {
                Sphere(new Vector3(0.0f, 1.3f, -3.8f), new Vector3(0.42f, 0.72f, 0.94f), 1.45f, 1.0f, 0.35f, 1.5f, 2),
                Sphere(new Vector3(0.0f, 1.1f, 1.4f), new Vector3(0.92f, 0.2f, 0.08f), 0.75f, 0.2f, 1.0f, 1.0f, 0)
            }, false, new Vector3(0.0f, 1.3f, -4.5f), Quaternion.identity);

            AssertSignature("camera inside translucent glass sphere", signature, InsideGlassCameraBaseline);
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

        [TestCase(1, 1)]
        [TestCase(3, 5)]
        [TestCase(13, 7)]
        public void NonMultipleOfEightDispatch_WritesEveryPixelWithoutOutOfBoundsAccess(int width, int height)
        {
            Vector4[] signature = RenderSignature(
                Array.Empty<SphereData>(),
                false,
                new Vector3(0.0f, 1.6f, -4.5f),
                Quaternion.identity,
                width: width,
                height: height);

            foreach (Vector4 value in signature)
            {
                Assert.That(float.IsNaN(value.x) || float.IsInfinity(value.x), Is.False);
                Assert.That(float.IsNaN(value.y) || float.IsInfinity(value.y), Is.False);
                Assert.That(float.IsNaN(value.z) || float.IsInfinity(value.z), Is.False);
                Assert.That(value.w, Is.EqualTo(1.0f).Within(0.0001f));
            }
        }

        [Test]
        public void TexturedMeshScene_CurrentImageBaseline_IsStable()
        {
            CreateTexturedQuad(out MeshTriangleData[] triangles, out MeshInfoData[] meshes, out BvhNodeData[] bvhNodes);
            Texture2DArray textures = CreateCheckerTextureArray();
            try
            {
                Vector4[] signature = RenderSignature(
                    Array.Empty<SphereData>(), false, new Vector3(0.0f, 1.3f, -4.5f), Quaternion.identity,
                    triangles, meshes, bvhNodes,
                    new[] { SphereLight(new Vector3(-1.8f, 3.2f, -1.5f), new Vector3(14.0f, 14.0f, 14.0f), 0.3f) },
                    textures);
                AssertSignature("textured mesh", signature, TexturedMeshBaseline);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(textures);
            }
        }

        [Test]
        public void MeshLightScene_CurrentImageBaseline_IsStable()
        {
            CreateEmissiveQuad(out MeshTriangleData[] triangles, out MeshInfoData[] meshes, out BvhNodeData[] bvhNodes, out LightData[] lights);
            Vector4[] signature = RenderSignature(
                new[] { Sphere(new Vector3(0.0f, 0.75f, 1.5f), new Vector3(0.75f, 0.35f, 0.12f), 0.75f, 0.2f, 1.0f, 1.0f, 0) },
                false, new Vector3(0.0f, 1.6f, -4.5f), Quaternion.Euler(4.0f, 0.0f, 0.0f),
                triangles, meshes, bvhNodes, lights);
            AssertSignature("mesh light", signature, MeshLightBaseline);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void TransparentShadowScenes_CurrentImageBaselines_AreStable(int fixture)
        {
            CreateShadowFixture(fixture, out SphereData[] spheres, out MeshTriangleData[] triangles, out MeshInfoData[] meshes, out BvhNodeData[] bvhNodes);
            var lights = new[] { SphereLight(new Vector3(1.8f, 4.0f, -0.8f), new Vector3(18.0f, 16.0f, 14.0f), 0.35f) };
            Vector4[] signature = RenderSignature(
                spheres, false, new Vector3(0.0f, 1.8f, -5.5f), Quaternion.Euler(12.0f, 0.0f, 0.0f),
                triangles, meshes, bvhNodes, lights);

            Vector4[][] baselines = { TransparentSphereShadowBaseline, TransparentMeshShadowBaseline, StackedTransparentShadowBaseline };
            AssertSignature($"transparent shadow fixture {fixture}", signature, baselines[fixture]);
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

        private static readonly Vector4[] InsideGlassCameraBaseline =
        {
            new Vector4(0.05468999f, 0.22955850f, 0.51936210f, 1.0f),
            new Vector4(0.04178934f, 0.19062350f, 0.45779480f, 1.0f),
            new Vector4(0.04602021f, 0.22105660f, 0.51487710f, 1.0f),
            new Vector4(0.05979341f, 0.26276930f, 0.56128440f, 1.0f),
            new Vector4(0.05455200f, 0.23933270f, 0.53959990f, 1.0f),
            new Vector4(0.02384388f, 0.02539920f, 0.07592814f, 1.0f),
            new Vector4(0.09506290f, 0.34522480f, 0.63457150f, 1.0f),
            new Vector4(0.07205451f, 0.28918140f, 0.59165610f, 1.0f),
            new Vector4(0.04310199f, 0.20011510f, 0.47649630f, 1.0f)
        };

        private static readonly Vector4[] WaterBaseline =
        {
            new Vector4(0.10111540f, 0.19415280f, 0.31678240f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f), new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f), new Vector4(0.03365663f, 0.08516176f, 0.19658320f, 1.0f),
            new Vector4(0.01132319f, 0.02900140f, 0.07406791f, 1.0f), new Vector4(0.01617341f, 0.07468688f, 0.20154250f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static readonly Vector4[] NestedWaterGlassBaseline =
        {
            new Vector4(0.10110180f, 0.19405520f, 0.31659620f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f), new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f), new Vector4(0.03365663f, 0.08516176f, 0.19658320f, 1.0f),
            new Vector4(0.01132319f, 0.02900140f, 0.07406791f, 1.0f), new Vector4(0.01617341f, 0.07468688f, 0.20154250f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static readonly Vector4[] UnderwaterCameraBaseline =
        {
            new Vector4(0.00060182f, 0.00948681f, 0.03588293f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f), new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f), new Vector4(0.00168944f, 0.02495992f, 0.09691052f, 1.0f),
            new Vector4(0.00009245f, 0.00087927f, 0.00297273f, 1.0f), new Vector4(0.00166109f, 0.02461908f, 0.09583434f, 1.0f),
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

        private static readonly Vector4[] TexturedMeshBaseline =
        {
            new Vector4(0.66644350f, 0.44557010f, 0.82882260f, 1.0f), new Vector4(0.97508000f, 0.00002457f, 0.95618470f, 1.0f),
            new Vector4(0.95546660f, 0.00000784f, 0.96079920f, 1.0f), new Vector4(0.89144050f, 0.00000029f, 0.91804080f, 1.0f),
            new Vector4(0.97313550f, 0.01514817f, 0.99068440f, 1.0f), new Vector4(0.95175840f, 0.00075266f, 0.95018200f, 1.0f),
            new Vector4(0.91613910f, 0.00000660f, 0.89885240f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static readonly Vector4[] MeshLightBaseline =
        {
            new Vector4(0.61611250f, 0.70159460f, 0.77816160f, 1.0f), new Vector4(1.0f, 1.0f, 0.99457820f, 1.0f),
            new Vector4(0.01259302f, 0.00944683f, 0.00465952f, 1.0f), new Vector4(1.0f, 1.0f, 0.99132970f, 1.0f),
            new Vector4(0.21515110f, 0.38496370f, 0.58750780f, 1.0f), new Vector4(1.0f, 0.99655140f, 0.89897720f, 1.0f),
            new Vector4(0.21410150f, 0.38864730f, 0.59400840f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static readonly Vector4[] TransparentSphereShadowBaseline =
        {
            new Vector4(0.67417510f, 0.74471530f, 0.82114350f, 1.0f), new Vector4(0.95968090f, 0.95407700f, 0.95036980f, 1.0f),
            new Vector4(0.97766330f, 0.97265360f, 0.96821630f, 1.0f), new Vector4(0.98836910f, 0.98412730f, 0.98013760f, 1.0f),
            new Vector4(0.84476920f, 0.84344210f, 0.85538310f, 1.0f), new Vector4(0.89204600f, 0.88718720f, 0.89049240f, 1.0f),
            new Vector4(0.89949620f, 0.89429760f, 0.89648050f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.74902470f, 0.86729660f, 0.89976260f, 1.0f)
        };

        private static readonly Vector4[] TransparentMeshShadowBaseline =
        {
            new Vector4(0.66521620f, 0.73002660f, 0.80848420f, 1.0f), new Vector4(0.95968090f, 0.95407700f, 0.95036980f, 1.0f),
            new Vector4(0.97830630f, 0.97300170f, 0.96822960f, 1.0f), new Vector4(0.98836910f, 0.98412730f, 0.98013760f, 1.0f),
            new Vector4(0.84476920f, 0.84344210f, 0.85538310f, 1.0f), new Vector4(0.89204600f, 0.88718720f, 0.89049240f, 1.0f),
            new Vector4(0.89949620f, 0.89429760f, 0.89648050f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static readonly Vector4[] StackedTransparentShadowBaseline =
        {
            new Vector4(0.67545430f, 0.73982670f, 0.81464280f, 1.0f), new Vector4(0.95968090f, 0.95407700f, 0.95036980f, 1.0f),
            new Vector4(0.97803340f, 0.97257240f, 0.96746020f, 1.0f), new Vector4(0.98836910f, 0.98412730f, 0.98013760f, 1.0f),
            new Vector4(0.84476920f, 0.84344210f, 0.85538310f, 1.0f), new Vector4(0.89204600f, 0.88718720f, 0.89049240f, 1.0f),
            new Vector4(0.89949620f, 0.89429760f, 0.89648050f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.74902470f, 0.86729660f, 0.89976260f, 1.0f)
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

        private static LightData SphereLight(Vector3 position, Vector3 emission, float radius)
        {
            return new LightData
            {
                position = position,
                emission = emission,
                radius = radius,
                type = 0
            };
        }

        private static Vector4[] RenderSignature(
            SphereData[] spheres,
            bool waterEnabled,
            Vector3 cameraPosition,
            Quaternion cameraRotation,
            MeshTriangleData[] triangles = null,
            MeshInfoData[] meshes = null,
            BvhNodeData[] bvhNodes = null,
            LightData[] lights = null,
            Texture2DArray meshTextures = null,
            int width = ImageSize,
            int height = ImageSize)
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
            lights = lights ?? Array.Empty<LightData>();
            var result = CreateRenderTexture(width, height, RenderTextureFormat.ARGBFloat);
            var accumulation = CreateRenderTexture(width, height, RenderTextureFormat.ARGBFloat);
            var skybox = CreateSolidTexture(new Color(0.18f, 0.32f, 0.58f, 1.0f));
            bool ownsMeshTextures = meshTextures == null;
            meshTextures = meshTextures ?? CreateMeshTextureArray();
            ComputeBuffer sphereBuffer = CreateBuffer(spheres, 56);
            ComputeBuffer lightBuffer = CreateBuffer(lights, 72);
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
                shader.SetInt("_MaxLightSamples", lights.Length);
                shader.SetInt("_LightSamplingStrategy", 0);
                shader.SetInt("_LightSampleCount", 1);
                shader.SetInt("_ShadowQuality", 0);
                shader.SetFloat("_ShadowRandomness", 0.0f);
                shader.SetFloat("_LightFalloffScale", 0.16f);
                shader.SetFloat("_FocalDistance", 100.0f);
                shader.SetFloat("_GroundSmoothness", 0.9f);
                shader.SetFloat("_Exposure", 1.0f);
                shader.SetInt("_NumSpheres", spheres.Length);
                shader.SetInt("_NumLights", lights.Length);
                shader.SetInt("_NumTriangles", triangles.Length);
                shader.SetInt("_NumMeshes", meshes.Length);
                shader.SetInt("_NumTopLevelBvhNodes", 0);
                shader.SetInt("_NumShadowBvhNodes", 0);
                bool hasTransparentSphere = spheres.Length > 0 && Array.Exists(spheres, sphere => sphere.opacity < 1.0f);
                bool hasTransparentMesh = triangles.Length > 0 && Array.Exists(triangles, triangle => triangle.opacity < 1.0f);
                shader.SetInt("_HasTransparentShadowBlockers", hasTransparentSphere || hasTransparentMesh ? 1 : 0);
                SetWater(shader, waterEnabled);

                shader.Dispatch(kernel, Mathf.CeilToInt(width / 8.0f), Mathf.CeilToInt(height / 8.0f), 1);
                return ReadSignature(result, width, height);
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
                if (ownsMeshTextures)
                {
                    UnityEngine.Object.DestroyImmediate(meshTextures);
                }
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

        private static void CreateTexturedQuad(out MeshTriangleData[] triangles, out MeshInfoData[] meshes, out BvhNodeData[] bvhNodes)
        {
            Vector3 min = new Vector3(-1.4f, 0.1f, 0.45f);
            Vector3 max = new Vector3(1.4f, 2.3f, 0.45f);
            triangles = new[]
            {
                SurfaceTriangle(new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z), new Vector3(max.x, max.y, min.z), Vector3.back, Vector3.one, 1),
                SurfaceTriangle(new Vector3(min.x, min.y, min.z), new Vector3(max.x, max.y, min.z), new Vector3(min.x, max.y, min.z), Vector3.back, Vector3.one, 1)
            };
            triangles[0].uv0 = new Vector2(0.0f, 0.0f);
            triangles[0].uv1 = new Vector2(1.0f, 0.0f);
            triangles[0].uv2 = new Vector2(1.0f, 1.0f);
            triangles[1].uv0 = new Vector2(0.0f, 0.0f);
            triangles[1].uv1 = new Vector2(1.0f, 1.0f);
            triangles[1].uv2 = new Vector2(0.0f, 1.0f);
            CreateSingleLeafMesh(triangles, min - Vector3.one * 0.0001f, max + Vector3.one * 0.0001f, false, out meshes, out bvhNodes);
        }

        private static void CreateEmissiveQuad(
            out MeshTriangleData[] triangles,
            out MeshInfoData[] meshes,
            out BvhNodeData[] bvhNodes,
            out LightData[] lights)
        {
            Vector3 p0 = new Vector3(-1.0f, 3.6f, 0.0f);
            Vector3 p1 = new Vector3(1.0f, 3.6f, 0.0f);
            Vector3 p2 = new Vector3(1.0f, 3.6f, 2.0f);
            Vector3 p3 = new Vector3(-1.0f, 3.6f, 2.0f);
            Vector3 emission = new Vector3(8.0f, 7.0f, 5.0f);
            triangles = new[]
            {
                SurfaceTriangle(p0, p2, p1, Vector3.down, Vector3.one, -1, emission, 3),
                SurfaceTriangle(p0, p3, p2, Vector3.down, Vector3.one, -1, emission, 3)
            };
            CreateSingleLeafMesh(triangles, p0 - Vector3.one * 0.0001f, p2 + Vector3.one * 0.0001f, true, out meshes, out bvhNodes);
            lights = new[]
            {
                TriangleLight(p0, p2 - p0, p1 - p0, Vector3.down, emission),
                TriangleLight(p0, p3 - p0, p2 - p0, Vector3.down, emission)
            };
        }

        private static LightData TriangleLight(Vector3 position, Vector3 u, Vector3 v, Vector3 normal, Vector3 emission)
        {
            return new LightData
            {
                position = position,
                emission = emission,
                u = u,
                v = v,
                area = Vector3.Cross(u, v).magnitude * 0.5f,
                normal = normal,
                type = 1
            };
        }

        private static void CreateShadowFixture(
            int fixture,
            out SphereData[] spheres,
            out MeshTriangleData[] triangles,
            out MeshInfoData[] meshes,
            out BvhNodeData[] bvhNodes)
        {
            spheres = fixture == 0 || fixture == 2
                ? new[] { Sphere(new Vector3(0.6f, 1.55f, 0.35f), new Vector3(0.25f, 0.65f, 0.9f), 0.65f, 1.0f, 0.35f, 1.5f, 2) }
                : Array.Empty<SphereData>();

            if (fixture == 0)
            {
                triangles = Array.Empty<MeshTriangleData>();
                meshes = Array.Empty<MeshInfoData>();
                bvhNodes = Array.Empty<BvhNodeData>();
                return;
            }

            CreateTransparentBox(new Vector3(-0.9f, 0.65f, -0.15f), new Vector3(-0.1f, 1.85f, 0.65f), out triangles, out meshes, out bvhNodes);
        }

        private static void CreateTransparentBox(
            Vector3 min,
            Vector3 max,
            out MeshTriangleData[] triangles,
            out MeshInfoData[] meshes,
            out BvhNodeData[] bvhNodes)
        {
            Vector3 p000 = new Vector3(min.x, min.y, min.z);
            Vector3 p001 = new Vector3(min.x, min.y, max.z);
            Vector3 p010 = new Vector3(min.x, max.y, min.z);
            Vector3 p011 = new Vector3(min.x, max.y, max.z);
            Vector3 p100 = new Vector3(max.x, min.y, min.z);
            Vector3 p101 = new Vector3(max.x, min.y, max.z);
            Vector3 p110 = new Vector3(max.x, max.y, min.z);
            Vector3 p111 = new Vector3(max.x, max.y, max.z);
            Vector3 color = new Vector3(0.75f, 0.28f, 0.22f);
            triangles = new[]
            {
                SurfaceTriangle(p000, p010, p110, Vector3.back, color, -1, opacity: 0.4f), SurfaceTriangle(p000, p110, p100, Vector3.back, color, -1, opacity: 0.4f),
                SurfaceTriangle(p001, p101, p111, Vector3.forward, color, -1, opacity: 0.4f), SurfaceTriangle(p001, p111, p011, Vector3.forward, color, -1, opacity: 0.4f),
                SurfaceTriangle(p000, p001, p011, Vector3.left, color, -1, opacity: 0.4f), SurfaceTriangle(p000, p011, p010, Vector3.left, color, -1, opacity: 0.4f),
                SurfaceTriangle(p100, p110, p111, Vector3.right, color, -1, opacity: 0.4f), SurfaceTriangle(p100, p111, p101, Vector3.right, color, -1, opacity: 0.4f),
                SurfaceTriangle(p000, p100, p101, Vector3.down, color, -1, opacity: 0.4f), SurfaceTriangle(p000, p101, p001, Vector3.down, color, -1, opacity: 0.4f),
                SurfaceTriangle(p010, p011, p111, Vector3.up, color, -1, opacity: 0.4f), SurfaceTriangle(p010, p111, p110, Vector3.up, color, -1, opacity: 0.4f)
            };
            CreateSingleLeafMesh(triangles, min, max, false, out meshes, out bvhNodes);
        }

        private static MeshTriangleData SurfaceTriangle(
            Vector3 vertex0,
            Vector3 vertex1,
            Vector3 vertex2,
            Vector3 normal,
            Vector3 color,
            int textureIndex,
            Vector3 emission = default(Vector3),
            int materialType = 0,
            float opacity = 1.0f)
        {
            return new MeshTriangleData
            {
                vertex0 = vertex0,
                vertex1 = vertex1,
                vertex2 = vertex2,
                normal = normal,
                color = color,
                smoothness = 0.25f,
                opacity = opacity,
                emission = emission,
                refraction = opacity < 1.0f ? 1.5f : 1.0f,
                materialType = materialType,
                meshIndex = 0,
                textureIndex = textureIndex
            };
        }

        private static void CreateSingleLeafMesh(
            MeshTriangleData[] triangles,
            Vector3 min,
            Vector3 max,
            bool isLight,
            out MeshInfoData[] meshes,
            out BvhNodeData[] bvhNodes)
        {
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
                    isLight = isLight ? 1 : 0
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

        private static Vector4[] ReadSignature(RenderTexture source, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = source;
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
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

            float[,] probes = { { 0.25f, 0.25f }, { 0.5f, 0.25f }, { 0.75f, 0.25f }, { 0.25f, 0.5f }, { 0.5f, 0.5f }, { 0.75f, 0.5f }, { 0.375f, 0.75f }, { 0.625f, 0.75f } };
            for (int i = 0; i < probes.GetLength(0); i++)
            {
                int x = Mathf.Clamp(Mathf.FloorToInt(probes[i, 0] * width), 0, width - 1);
                int y = Mathf.Clamp(Mathf.FloorToInt(probes[i, 1] * height), 0, height - 1);
                signature[i + 1] = pixels[y * width + x];
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
            var mismatches = new System.Collections.Generic.List<string>();
            for (int i = 0; i < expected.Length; i++)
            {
                AddSignatureMismatch(mismatches, i, "r", actual[i].x, expected[i].x);
                AddSignatureMismatch(mismatches, i, "g", actual[i].y, expected[i].y);
                AddSignatureMismatch(mismatches, i, "b", actual[i].z, expected[i].z);
            }

            if (mismatches.Count > 0)
            {
                Assert.Fail(
                    $"{scene} image signature changed:\n{string.Join("\n", mismatches)}\n\n" +
                    $"Actual signature:\n{FormatSignature(actual)}");
            }
        }

        private static void AddSignatureMismatch(
            System.Collections.Generic.List<string> mismatches,
            int signatureIndex,
            string channel,
            float actual,
            float expected)
        {
            float delta = actual - expected;
            if (Mathf.Abs(delta) > SignatureTolerance)
            {
                mismatches.Add(
                    $"signature[{signatureIndex}].{channel}: expected {expected:F8}, actual {actual:F8}, delta {delta:+0.00000000;-0.00000000}");
            }
        }

        private static string FormatSignature(Vector4[] signature)
        {
            return string.Join(",\n", Array.ConvertAll(signature, value =>
                $"new Vector4({value.x:F8}f, {value.y:F8}f, {value.z:F8}f, {value.w:F8}f)"));
        }

        private static RenderTexture CreateRenderTexture(int width, int height, RenderTextureFormat format)
        {
            var texture = new RenderTexture(width, height, 0, format) { enableRandomWrite = true };
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

        private static Texture2DArray CreateCheckerTextureArray()
        {
            var texture = new Texture2DArray(2, 2, 2, TextureFormat.RGBA32, false, true);
            texture.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white }, 0);
            texture.SetPixels(new[] { Color.red, Color.blue, Color.blue, Color.red }, 1);
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
