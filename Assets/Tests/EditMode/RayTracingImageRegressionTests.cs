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
        private const string DenoiserShaderPath = "Assets/Resources/RayTracingSpatialDenoiser.compute";
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
            public float specular;
            public float transmission;
            public int materialType;
            public int textureIndex;
            public int normalTextureIndex;
            public int parallaxTextureIndex;
            public Vector2 textureUvScale;
            public float parallaxStrength;
            public float minimumParallaxStrength;
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
            public int triangleStart;
            public int triangleCount;
            public float totalArea;
            public float padding;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MeshTriangleData
        {
            public Vector3 vertex0;
            public Vector3 vertex1;
            public Vector3 vertex2;
            public Vector3 normal;
            public Vector3 normal0;
            public Vector3 normal1;
            public Vector3 normal2;
            public Vector4 tangent0;
            public Vector4 tangent1;
            public Vector4 tangent2;
            public Vector3 color;
            public float smoothness;
            public float metallic;
            public Vector2 uv0;
            public Vector2 uv1;
            public Vector2 uv2;
            public float opacity;
            public Vector3 emission;
            public float refraction;
            public float specular;
            public float transmission;
            public int materialType;
            public int meshIndex;
            public int textureIndex;
            public int metallicRoughnessTextureIndex;
            public int normalTextureIndex;
            public int parallaxTextureIndex;
            public Vector2 textureUvScale;
            public float parallaxStrength;
            public float minimumParallaxStrength;
            public int interpolateNormals;
            public int lightIndex;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct CausticPhotonData
        {
            public Vector3 position;
            public Vector3 incomingDirection;
            public Vector3 power;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CausticTargetPairData
        {
            public int lightIndex;
            public int refractorType;
            public int refractorIndex;
            public int triangleStart;
            public int triangleCount;
            public float cumulativeProbability;
            public float selectionProbability;
            public float padding;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CausticTargetTriangleData
        {
            public int triangleIndex;
            public float cumulativeProbability;
            public float selectionProbability;
        }

        private sealed class CausticOptions
        {
            public int photonCount = 4096;
            public int seed = 1;
            public int frameIndex;
            public int maxBounces = 10;
            public float gatherRadius = 0.28f;
            public float intensity = 1.0f;
            public bool waterEnabled;
        }

        private sealed class CausticMap : IDisposable
        {
            public readonly ComputeBuffer photons;
            public readonly ComputeBuffer metadata;
            public readonly ComputeBuffer gridCellHeads;
            public readonly ComputeBuffer photonNext;
            public readonly ComputeBuffer targetPairs;
            public readonly ComputeBuffer targetTriangles;

            public CausticMap(int photonCapacity, CausticTargetPairData[] pairs, CausticTargetTriangleData[] triangles)
            {
                photons = new ComputeBuffer(photonCapacity, 36);
                metadata = new ComputeBuffer(6, sizeof(uint));
                gridCellHeads = new ComputeBuffer(65536, sizeof(int));
                photonNext = new ComputeBuffer(photonCapacity, sizeof(int));
                targetPairs = CreateBuffer(pairs.Length > 0 ? pairs : new CausticTargetPairData[1], 32);
                targetTriangles = CreateBuffer(triangles.Length > 0 ? triangles : new CausticTargetTriangleData[1], 12);
            }

            public void Dispose()
            {
                photons.Release();
                metadata.Release();
                gridCellHeads.Release();
                photonNext.Release();
                targetPairs.Release();
                targetTriangles.Release();
            }
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
        public void SphereLightReflection_HasNoDarkCenter()
        {
            float[,] probes =
            {
                { 0.5f, 0.5f },
                { 0.5f, 0.4375f },
                { 0.5f, 0.46875f },
                { 0.5f, 0.53125f },
                { 0.5f, 0.5625f },
                { 0.46875f, 0.5f },
                { 0.53125f, 0.5f }
            };
            Vector4[] signature = RenderSignature(
                Array.Empty<SphereData>(),
                false,
                new Vector3(0.0f, 2.0f, -6.0f),
                Quaternion.Euler(40.0f, 0.0f, 0.0f),
                lights: new[] { SphereLight(new Vector3(0.0f, 3.0f, 0.0f), new Vector3(30.0f, 30.0f, 30.0f), 1.0f) },
                width: 64,
                height: 64,
                shadowRandomness: 0.8f,
                receiverSmoothness: 1.0f,
                skyboxColor: Color.black,
                numberOfPasses: 256,
                lightFalloffScale: 1000.0f,
                probes: probes);

            float center = signature[1].x;
            float surroundingReflection = Mathf.Min(signature[3].x, signature[4].x);
            Assert.That(center, Is.GreaterThan(surroundingReflection * 0.5f),
                $"Sphere-light reflection center is dark. Probes:\n{FormatSignature(signature)}");
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

        [Test]
        public void NestedWaterAndClosedMeshGlassScene_CurrentImageBaseline_IsStable()
        {
            CreateSubmergedGlassCube(out MeshTriangleData[] triangles, out MeshInfoData[] meshes, out BvhNodeData[] bvhNodes);
            float[,] probes =
            {
                { 0.375f, 0.3125f }, { 0.5f, 0.3125f }, { 0.625f, 0.3125f },
                { 0.375f, 0.40625f }, { 0.5f, 0.40625f }, { 0.625f, 0.40625f },
                { 0.4375f, 0.5f }, { 0.5625f, 0.5f }
            };
            Vector4[] signature = RenderSignature(new[]
            {
                Sphere(new Vector3(0.0f, 0.25f, 2.0f), new Vector3(0.95f, 0.18f, 0.06f), 0.28f, 0.2f, 1.0f, 1.0f, 0)
            }, true, new Vector3(0.0f, 1.6f, -4.5f), Quaternion.Euler(10.0f, 0.0f, 0.0f),
                triangles, meshes, bvhNodes, probes: probes);

            AssertSignature("nested water and closed mesh glass", signature, NestedWaterClosedMeshGlassBaseline);
        }

        [TestCase(1, 1)]
        [TestCase(3, 5)]
        [TestCase(13, 7)]
        public void PartialThreadGroupDispatch_WritesEveryPixelWithoutOutOfBoundsAccess(int width, int height)
        {
            Vector4[] signature = RenderSignature(
                Array.Empty<SphereData>(),
                false,
                new Vector3(0.0f, 1.6f, -4.5f),
                Quaternion.identity,
                includeReceiver: false,
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

        [Test]
        public void CausticPhotonGeneration_FixedSeed_IsDeterministic()
        {
            CausticOptions options = new CausticOptions { photonCount = 2048 };
            SphereData[] spheres = CreateCausticSpheres();
            LightData[] lights = CreateCausticLights();

            CausticPhotonData[] first = GenerateCausticPhotons(spheres, lights, options, out uint[] firstMetadata);
            CausticPhotonData[] second = GenerateCausticPhotons(spheres, lights, options, out uint[] secondMetadata);

            CollectionAssert.AreEqual(firstMetadata, secondMetadata);
            Assert.That(firstMetadata[2], Is.EqualTo((uint)options.photonCount), "attempted photon count");
            Assert.That(firstMetadata[0], Is.GreaterThan(0u), "receiver-hit photon count");
            Assert.That(firstMetadata[1], Is.EqualTo(0u), "overflow count");
            Assert.That(firstMetadata[3], Is.EqualTo(firstMetadata[0]), "stored photon count");
            Assert.That(firstMetadata[4] + firstMetadata[5], Is.EqualTo(firstMetadata[3]),
                "every stored photon should be indexed or reported outside the test grid");

            SortPhotons(first);
            SortPhotons(second);
            Assert.That(first.Length, Is.EqualTo(second.Length));
            for (int i = 0; i < first.Length; i++)
            {
                AssertVector3(first[i].position, second[i].position, $"photon {i} position");
                AssertVector3(first[i].incomingDirection, second[i].incomingDirection, $"photon {i} direction");
                AssertVector3(first[i].power, second[i].power, $"photon {i} power");
                Assert.That(Mathf.Abs(first[i].position.y), Is.LessThan(0.005f), $"photon {i} receiver height");
                Assert.That(first[i].power.x + first[i].power.y + first[i].power.z, Is.GreaterThan(0.0f));
            }
        }

        [Test]
        public void CausticPhotonGeneration_ProgressiveFrameIndex_ChangesPhotonBatch()
        {
            SphereData[] spheres = CreateCausticSpheres();
            LightData[] lights = CreateCausticLights();
            CausticPhotonData[] first = GenerateCausticPhotons(
                spheres, lights, new CausticOptions { photonCount = 2048, frameIndex = 0 }, out uint[] firstMetadata);
            CausticPhotonData[] second = GenerateCausticPhotons(
                spheres, lights, new CausticOptions { photonCount = 2048, frameIndex = 1 }, out uint[] secondMetadata);

            Assert.That(firstMetadata[2], Is.EqualTo(2048u));
            Assert.That(secondMetadata[2], Is.EqualTo(2048u));
            Assert.That(first.Length, Is.GreaterThan(0));
            Assert.That(second.Length, Is.GreaterThan(0));
            SortPhotons(first);
            SortPhotons(second);
            bool differs = first.Length != second.Length;
            for (int i = 0; i < Mathf.Min(first.Length, second.Length) && !differs; i++)
            {
                differs = Vector3.SqrMagnitude(first[i].position - second[i].position) > 1e-8f;
            }
            Assert.That(differs, Is.True, "successive progressive batches should not reuse the same photon map");
        }

        [Test]
        public void CausticPhotonGeneration_TriangleLight_ProducesReceiverPhotons()
        {
            CausticOptions options = new CausticOptions { photonCount = 4096 };
            SphereData[] spheres = CreateCausticSpheres();
            Vector3 p0 = new Vector3(-1.4f, 6.8f, 1.7f);
            LightData[] lights =
            {
                TriangleLight(p0, new Vector3(2.8f, 0.0f, 0.0f), new Vector3(0.0f, 0.0f, 1.6f),
                    Vector3.down, new Vector3(20.0f, 19.0f, 17.0f))
            };

            CausticPhotonData[] photons = GenerateCausticPhotons(spheres, lights, options, out uint[] metadata);

            Assert.That(metadata[2], Is.EqualTo((uint)options.photonCount), "attempted photon count");
            Assert.That(metadata[0], Is.GreaterThan(128u), "receiver-hit photon count");
            Assert.That(metadata[1], Is.EqualTo(0u), "overflow count");
            Assert.That(photons.Length, Is.EqualTo((int)metadata[3]));
            foreach (CausticPhotonData photon in photons)
            {
                Assert.That(Mathf.Abs(photon.position.y), Is.LessThan(0.005f), "receiver height");
                Assert.That(photon.power.x + photon.power.y + photon.power.z, Is.GreaterThan(0.0f));
            }
        }

        [Test]
        public void CausticPhotonGeneration_MultiEventSphereTransport_UsesBounceBudget()
        {
            SphereData[] spheres = CreateCausticSpheres();
            LightData[] lights = CreateCausticLights();

            CausticPhotonData[] noReceiverEvent = GenerateCausticPhotons(
                spheres,
                lights,
                new CausticOptions { photonCount = 4096, maxBounces = 1 },
                out uint[] noReceiverMetadata);
            CausticPhotonData[] reflected = GenerateCausticPhotons(
                spheres,
                lights,
                new CausticOptions { photonCount = 4096, maxBounces = 2 },
                out uint[] reflectedMetadata);
            CausticPhotonData[] multiEvent = GenerateCausticPhotons(
                spheres,
                lights,
                new CausticOptions { photonCount = 4096, maxBounces = 3 },
                out uint[] multiEventMetadata);

            Assert.That(noReceiverMetadata[2], Is.EqualTo(4096u), "one-bounce attempted photon count");
            Assert.That(noReceiverEvent, Is.Empty, "one glass event cannot yet reach a receiver");
            Assert.That(reflectedMetadata[2], Is.EqualTo(4096u), "reflected attempted photon count");
            Assert.That(reflected.Length, Is.GreaterThan(0), "entry reflections should reach the receiver");
            Assert.That(multiEventMetadata[2], Is.EqualTo(4096u), "multi-event attempted photon count");
            Assert.That(multiEvent.Length, Is.GreaterThan(reflected.Length),
                "sphere entry and exit transmission should add receiver photons at the third event");
        }

        [Test]
        public void CausticPhotonGeneration_ClosedGlassMesh_ProducesReceiverPhotons()
        {
            CreateGlassCube(
                new Vector3(-0.8f, 0.2f, 1.7f),
                new Vector3(0.8f, 2.8f, 3.3f),
                out MeshTriangleData[] triangles,
                out MeshInfoData[] meshes,
                out BvhNodeData[] bvhNodes);
            LightData[] lights = { SphereLight(new Vector3(0.0f, 6.8f, 2.5f), new Vector3(20.0f, 19.0f, 17.0f), 0.24f) };

            CausticPhotonData[] photons = GenerateCausticPhotons(
                Array.Empty<SphereData>(), lights, triangles, meshes, bvhNodes,
                new CausticOptions { photonCount = 4096 }, out uint[] metadata);

            Assert.That(metadata[2], Is.EqualTo(4096u), "attempted photon count");
            Assert.That(metadata[0], Is.GreaterThan(0u), "receiver-hit photon count");
            Assert.That(metadata[1], Is.EqualTo(0u), "overflow count");
            Assert.That(photons.Length, Is.EqualTo((int)metadata[3]));
            foreach (CausticPhotonData photon in photons)
            {
                Assert.That(Mathf.Abs(photon.position.y), Is.LessThan(0.005f), "receiver height");
                Assert.That(photon.power.x + photon.power.y + photon.power.z, Is.GreaterThan(0.0f));
            }
        }

        [Test]
        public void CausticPhotonGeneration_WaterVolume_ProducesReceiverPhotons()
        {
            LightData[] lights = CreateCausticLights();

            CausticPhotonData[] photons = GenerateCausticPhotons(
                Array.Empty<SphereData>(), lights,
                new CausticOptions { photonCount = 4096, waterEnabled = true }, out uint[] metadata);

            Assert.That(metadata[2], Is.EqualTo(4096u), "attempted photon count");
            Assert.That(metadata[0], Is.GreaterThan(0u), "water transmission should reach the receiver");
            Assert.That(metadata[1], Is.EqualTo(0u), "overflow count");
            foreach (CausticPhotonData photon in photons)
            {
                Assert.That(Mathf.Abs(photon.position.y), Is.LessThan(0.005f), "receiver height");
                Assert.That(photon.power.x + photon.power.y + photon.power.z, Is.GreaterThan(0.0f));
            }
        }

        [Test]
        public void SphereCaustic_DebugImageBaseline_IsStable()
        {
            Vector4[] signature = RenderCausticSignature(new CausticOptions());
            AssertSignature("sphere photon caustic", signature, SphereCausticBaseline);
        }

        [Test]
        public void CausticDebugImage_PhotonCountChange_PreservesAverageEnergy()
        {
            Vector4[] low = RenderCausticSignature(new CausticOptions { photonCount = 2048 });
            Vector4[] high = RenderCausticSignature(new CausticOptions { photonCount = 8192 });

            AssertRelativeEnergy(low[0], high[0], 0.18f, "photon count");
        }

        [Test]
        public void CausticDebugImage_GatherRadiusChange_PreservesAverageEnergyAndSharpensPeak()
        {
            Vector4[] narrow = RenderCausticSignature(new CausticOptions { photonCount = 8192, gatherRadius = 0.20f });
            Vector4[] wide = RenderCausticSignature(new CausticOptions { photonCount = 8192, gatherRadius = 0.40f });

            AssertRelativeEnergy(narrow[0], wide[0], 0.25f, "gather radius");
            Assert.That(MaxProbeLuminance(narrow), Is.GreaterThan(MaxProbeLuminance(wide)),
                $"A narrower gather should have a sharper sampled peak.\nNarrow:\n{FormatSignature(narrow)}\nWide:\n{FormatSignature(wide)}");
        }

        // Average HDR color followed by eight fixed pixel probes. These values intentionally lock
        // current output, including approximations; update only after reviewing an expected change.
        private static readonly Vector4[] ReflectionBaseline =
        {
            new Vector4(0.24528870f, 0.37967120f, 0.54246710f, 1.0f),
            new Vector4(0.31776680f, 0.46988330f, 0.65812360f, 1.0f),
            new Vector4(0.09328625f, 0.00868617f, 0.00301272f, 1.0f),
            new Vector4(0.23362680f, 0.42054410f, 0.62715080f, 1.0f),
            new Vector4(0.32049640f, 0.52216240f, 0.71244370f, 1.0f),
            new Vector4(0.18868000f, 0.09107766f, 0.07389045f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static readonly Vector4[] GlassBaseline =
        {
            new Vector4(0.22502670f, 0.39189720f, 0.57762590f, 1.0f),
            new Vector4(0.27923740f, 0.49491320f, 0.69961660f, 1.0f),
            new Vector4(0.01259302f, 0.01352454f, 0.03193457f, 1.0f),
            new Vector4(0.23362190f, 0.42053790f, 0.62714530f, 1.0f),
            new Vector4(0.32048560f, 0.52215090f, 0.71243460f, 1.0f),
            new Vector4(0.01259302f, 0.00704346f, 0.01204914f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static readonly Vector4[] InsideGlassCameraBaseline =
        {
            new Vector4(0.10890430f, 0.32960290f, 0.58115630f, 1.0f),
            new Vector4(0.13743680f, 0.41616670f, 0.68381760f, 1.0f),
            new Vector4(0.14077130f, 0.42367290f, 0.69050750f, 1.0f),
            new Vector4(0.15848610f, 0.45310660f, 0.71314140f, 1.0f),
            new Vector4(0.09136747f, 0.33912740f, 0.63450150f, 1.0f),
            new Vector4(0.04174894f, 0.02055860f, 0.02197600f, 1.0f),
            new Vector4(0.10657580f, 0.35463530f, 0.63019130f, 1.0f),
            new Vector4(0.10678040f, 0.35482970f, 0.63025460f, 1.0f),
            new Vector4(0.10697200f, 0.35501160f, 0.63031360f, 1.0f)
        };

        private static readonly Vector4[] WaterBaseline =
        {
            new Vector4(0.18173040f, 0.34718470f, 0.54317370f, 1.0f),
            new Vector4(0.11754310f, 0.26860310f, 0.47664970f, 1.0f), new Vector4(0.02927951f, 0.08683124f, 0.20704730f, 1.0f),
            new Vector4(0.24839830f, 0.47703760f, 0.68394960f, 1.0f), new Vector4(0.16319830f, 0.34220730f, 0.55730190f, 1.0f),
            new Vector4(0.14819820f, 0.26874160f, 0.44220630f, 1.0f), new Vector4(0.07051557f, 0.16169770f, 0.32682100f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static readonly Vector4[] NestedWaterGlassBaseline =
        {
            new Vector4(0.18149750f, 0.34698710f, 0.54311590f, 1.0f),
            new Vector4(0.11754290f, 0.26860270f, 0.47664930f, 1.0f), new Vector4(0.02930538f, 0.08688708f, 0.20714760f, 1.0f),
            new Vector4(0.24839830f, 0.47703760f, 0.68394960f, 1.0f), new Vector4(0.16319840f, 0.34220730f, 0.55730190f, 1.0f),
            new Vector4(0.10823700f, 0.23281910f, 0.42639530f, 1.0f), new Vector4(0.07051557f, 0.16169770f, 0.32682100f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static readonly Vector4[] UnderwaterCameraBaseline =
        {
            new Vector4(0.03493490f, 0.12596900f, 0.27270570f, 1.0f),
            new Vector4(0.02053524f, 0.08846512f, 0.22868340f, 1.0f), new Vector4(0.04203858f, 0.13829840f, 0.30726390f, 1.0f),
            new Vector4(0.04517544f, 0.16485880f, 0.36035240f, 1.0f), new Vector4(0.00754808f, 0.05908050f, 0.18263530f, 1.0f),
            new Vector4(0.01259302f, 0.00665446f, 0.00627632f, 1.0f), new Vector4(0.01195038f, 0.11639280f, 0.32787310f, 1.0f),
            new Vector4(0.01341614f, 0.07492283f, 0.20799020f, 1.0f), new Vector4(0.01850257f, 0.07965415f, 0.20648710f, 1.0f)
        };

        private static readonly Vector4[] ClosedMeshGlassBaseline =
        {
            new Vector4(0.23386160f, 0.42362850f, 0.62396780f, 1.0f),
            new Vector4(0.28581550f, 0.49898240f, 0.70027730f, 1.0f), new Vector4(0.02817204f, 0.00970107f, 0.00976967f, 1.0f),
            new Vector4(0.22758460f, 0.44473420f, 0.66268700f, 1.0f), new Vector4(0.32048560f, 0.52215090f, 0.71243460f, 1.0f),
            new Vector4(0.07051557f, 0.03160396f, 0.03853699f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static readonly Vector4[] NestedWaterClosedMeshGlassBaseline =
        {
            new Vector4(0.18070360f, 0.34529040f, 0.54096070f, 1.0f),
            new Vector4(0.22306350f, 0.44508800f, 0.65819670f, 1.0f),
            new Vector4(0.07771677f, 0.19563420f, 0.38385990f, 1.0f),
            new Vector4(0.14704260f, 0.31458840f, 0.52744710f, 1.0f),
            new Vector4(0.15809010f, 0.33278250f, 0.54688910f, 1.0f),
            new Vector4(0.14189040f, 0.29741820f, 0.50610320f, 1.0f),
            new Vector4(0.03751515f, 0.09106692f, 0.20648710f, 1.0f),
            new Vector4(0.22847110f, 0.41391780f, 0.62113910f, 1.0f),
            new Vector4(0.03751515f, 0.09106692f, 0.20648710f, 1.0f)
        };

        private static readonly Vector4[] TexturedMeshBaseline =
        {
            new Vector4(0.59012540f, 0.43600340f, 0.78018610f, 1.0f), new Vector4(0.83489880f, 0.05251009f, 0.86672280f, 1.0f),
            new Vector4(0.76660980f, 0.02982532f, 0.80148810f, 1.0f), new Vector4(0.65926810f, 0.01583910f, 0.74425760f, 1.0f),
            new Vector4(0.87942510f, 0.09330225f, 0.89870790f, 1.0f), new Vector4(0.82271610f, 0.05041737f, 0.85335210f, 1.0f),
            new Vector4(0.66739960f, 0.01714872f, 0.75128090f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static readonly Vector4[] MeshLightBaseline =
        {
            new Vector4(0.58112820f, 0.66808700f, 0.74601840f, 1.0f), new Vector4(0.95802680f, 0.94975860f, 0.93178500f, 1.0f),
            new Vector4(0.10561710f, 0.14010540f, 0.20529150f, 1.0f), new Vector4(0.94573960f, 0.93872450f, 0.91982350f, 1.0f),
            new Vector4(0.32306500f, 0.52341760f, 0.71273540f, 1.0f), new Vector4(0.98271740f, 0.92137300f, 0.70826300f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static readonly Vector4[] TransparentSphereShadowBaseline =
        {
            new Vector4(0.58512500f, 0.67768970f, 0.77665080f, 1.0f), new Vector4(0.82931160f, 0.83592620f, 0.85907860f, 1.0f),
            new Vector4(0.87332720f, 0.87375580f, 0.88619090f, 1.0f), new Vector4(0.89842370f, 0.89440270f, 0.89858200f, 1.0f),
            new Vector4(0.62780290f, 0.68533930f, 0.76859290f, 1.0f), new Vector4(0.71810750f, 0.76255630f, 0.82738180f, 1.0f),
            new Vector4(0.71800480f, 0.75456530f, 0.81407000f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.41652840f, 0.60685370f, 0.73313520f, 1.0f)
        };

        private static readonly Vector4[] TransparentMeshShadowBaseline =
        {
            new Vector4(0.58701330f, 0.66842480f, 0.76629810f, 1.0f), new Vector4(0.83024440f, 0.83358560f, 0.85404960f, 1.0f),
            new Vector4(0.87332720f, 0.87375580f, 0.88619090f, 1.0f), new Vector4(0.89842370f, 0.89440270f, 0.89858200f, 1.0f),
            new Vector4(0.62780290f, 0.68533930f, 0.76859290f, 1.0f), new Vector4(0.71810750f, 0.76255630f, 0.82738180f, 1.0f),
            new Vector4(0.71800480f, 0.75456530f, 0.81407000f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f)
        };

        private static readonly Vector4[] StackedTransparentShadowBaseline =
        {
            new Vector4(0.58223590f, 0.66783750f, 0.76599900f, 1.0f), new Vector4(0.83024440f, 0.83358560f, 0.85404960f, 1.0f),
            new Vector4(0.87332720f, 0.87375580f, 0.88619090f, 1.0f), new Vector4(0.89842370f, 0.89440270f, 0.89858200f, 1.0f),
            new Vector4(0.62780290f, 0.68533930f, 0.76859290f, 1.0f), new Vector4(0.71810750f, 0.76255630f, 0.82738180f, 1.0f),
            new Vector4(0.71800480f, 0.75456530f, 0.81407000f, 1.0f), new Vector4(0.26689890f, 0.46158100f, 0.66307280f, 1.0f),
            new Vector4(0.41652840f, 0.60685370f, 0.73313520f, 1.0f)
        };

        private static readonly Vector4[] SphereCausticBaseline =
        {
            new Vector4(0.00244124f, 0.00232730f, 0.00208725f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f), new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f), new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f), new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
            new Vector4(0.0f, 0.0f, 0.0f, 1.0f), new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
            new Vector4(1.85446500f, 1.76887500f, 1.58700800f, 1.0f)
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
                transmission = 1.0f,
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
            int height = ImageSize,
            float shadowRandomness = 0.0f,
            float receiverSmoothness = 0.9f,
            Color? skyboxColor = null,
            int numberOfPasses = 8,
            float lightFalloffScale = 0.16f,
            float[,] probes = null,
            CausticOptions caustics = null,
            bool includePeak = false,
            bool includeReceiver = true)
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Compute shaders are not supported by the active graphics device.");
            }

            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeShaderPath);
            Assert.That(shader, Is.Not.Null);
            string kernelName = caustics == null ? "CSMain" : "CSCausticsDebug";
            if (!shader.HasKernel(kernelName))
            {
                Assert.Ignore($"The active graphics device did not compile {kernelName}. Run without -nographics.");
            }

            int kernel = shader.FindKernel(kernelName);
            triangles = triangles ?? Array.Empty<MeshTriangleData>();
            meshes = meshes ?? Array.Empty<MeshInfoData>();
            bvhNodes = bvhNodes ?? Array.Empty<BvhNodeData>();
            if (includeReceiver)
            {
                AppendReceiver(ref triangles, ref meshes, ref bvhNodes, receiverSmoothness);
            }
            lights = lights ?? Array.Empty<LightData>();
            var result = CreateRenderTexture(width, height, RenderTextureFormat.ARGBFloat);
            var accumulation = CreateRenderTexture(width, height, RenderTextureFormat.ARGBFloat);
            var beauty = CreateRenderTexture(width, height, RenderTextureFormat.ARGBFloat);
            var featureNormal = CreateRenderTexture(width, height, RenderTextureFormat.ARGBHalf);
            var featureAlbedo = CreateRenderTexture(width, height, RenderTextureFormat.ARGBHalf);
            var featureDepth = CreateRenderTexture(width, height, RenderTextureFormat.RHalf);
            var featureIdentity = CreateRenderTexture(width, height, RenderTextureFormat.RFloat);
            var featureValidity = CreateRenderTexture(width, height, RenderTextureFormat.RHalf);
            var skybox = CreateSolidTexture(skyboxColor ?? new Color(0.18f, 0.32f, 0.58f, 1.0f));
            bool ownsMeshTextures = meshTextures == null;
            meshTextures = meshTextures ?? CreateMeshTextureArray();
            var meshDataTextures = CreateMeshTextureArray();
            var meshNormalTextures = CreateNormalTextureArray();
            var meshParallaxTextures = CreateParallaxTextureArray();
            ComputeBuffer sphereBuffer = CreateBuffer(spheres, 92);
            ComputeBuffer lightBuffer = CreateBuffer(lights, 88);
            ComputeBuffer triangleBuffer = CreateBuffer(triangles, 252);
            ComputeBuffer meshBuffer = CreateBuffer(meshes, 48);
            ComputeBuffer bvhBuffer = CreateBuffer(bvhNodes, 48);
            ComputeBuffer topLevelBuffer = CreateDummyBuffer(48);
            ComputeBuffer shadowBuffer = CreateDummyBuffer(48);
            ComputeBuffer meshLightCdfBuffer = CreateDummyBuffer(4);
            CausticMap causticMap = null;

            try
            {
                if (caustics == null)
                {
                    shader.DisableKeyword("DEBUG_RENDER");
                    shader.DisableKeyword("CAUSTICS_ENABLED");
                }
                else
                {
                    shader.DisableKeyword("DEBUG_RENDER");
                    shader.EnableKeyword("CAUSTICS_ENABLED");
                    causticMap = DispatchCausticPhotons(
                        shader, sphereBuffer, lightBuffer, triangleBuffer, meshBuffer, bvhBuffer,
                        topLevelBuffer, shadowBuffer, spheres.Length, lights.Length, triangles.Length,
                        meshes.Length, caustics);
                    SetCausticBuffers(shader, kernel, causticMap);
                    SetCausticParameters(shader, caustics);
                }
                shader.SetTexture(kernel, "Result", result);
                shader.SetTexture(kernel, "AccumulationResult", accumulation);
                shader.SetTexture(kernel, "Beauty", beauty);
                shader.SetTexture(kernel, "_SkyboxTexture", skybox);
                shader.SetTexture(kernel, "_MeshAlbedoTextures", meshTextures);
                shader.SetTexture(kernel, "_MeshMetallicRoughnessTextures", meshDataTextures);
                shader.SetTexture(kernel, "_MeshNormalTextures", meshNormalTextures);
                shader.SetTexture(kernel, "_MeshParallaxTextures", meshParallaxTextures);
                shader.SetBuffer(kernel, "_Spheres", sphereBuffer);
                shader.SetBuffer(kernel, "_Lights", lightBuffer);
                shader.SetBuffer(kernel, "_Triangles", triangleBuffer);
                shader.SetBuffer(kernel, "_Meshes", meshBuffer);
                shader.SetBuffer(kernel, "_BvhNodes", bvhBuffer);
                shader.SetBuffer(kernel, "_TopLevelBvhNodes", topLevelBuffer);
                shader.SetBuffer(kernel, "_ShadowBvhNodes", shadowBuffer);
                shader.SetBuffer(kernel, "_MeshLightTriangleCdf", meshLightCdfBuffer);

                // Unity view-space camera rays point down -Z; cameraToWorld includes that handedness
                // conversion, unlike Transform.localToWorldMatrix.
                Matrix4x4 cameraToWorld = Matrix4x4.TRS(
                    cameraPosition,
                    cameraRotation,
                    new Vector3(1.0f, 1.0f, -1.0f));
                shader.SetMatrix("_CameraToWorld", cameraToWorld);
                shader.SetMatrix("_CameraInverseProjection", Matrix4x4.Perspective(48.0f, 1.0f, 0.1f, 100.0f).inverse);
                shader.SetVector("_FrameJitterNdc", Vector4.zero);
                shader.SetInt("_UseTemporalJitter", 0);
                shader.SetVector("_SkyboxLight", Vector4.one);
                shader.SetInt("_Seed", 1);
                shader.SetInt("_SampleOffset", 0);
                shader.SetInt("_NumberOfPasses", numberOfPasses);
                shader.SetFloat("_SubpixelJitterScale", 1.0f);
                shader.SetInt("_NumBounces", 6);
                shader.SetInt("_DebugRenderMode", 0);
                shader.SetInt("_UseFrameAccumulation", 0);
                shader.SetInt("_AccumulatedFrameCount", 0);
                shader.SetInt("_MaxLightSamples", lights.Length);
                shader.SetInt("_LightSamplingStrategy", 0);
                shader.SetInt("_LightSampleCount", 1);
                shader.SetInt("_ShadowQuality", 0);
                shader.SetFloat("_ShadowRandomness", shadowRandomness);
                shader.SetFloat("_LightFalloffScale", lightFalloffScale);
                shader.SetFloat("_FocalDistance", 100.0f);
                shader.SetFloat("_ApertureRadius", 0.0f);
                shader.SetInt("_ApertureBladeCount", 0);
                shader.SetFloat("_ApertureBladeRotation", 0.0f);
                shader.SetFloat("_AnamorphicRatio", 1.0f);
                shader.SetFloat("_Exposure", 1.0f);
                shader.SetFloat("_FireflyClamp", 0.0f);
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
                SetFogDisabled(shader);
                shader.DisableKeyword("FOG_ENABLED");

                shader.Dispatch(kernel, Mathf.CeilToInt(width / 4.0f), Mathf.CeilToInt(height / 4.0f), 1);
                if (caustics == null)
                {
                    ComputeShader denoiser = AssetDatabase.LoadAssetAtPath<ComputeShader>(DenoiserShaderPath);
                    Assert.That(denoiser, Is.Not.Null);
                    int presentKernel = denoiser.FindKernel("CSPresent");
                    denoiser.SetTexture(presentKernel, "InputBeauty", beauty);
                    denoiser.SetTexture(presentKernel, "PresentationResult", result);
                    denoiser.SetFloat("_Exposure", 1.0f);
                    denoiser.Dispatch(presentKernel, Mathf.CeilToInt(width / 8.0f), Mathf.CeilToInt(height / 8.0f), 1);
                }
                return ReadSignature(result, width, height, probes, includePeak);
            }
            finally
            {
                shader.DisableKeyword("DEBUG_RENDER");
                shader.DisableKeyword("CAUSTICS_ENABLED");
                shader.DisableKeyword("FOG_ENABLED");
                causticMap?.Dispose();
                sphereBuffer.Release();
                lightBuffer.Release();
                triangleBuffer.Release();
                meshBuffer.Release();
                bvhBuffer.Release();
                topLevelBuffer.Release();
                shadowBuffer.Release();
                meshLightCdfBuffer.Release();
                result.Release();
                accumulation.Release();
                beauty.Release();
                featureNormal.Release();
                featureAlbedo.Release();
                featureDepth.Release();
                featureIdentity.Release();
                featureValidity.Release();
                UnityEngine.Object.DestroyImmediate(skybox);
                if (ownsMeshTextures)
                {
                    UnityEngine.Object.DestroyImmediate(meshTextures);
                }
                UnityEngine.Object.DestroyImmediate(meshDataTextures);
                UnityEngine.Object.DestroyImmediate(meshNormalTextures);
                UnityEngine.Object.DestroyImmediate(meshParallaxTextures);
            }
        }

        private static Vector4[] RenderCausticSignature(CausticOptions options)
        {
            float[,] probes =
            {
                { 0.375f, 0.0625f }, { 0.5f, 0.0625f }, { 0.625f, 0.0625f },
                { 0.375f, 0.125f }, { 0.5f, 0.125f }, { 0.625f, 0.125f },
                { 0.4375f, 0.1875f }, { 0.5625f, 0.1875f }
            };
            return RenderSignature(
                CreateCausticSpheres(),
                false,
                new Vector3(0.0f, 5.4f, -10.5f),
                Quaternion.Euler(19.0f, 0.0f, 0.0f),
                lights: CreateCausticLights(),
                width: 48,
                height: 48,
                receiverSmoothness: 0.05f,
                skyboxColor: Color.black,
                numberOfPasses: 8,
                probes: probes,
                caustics: options,
                includePeak: true);
        }

        private static SphereData[] CreateCausticSpheres()
        {
            return new[]
            {
                Sphere(new Vector3(0.0f, 1.32f, 2.5f), new Vector3(238.0f / 255.0f, 248.0f / 255.0f, 1.0f),
                    1.3f, 1.0f, 0.04f, 1.52f, 2)
            };
        }

        private static LightData[] CreateCausticLights()
        {
            return new[] { SphereLight(new Vector3(0.0f, 6.8f, 2.5f), new Vector3(20.0f, 19.0f, 17.0f), 0.24f) };
        }

        private static CausticPhotonData[] GenerateCausticPhotons(
            SphereData[] spheres,
            LightData[] lights,
            CausticOptions options,
            out uint[] metadata)
        {
            return GenerateCausticPhotons(
                spheres, lights, Array.Empty<MeshTriangleData>(), Array.Empty<MeshInfoData>(),
                Array.Empty<BvhNodeData>(), options, out metadata);
        }

        private static CausticPhotonData[] GenerateCausticPhotons(
            SphereData[] spheres,
            LightData[] lights,
            MeshTriangleData[] triangles,
            MeshInfoData[] meshes,
            BvhNodeData[] bvhNodes,
            CausticOptions options,
            out uint[] metadata)
        {
            AppendReceiver(ref triangles, ref meshes, ref bvhNodes, 0.05f);
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Compute shaders are not supported by the active graphics device.");
            }

            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeShaderPath);
            Assert.That(shader, Is.Not.Null);
            shader.EnableKeyword("CAUSTICS_ENABLED");
            if (!shader.HasKernel("TraceCausticPhotons"))
            {
                shader.DisableKeyword("CAUSTICS_ENABLED");
                Assert.Ignore("The active graphics device did not compile the caustics kernels. Run without -nographics.");
            }

            ComputeBuffer sphereBuffer = CreateBuffer(spheres, 92);
            ComputeBuffer lightBuffer = CreateBuffer(lights, 88);
            ComputeBuffer triangleBuffer = CreateBuffer(triangles, 252);
            ComputeBuffer meshBuffer = CreateBuffer(meshes, 48);
            ComputeBuffer bvhBuffer = CreateBuffer(bvhNodes, 48);
            ComputeBuffer topLevelBuffer = CreateDummyBuffer(48);
            ComputeBuffer shadowBuffer = CreateDummyBuffer(48);
            CausticMap map = null;
            try
            {
                map = DispatchCausticPhotons(
                    shader, sphereBuffer, lightBuffer, triangleBuffer, meshBuffer, bvhBuffer,
                    topLevelBuffer, shadowBuffer, spheres.Length, lights.Length, triangles.Length, meshes.Length, options);
                metadata = new uint[6];
                map.metadata.GetData(metadata);
                var photons = new CausticPhotonData[checked((int)metadata[3])];
                if (photons.Length > 0)
                {
                    map.photons.GetData(photons, 0, 0, photons.Length);
                }
                return photons;
            }
            finally
            {
                shader.DisableKeyword("CAUSTICS_ENABLED");
                map?.Dispose();
                sphereBuffer.Release();
                lightBuffer.Release();
                triangleBuffer.Release();
                meshBuffer.Release();
                bvhBuffer.Release();
                topLevelBuffer.Release();
                shadowBuffer.Release();
            }
        }

        private static CausticMap DispatchCausticPhotons(
            ComputeShader shader,
            ComputeBuffer sphereBuffer,
            ComputeBuffer lightBuffer,
            ComputeBuffer triangleBuffer,
            ComputeBuffer meshBuffer,
            ComputeBuffer bvhBuffer,
            ComputeBuffer topLevelBuffer,
            ComputeBuffer shadowBuffer,
            int sphereCount,
            int lightCount,
            int triangleCount,
            int meshCount,
            CausticOptions options)
        {
            int clearKernel = shader.FindKernel("ClearCausticPhotons");
            int traceKernel = shader.FindKernel("TraceCausticPhotons");
            int clearGridKernel = shader.FindKernel("ClearCausticGrid");
            int buildGridKernel = shader.FindKernel("BuildCausticGrid");
            BuildCausticTargets(sphereBuffer, lightBuffer, triangleBuffer, meshBuffer,
                sphereCount, lightCount, triangleCount, meshCount, options.waterEnabled, out CausticTargetPairData[] targetPairs,
                out CausticTargetTriangleData[] targetTriangles);
            var map = new CausticMap(options.photonCount, targetPairs, targetTriangles);
            var meshDataTextures = CreateMeshTextureArray();
            var meshNormalTextures = CreateNormalTextureArray();
            var meshParallaxTextures = CreateParallaxTextureArray();
            SetCausticParameters(shader, options);
            shader.SetInt("_NumSpheres", sphereCount);
            shader.SetInt("_NumLights", lightCount);
            shader.SetInt("_NumTriangles", triangleCount);
            shader.SetInt("_NumMeshes", meshCount);
            shader.SetInt("_NumTopLevelBvhNodes", 0);
            shader.SetInt("_NumShadowBvhNodes", 0);
            shader.SetInt("_NumCausticTargetPairs", targetPairs.Length);
            SetWater(shader, options.waterEnabled);
            SetCausticBuffers(shader, clearKernel, map);
            SetCausticBuffers(shader, traceKernel, map);
            SetCausticBuffers(shader, clearGridKernel, map);
            SetCausticBuffers(shader, buildGridKernel, map);
            shader.SetBuffer(traceKernel, "_Spheres", sphereBuffer);
            shader.SetBuffer(traceKernel, "_Lights", lightBuffer);
            shader.SetBuffer(traceKernel, "_Triangles", triangleBuffer);
            shader.SetBuffer(traceKernel, "_Meshes", meshBuffer);
            shader.SetBuffer(traceKernel, "_BvhNodes", bvhBuffer);
            shader.SetBuffer(traceKernel, "_TopLevelBvhNodes", topLevelBuffer);
            shader.SetBuffer(traceKernel, "_ShadowBvhNodes", shadowBuffer);
            ComputeBuffer meshLightCdfBuffer = CreateDummyBuffer(4);
            shader.SetBuffer(traceKernel, "_MeshLightTriangleCdf", meshLightCdfBuffer);
            shader.SetBuffer(traceKernel, "_CausticTargetPairs", map.targetPairs);
            shader.SetBuffer(traceKernel, "_CausticTargetTriangles", map.targetTriangles);
            shader.SetTexture(traceKernel, "_MeshMetallicRoughnessTextures", meshDataTextures);
            shader.SetTexture(traceKernel, "_MeshNormalTextures", meshNormalTextures);
            shader.SetTexture(traceKernel, "_MeshParallaxTextures", meshParallaxTextures);
            shader.Dispatch(clearKernel, 1, 1, 1);
            shader.Dispatch(traceKernel, Mathf.CeilToInt(options.photonCount / 32.0f), 1, 1);
            shader.Dispatch(clearGridKernel, 1024, 1, 1);
            shader.Dispatch(buildGridKernel, Mathf.CeilToInt(options.photonCount / 32.0f), 1, 1);
            meshLightCdfBuffer.Release();
            UnityEngine.Object.DestroyImmediate(meshDataTextures);
            UnityEngine.Object.DestroyImmediate(meshNormalTextures);
            UnityEngine.Object.DestroyImmediate(meshParallaxTextures);
            return map;
        }

        private static void BuildCausticTargets(ComputeBuffer spheres, ComputeBuffer lights, ComputeBuffer triangles,
            ComputeBuffer meshes, int sphereCount, int lightCount, int triangleCount, int meshCount, bool includeWater,
            out CausticTargetPairData[] pairs, out CausticTargetTriangleData[] targetTriangles)
        {
            var sphereData = new SphereData[sphereCount];
            var lightData = new LightData[lightCount];
            var triangleData = new MeshTriangleData[triangleCount];
            var meshData = new MeshInfoData[meshCount];
            if (sphereCount > 0) spheres.GetData(sphereData, 0, 0, sphereCount);
            if (lightCount > 0) lights.GetData(lightData, 0, 0, lightCount);
            if (triangleCount > 0) triangles.GetData(triangleData, 0, 0, triangleCount);
            if (meshCount > 0) meshes.GetData(meshData, 0, 0, meshCount);

            var pairList = new System.Collections.Generic.List<CausticTargetPairData>();
            var targetTriangleList = new System.Collections.Generic.List<CausticTargetTriangleData>();
            for (int lightIndex = 0; lightIndex < lightData.Length; lightIndex++)
            {
                for (int sphereIndex = 0; sphereIndex < sphereData.Length; sphereIndex++)
                {
                    SphereData sphere = sphereData[sphereIndex];
                    if ((sphere.materialType == 2 || sphere.opacity < 1.0f) && sphere.opacity < 1.0f)
                    {
                        pairList.Add(new CausticTargetPairData
                        {
                            lightIndex = lightIndex,
                            refractorType = 0,
                            refractorIndex = sphereIndex
                        });
                    }
                }

                for (int meshIndex = 0; meshIndex < meshData.Length; meshIndex++)
                {
                    MeshInfoData mesh = meshData[meshIndex];
                    if (mesh.isLight != 0 || mesh.triangleCount <= 0)
                    {
                        continue;
                    }
                    MeshTriangleData material = triangleData[mesh.triangleStart];
                    if (!((material.materialType == 2 || material.opacity < 1.0f) && material.opacity < 1.0f))
                    {
                        continue;
                    }

                    int targetStart = targetTriangleList.Count;
                    float totalArea = 0.0f;
                    for (int i = 0; i < mesh.triangleCount; i++)
                    {
                        MeshTriangleData triangle = triangleData[mesh.triangleStart + i];
                        totalArea += 0.5f * Vector3.Cross(
                            triangle.vertex1 - triangle.vertex0,
                            triangle.vertex2 - triangle.vertex0).magnitude;
                        targetTriangleList.Add(new CausticTargetTriangleData
                        {
                            triangleIndex = mesh.triangleStart + i,
                            cumulativeProbability = totalArea
                        });
                    }
                    int lastTriangle = targetTriangleList.Count - 1;
                    float previous = 0.0f;
                    for (int i = targetStart; i < targetTriangleList.Count; i++)
                    {
                        CausticTargetTriangleData target = targetTriangleList[i];
                        float cumulative = target.cumulativeProbability / totalArea;
                        target.selectionProbability = cumulative - previous;
                        target.cumulativeProbability = i == lastTriangle ? 1.0f : cumulative;
                        previous = cumulative;
                        targetTriangleList[i] = target;
                    }
                    pairList.Add(new CausticTargetPairData
                    {
                        lightIndex = lightIndex,
                        refractorType = 1,
                        refractorIndex = meshIndex,
                        triangleStart = targetStart,
                        triangleCount = mesh.triangleCount
                    });
                }

                if (includeWater)
                {
                    pairList.Add(new CausticTargetPairData
                    {
                        lightIndex = lightIndex,
                        refractorType = 2,
                        refractorIndex = -1
                    });
                }
            }

            for (int i = 0; i < pairList.Count; i++)
            {
                CausticTargetPairData pair = pairList[i];
                pair.selectionProbability = 1.0f / pairList.Count;
                pair.cumulativeProbability = (i + 1.0f) / pairList.Count;
                pairList[i] = pair;
            }
            pairs = pairList.ToArray();
            targetTriangles = targetTriangleList.ToArray();
        }

        private static void SetCausticParameters(ComputeShader shader, CausticOptions options)
        {
            shader.SetInt("_CausticPhotonCapacity", options.photonCount);
            shader.SetInt("_CausticPhotonAttemptCount", options.photonCount);
            shader.SetInt("_CausticMaxBounces", options.maxBounces);
            shader.SetInt("_CausticSeed", options.seed);
            shader.SetInt("_CausticFrameIndex", options.frameIndex);
            shader.SetFloat("_CausticGatherRadius", options.gatherRadius);
            shader.SetFloat("_CausticIntensity", options.intensity);
            shader.SetVector("_CausticGridMin", new Vector3(-9.0f, -1.0f, -9.0f));
            shader.SetFloat("_CausticGridCellSize", options.gatherRadius);
            shader.SetInts("_CausticGridDimensions", 64, 16, 64);
            shader.SetInt("_CausticGridCellCount", 65536);
        }

        private static void SetCausticBuffers(ComputeShader shader, int kernel, CausticMap map)
        {
            shader.SetBuffer(kernel, "_CausticPhotons", map.photons);
            shader.SetBuffer(kernel, "_CausticPhotonMetadata", map.metadata);
            shader.SetBuffer(kernel, "_CausticGridCellHeads", map.gridCellHeads);
            shader.SetBuffer(kernel, "_CausticPhotonNext", map.photonNext);
            shader.SetBuffer(kernel, "_CausticTargetPairs", map.targetPairs);
            shader.SetBuffer(kernel, "_CausticTargetTriangles", map.targetTriangles);
        }

        private static void SortPhotons(CausticPhotonData[] photons)
        {
            Array.Sort(photons, (left, right) =>
            {
                int result = left.position.x.CompareTo(right.position.x);
                if (result == 0) result = left.position.z.CompareTo(right.position.z);
                if (result == 0) result = left.incomingDirection.x.CompareTo(right.incomingDirection.x);
                if (result == 0) result = left.power.x.CompareTo(right.power.x);
                return result;
            });
        }

        private static void AssertVector3(Vector3 actual, Vector3 expected, string label)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0002f), $"{label} x");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0002f), $"{label} y");
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0002f), $"{label} z");
        }

        private static void AssertRelativeEnergy(Vector4 left, Vector4 right, float tolerance, string label)
        {
            float leftEnergy = Luminance(left);
            float rightEnergy = Luminance(right);
            float drift = Mathf.Abs(leftEnergy - rightEnergy) / Mathf.Max(1e-6f, (leftEnergy + rightEnergy) * 0.5f);
            Assert.That(leftEnergy, Is.GreaterThan(0.0f));
            Assert.That(rightEnergy, Is.GreaterThan(0.0f));
            Assert.That(drift, Is.LessThan(tolerance),
                $"Caustic {label} energy drift was {drift:P2}: {leftEnergy:F8} versus {rightEnergy:F8}");
        }

        private static float MaxProbeLuminance(Vector4[] signature)
        {
            float maximum = 0.0f;
            for (int i = 1; i < signature.Length; i++)
            {
                maximum = Mathf.Max(maximum, Luminance(signature[i]));
            }
            return maximum;
        }

        private static float Luminance(Vector4 color)
        {
            return color.x * 0.2126f + color.y * 0.7152f + color.z * 0.0722f;
        }

        private static void CreateGlassCube(out MeshTriangleData[] triangles, out MeshInfoData[] meshes, out BvhNodeData[] bvhNodes)
        {
            Vector3 min = new Vector3(-0.8f, 0.2f, -0.3f);
            Vector3 max = new Vector3(0.8f, 1.8f, 1.3f);
            CreateGlassCube(min, max, out triangles, out meshes, out bvhNodes);
        }

        private static void CreateSubmergedGlassCube(out MeshTriangleData[] triangles, out MeshInfoData[] meshes, out BvhNodeData[] bvhNodes)
        {
            CreateGlassCube(new Vector3(-0.65f, 0.05f, 0.65f), new Vector3(0.65f, 0.65f, 1.65f), out triangles, out meshes, out bvhNodes);
        }

        private static void CreateGlassCube(
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
            triangles[0].lightIndex = 0;
            triangles[1].lightIndex = 1;
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
                transmission = 1.0f,
                materialType = materialType,
                meshIndex = 0,
                textureIndex = textureIndex,
                metallicRoughnessTextureIndex = -1,
                normalTextureIndex = -1,
                lightIndex = materialType == 3 ? 0 : -1
            };
        }

        private static void AppendReceiver(
            ref MeshTriangleData[] triangles,
            ref MeshInfoData[] meshes,
            ref BvhNodeData[] bvhNodes,
            float smoothness)
        {
            const float extent = 20.0f;
            int triangleStart = triangles.Length;
            int meshIndex = meshes.Length;
            int rootNodeIndex = bvhNodes.Length;
            Vector3 min = new Vector3(-extent, -0.0001f, -extent);
            Vector3 max = new Vector3(extent, 0.0001f, extent);
            Vector3 p0 = new Vector3(-extent, 0.0f, -extent);
            Vector3 p1 = new Vector3(-extent, 0.0f, extent);
            Vector3 p2 = new Vector3(extent, 0.0f, extent);
            Vector3 p3 = new Vector3(extent, 0.0f, -extent);
            Vector3 color = new Vector3(0.8f, 0.8f, 0.8f);

            MeshTriangleData first = SurfaceTriangle(p0, p1, p2, Vector3.up, color, -1);
            MeshTriangleData second = SurfaceTriangle(p0, p2, p3, Vector3.up, color, -1);
            first.smoothness = smoothness;
            second.smoothness = smoothness;
            first.meshIndex = meshIndex;
            second.meshIndex = meshIndex;

            Array.Resize(ref triangles, triangleStart + 2);
            triangles[triangleStart] = first;
            triangles[triangleStart + 1] = second;

            Array.Resize(ref meshes, meshIndex + 1);
            meshes[meshIndex] = new MeshInfoData
            {
                boundsMin = min,
                rootNodeIndex = rootNodeIndex,
                boundsMax = max,
                triangleStart = triangleStart,
                triangleCount = 2,
                meshIndex = meshIndex,
                isLight = 0
            };

            Array.Resize(ref bvhNodes, rootNodeIndex + 1);
            bvhNodes[rootNodeIndex] = new BvhNodeData
            {
                boundsMin = min,
                leftChildIndex = -1,
                boundsMax = max,
                rightChildIndex = -1,
                triangleStart = triangleStart,
                triangleCount = 2
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
                transmission = 1.0f,
                materialType = 2,
                meshIndex = 0,
                textureIndex = -1,
                metallicRoughnessTextureIndex = -1,
                normalTextureIndex = -1,
                lightIndex = -1
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

        private static void SetFogDisabled(ComputeShader shader)
        {
            shader.SetInt("_FogEnabled", 0);
            shader.SetVector("_FogBoundsMin", Vector4.zero);
            shader.SetVector("_FogBoundsMax", Vector4.one);
            shader.SetFloat("_FogDensity", 0.0f);
            shader.SetVector("_FogScatteringAlbedo", Vector4.zero);
            shader.SetFloat("_FogInScatteringIntensity", 0.0f);
            shader.SetInt("_FogMultipleScattering", 0);
        }

        private static Vector4[] ReadSignature(
            RenderTexture source,
            int width,
            int height,
            float[,] probes = null,
            bool includePeak = false)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = source;
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture.Apply(false, false);
            RenderTexture.active = previous;
            Color[] pixels = texture.GetPixels();
            UnityEngine.Object.DestroyImmediate(texture);

            float[,] defaultProbes = { { 0.25f, 0.25f }, { 0.5f, 0.25f }, { 0.75f, 0.25f }, { 0.25f, 0.5f }, { 0.5f, 0.5f }, { 0.75f, 0.5f }, { 0.375f, 0.75f }, { 0.625f, 0.75f } };
            probes = probes ?? defaultProbes;
            var signature = new Vector4[probes.GetLength(0) + 1 + (includePeak ? 1 : 0)];
            Vector4 average = Vector4.zero;
            Vector4 peak = Vector4.zero;
            foreach (Color pixel in pixels)
            {
                average += (Vector4)pixel;
                if (Luminance(pixel) > Luminance(peak))
                {
                    peak = pixel;
                }
            }
            signature[0] = average / pixels.Length;

            for (int i = 0; i < probes.GetLength(0); i++)
            {
                int x = Mathf.Clamp(Mathf.FloorToInt(probes[i, 0] * width), 0, width - 1);
                int y = Mathf.Clamp(Mathf.FloorToInt(probes[i, 1] * height), 0, height - 1);
                signature[i + 1] = pixels[y * width + x];
            }
            if (includePeak)
            {
                signature[signature.Length - 1] = peak;
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

        private static Texture2DArray CreateParallaxTextureArray()
        {
            var texture = new Texture2DArray(1, 1, 1, TextureFormat.RGBA32, false, true);
            texture.SetPixels(new[] { Color.black }, 0);
            texture.Apply(false, false);
            return texture;
        }

        private static Texture2DArray CreateNormalTextureArray()
        {
            var texture = new Texture2DArray(1, 1, 1, TextureFormat.RGBA32, false, true);
            texture.SetPixels(new[] { new Color(0.5f, 0.5f, 1.0f, 1.0f) }, 0);
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
