using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GPURayTracing.Tests
{
    public class RayTracingComputeRegressionTests
    {
        private const string ComputeShaderPath = "Assets/Scripts/RayTracingCompute.compute";
        private const string DenoiserShaderPath = "Assets/Resources/RayTracingSpatialDenoiser.compute";
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
            var buffer = new ComputeBuffer(39, sizeof(float) * 4);
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

                var results = new Vector4[39];
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
                AssertVector(results[32], new Vector4(2.0f, 0.1111111f, 0.1111111f, 0.1111111f), "triangle-light PDF and water F0");
                AssertVector(results[33], new Vector4(1.6999575f, 0.8499787f, 0.4249894f, 1.0f), "firefly luminance clamp", 0.0002f);
                AssertVector(results[34], new Vector4(0.0f, 0.4472136f, 0.8944272f, 1.0f), "caustic optical normal", 0.0002f);
                AssertVector(results[35], new Vector4(0.0f, -0.1602089f, -0.9870830f, 0.0400126f), "interpolated mesh refraction and Fresnel", 0.0002f);
                AssertVector(results[36], new Vector4(0.0f, 1.0f, 0.0f, 1.0f), "fully smooth glass preserves the optical normal");
                Assert.That(results[37].w, Is.GreaterThan(0.01f), "rough glass should perturb its transmitted direction");
                Assert.That(new Vector3(results[37].x, results[37].y, results[37].z).sqrMagnitude,
                    Is.EqualTo(1.0f).Within(0.001f), "rough glass microfacet normal should remain normalized");
                AssertVector(results[38], new Vector4(1.0f, 0.25f, 0.5f, 1.0f), "small triangle intersection", 0.0002f);
            }
            finally
            {
                sphereBuffer.Release();
                buffer.Release();
            }
        }

        [Test]
        public void DenoiserShader_TemporalKernelsCompile()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Compute shaders are not supported by the active graphics device.");
            }

            ComputeShader denoiser = AssetDatabase.LoadAssetAtPath<ComputeShader>(DenoiserShaderPath);
            Assert.That(denoiser, Is.Not.Null, $"Missing compute shader at {DenoiserShaderPath}");
            // Batch mode uses Unity's Null graphics device, which cannot compile GPU kernels.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Assert.Ignore("Temporal compute kernels require a graphics device to compile.");
            }
            Assert.That(denoiser.HasKernel("CSGenerateCameraMotion"), Is.True);
            Assert.That(denoiser.HasKernel("CSTemporalReprojectValidate"), Is.True);
            Assert.That(denoiser.HasKernel("CSUpdateTemporalMoments"), Is.True);
            Assert.That(denoiser.HasKernel("CSVisualizeTemporal"), Is.True);
            Assert.That(denoiser.HasKernel("CSVisualizeFeature"), Is.True);

            ComputeShader renderer = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeShaderPath);
            Assert.That(renderer.HasKernel("CSFeatures"), Is.True);
        }

        [Test]
        public void GameManager_DefaultFireflyClamp_IsEnabled()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            var gameObject = new GameObject("Firefly Clamp Default Test");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                FieldInfo clampField = managerType.GetField("fireflyClamp");

                Assert.That(clampField, Is.Not.Null);
                Assert.That(clampField.GetValue(manager), Is.EqualTo(1.0f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void GameManager_DefaultCameraLens_PreservesPreviousBlurScale()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            var gameObject = new GameObject("Camera Lens Default Test");
            try
            {
                Component manager = gameObject.AddComponent(managerType);
                FieldInfo modeField = managerType.GetField("cameraApertureMode");
                FieldInfo radiusField = managerType.GetField("cameraApertureRadius");
                FieldInfo clickField = managerType.GetField("enableClickToFocus");
                FieldInfo trackClickField = managerType.GetField("trackClickedFocusPoint");

                Assert.That(modeField, Is.Not.Null);
                Assert.That(modeField.GetValue(manager).ToString(), Is.EqualTo("LensRadius"));
                Assert.That(radiusField, Is.Not.Null);
                Assert.That(radiusField.GetValue(manager), Is.EqualTo(0.005f));
                Assert.That(clickField, Is.Not.Null);
                Assert.That(clickField.GetValue(manager), Is.EqualTo(true));
                Assert.That(trackClickField, Is.Not.Null);
                Assert.That(trackClickField.GetValue(manager), Is.EqualTo(true));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void GameManager_CameraRotation_StopsBeforeVerticalPolesAndCanRotateBack()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            MethodInfo rotateMethod = managerType.GetMethod("RotateCamera", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(rotateMethod, Is.Not.Null);

            var cameraObject = new GameObject("Camera Pitch Limit Test");
            try
            {
                cameraObject.transform.eulerAngles = new Vector3(88.0f, 10.0f, 0.0f);
                rotateMethod.Invoke(null, new object[] { cameraObject.transform, 5.0f, 5.0f });
                Assert.That(Mathf.DeltaAngle(0.0f, cameraObject.transform.eulerAngles.x), Is.EqualTo(89.0f).Within(Epsilon));
                Assert.That(Mathf.DeltaAngle(0.0f, cameraObject.transform.eulerAngles.y), Is.EqualTo(15.0f).Within(Epsilon));

                rotateMethod.Invoke(null, new object[] { cameraObject.transform, 0.0f, -10.0f });
                Assert.That(Mathf.DeltaAngle(0.0f, cameraObject.transform.eulerAngles.x), Is.EqualTo(79.0f).Within(Epsilon));

                cameraObject.transform.eulerAngles = new Vector3(-88.0f, 10.0f, 0.0f);
                rotateMethod.Invoke(null, new object[] { cameraObject.transform, 0.0f, -5.0f });
                Assert.That(Mathf.DeltaAngle(0.0f, cameraObject.transform.eulerAngles.x), Is.EqualTo(-89.0f).Within(Epsilon));

                rotateMethod.Invoke(null, new object[] { cameraObject.transform, 0.0f, 10.0f });
                Assert.That(Mathf.DeltaAngle(0.0f, cameraObject.transform.eulerAngles.x), Is.EqualTo(-79.0f).Within(Epsilon));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void GameManager_TrackedFocusPoint_UpdatesDistanceAndUsesPinholeOutsideFrustum()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");

            var managerObject = new GameObject("Tracked Focus Point Test");
            var cameraObject = new GameObject("Tracked Focus Camera");
            try
            {
                Component manager = managerObject.AddComponent(managerType);
                Camera camera = cameraObject.AddComponent<Camera>();
                managerType.GetField("renderTextureCamera").SetValue(manager, camera);
                managerType.GetField("trackClickedFocusPoint").SetValue(manager, true);
                managerType.GetField("cameraApertureRadius").SetValue(manager, 0.02f);

                FieldInfo focusPointField = managerType.GetField("_clickedFocusPoint", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo hasFocusPointField = managerType.GetField("_hasClickedFocusPoint", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo updateFocusMethod = managerType.GetMethod("UpdateTrackedFocusPoint", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo apertureMethod = managerType.GetMethod("GetCameraApertureRadius", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(focusPointField, Is.Not.Null);
                Assert.That(hasFocusPointField, Is.Not.Null);
                Assert.That(updateFocusMethod, Is.Not.Null);
                Assert.That(apertureMethod, Is.Not.Null);

                focusPointField.SetValue(manager, new Vector3(0.0f, 0.0f, 10.0f));
                hasFocusPointField.SetValue(manager, true);
                updateFocusMethod.Invoke(manager, null);

                Assert.That(managerType.GetField("cameraFocalDistance").GetValue(manager), Is.EqualTo(10.0f));
                Assert.That(apertureMethod.Invoke(manager, null), Is.EqualTo(0.02f));

                camera.transform.rotation = Quaternion.Euler(0.0f, 180.0f, 0.0f);
                updateFocusMethod.Invoke(manager, null);

                Assert.That(apertureMethod.Invoke(manager, null), Is.EqualTo(0.0f));
                Assert.That(managerType.GetField("cameraApertureMode").GetValue(manager).ToString(), Is.EqualTo("LensRadius"));

                managerType.GetField("enableClickToFocus").SetValue(manager, false);
                Assert.That(apertureMethod.Invoke(manager, null), Is.EqualTo(0.02f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void FocusQuery_SelectsFirstIntersectionRegardlessOfOpacity()
        {
            string shaderSource = System.IO.File.ReadAllText(ComputeShaderPath);
            int kernelStart = shaderSource.IndexOf("void CSFocusQuery", StringComparison.Ordinal);
            int mainKernelStart = shaderSource.IndexOf("void CSMain", kernelStart, StringComparison.Ordinal);

            Assert.That(kernelStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(mainKernelStart, Is.GreaterThan(kernelStart));
            string focusKernel = shaderSource.Substring(kernelStart, mainKernelStart - kernelStart);
            Assert.That(focusKernel, Does.Contain("GetNearestIntersection(ray)"));
            Assert.That(focusKernel, Does.Not.Contain("hit.opacity"));
            Assert.That(focusKernel, Does.Not.Contain("TransparentOpacityThreshold"));
        }

        [Test]
        public void GameManager_SameSizeMeshTextureArray_PreservesSourceTexels()
        {
            Type managerType = Type.GetType("GameManager, Assembly-CSharp");
            Assert.That(managerType, Is.Not.Null, "Could not load GameManager from Assembly-CSharp");
            MethodInfo buildMethod = managerType.GetMethod(
                "BuildMeshTextureArray",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(buildMethod, Is.Not.Null);

            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            var expected = new[]
            {
                new Color32(127, 127, 255, 255),
                new Color32(128, 126, 254, 255),
                new Color32(12, 240, 64, 255),
                new Color32(251, 3, 129, 255)
            };
            source.SetPixels32(expected);
            source.Apply(false, false);

            Texture2DArray result = null;
            try
            {
                result = (Texture2DArray)buildMethod.Invoke(null, new object[]
                {
                    new List<Texture2D> { source },
                    null,
                    "Exact Texture Copy Test",
                    Color.white,
                    true
                });

                Assert.That(result.width, Is.EqualTo(source.width));
                Assert.That(result.height, Is.EqualTo(source.height));
                Assert.That(result.GetPixels32(0), Is.EqualTo(expected));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(result);
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

        [Test]
        public void BenchmarkCaustics_ProductionSamplingDistribution_HasValidTargets()
        {
            Scene previousScene = SceneManager.GetActiveScene();
            string previousScenePath = previousScene.path;
            try
            {
                Scene scene = EditorSceneManager.OpenScene(
                    "Assets/Scenes/Benchmarks/Benchmark_Caustics.unity",
                    OpenSceneMode.Single);
                Assert.That(scene.IsValid(), Is.True);

                Type managerType = Type.GetType("GameManager, Assembly-CSharp");
                Component manager = UnityEngine.Object.FindFirstObjectByType(managerType) as Component;
                Assert.That(manager, Is.Not.Null);

                Type rayTracingObjectType = Type.GetType("RayTracingObject, Assembly-CSharp");
                MethodInfo registerMethod = managerType.GetMethod(
                    "RegisterObject", BindingFlags.Instance | BindingFlags.Public);
                foreach (UnityEngine.Object rayTracingObject in UnityEngine.Object.FindObjectsByType(
                    rayTracingObjectType, FindObjectsSortMode.None))
                {
                    registerMethod.Invoke(manager, new[] { rayTracingObject });
                }
                managerType.GetMethod("RebuildBuffers", BindingFlags.Instance | BindingFlags.Public)
                    .Invoke(manager, new object[] { false });
                managerType.GetMethod("UpdateSpheres", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(manager, null);
                managerType.GetMethod("UpdateTriangles", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(manager, null);
                managerType.GetMethod("BuildCausticSamplingDistribution", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(manager, null);

                PropertyInfo pairCountProperty = managerType.GetProperty("CausticTargetPairCount");
                Assert.That(pairCountProperty.GetValue(manager), Is.GreaterThan(0),
                    "The production scene should produce at least one eligible light/refractor pair");

                FieldInfo pairsField = managerType.GetField(
                    "_causticTargetPairs", BindingFlags.Instance | BindingFlags.NonPublic);
                var pairs = pairsField.GetValue(manager) as System.Collections.IList;
                float probabilitySum = 0.0f;
                foreach (object pair in pairs)
                {
                    Type pairType = pair.GetType();
                    float probability = (float)pairType.GetField("selectionProbability").GetValue(pair);
                    float cumulative = (float)pairType.GetField("cumulativeProbability").GetValue(pair);
                    Assert.That(float.IsNaN(probability), Is.False);
                    Assert.That(probability, Is.GreaterThan(0.0f));
                    Assert.That(cumulative, Is.InRange(0.0f, 1.0f));
                    probabilitySum += probability;
                }
                Assert.That(probabilitySum, Is.EqualTo(1.0f).Within(0.0001f));

                // The glass-mesh target CDF is a separate distribution from the pair CDF above, and
                // it is the one photon power divides by. A normalization error here silently scales
                // every mesh photon's power (potentially negative), producing no visible caustics
                // even while pair probabilities still look valid. Each mesh owns its own CDF range,
                // so validate per range rather than across the concatenated list.
                FieldInfo trianglesField = managerType.GetField(
                    "_causticTargetTriangles", BindingFlags.Instance | BindingFlags.NonPublic);
                var targetTriangles = trianglesField.GetValue(manager) as System.Collections.IList;
                Assert.That(targetTriangles.Count, Is.GreaterThan(0),
                    "The production scene's glass mesh should produce area-weighted target triangles");

                int validatedMeshRanges = 0;
                foreach (object pair in pairs)
                {
                    Type pairType = pair.GetType();
                    if ((int)pairType.GetField("refractorType").GetValue(pair) != 1)
                    {
                        continue;
                    }

                    int rangeStart = (int)pairType.GetField("triangleStart").GetValue(pair);
                    int rangeCount = (int)pairType.GetField("triangleCount").GetValue(pair);
                    Assert.That(rangeCount, Is.GreaterThan(0), "a glass-mesh pair must target triangles");
                    Assert.That(rangeStart + rangeCount, Is.LessThanOrEqualTo(targetTriangles.Count));

                    float triangleProbabilitySum = 0.0f;
                    float previousCumulative = 0.0f;
                    for (int i = rangeStart; i < rangeStart + rangeCount; i++)
                    {
                        object target = targetTriangles[i];
                        Type targetType = target.GetType();
                        float probability = (float)targetType.GetField("selectionProbability").GetValue(target);
                        float cumulative = (float)targetType.GetField("cumulativeProbability").GetValue(target);
                        Assert.That(float.IsNaN(probability), Is.False, $"target triangle {i} probability is NaN");
                        Assert.That(probability, Is.GreaterThan(0.0f),
                            $"target triangle {i} must have a positive selection probability");
                        Assert.That(cumulative, Is.InRange(0.0f, 1.0f),
                            $"target triangle {i} cumulative probability must be a normalized CDF value");
                        Assert.That(cumulative, Is.GreaterThanOrEqualTo(previousCumulative),
                            $"target triangle {i} must keep the CDF monotonically increasing");
                        previousCumulative = cumulative;
                        triangleProbabilitySum += probability;
                    }

                    Assert.That(triangleProbabilitySum, Is.EqualTo(1.0f).Within(0.001f),
                        "Glass-mesh target triangle probabilities must sum to one");
                    Assert.That(previousCumulative, Is.EqualTo(1.0f).Within(0.0001f),
                        "The final glass-mesh target CDF entry must reach one so no sample falls through");
                    validatedMeshRanges++;
                }
                Assert.That(validatedMeshRanges, Is.GreaterThan(0),
                    "The production scene should exercise at least one glass-mesh target distribution");

                managerType.GetMethod("UpdateCausticPhotonMap", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(manager, null);
                PropertyInfo photonCountProperty = managerType.GetProperty("CausticGridPhotonCount");
                int indexedPhotonCount = (int)photonCountProperty.GetValue(manager);
                TestContext.WriteLine($"Production indexed photon count: {indexedPhotonCount}");
                Assert.That(indexedPhotonCount, Is.GreaterThan(0),
                    "The production sampling distribution should produce indexed receiver photons");

                ComputeShader shader = managerType.GetField("shader").GetValue(manager) as ComputeShader;
                int gatherKernel = shader.FindKernel("CSCausticsDebug");
                var causticsImage = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGBFloat)
                {
                    enableRandomWrite = true
                };
                causticsImage.Create();
                try
                {
                    managerType.GetMethod("SetShaderParameters", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(manager, new object[] { gatherKernel });
                    shader.SetTexture(gatherKernel, "Result", causticsImage);
                    shader.SetInt("_NumberOfPasses", 1);
                    shader.Dispatch(gatherKernel, 32, 64, 1);

                    RenderTexture previous = RenderTexture.active;
                    RenderTexture.active = causticsImage;
                    var image = new Texture2D(256, 256, TextureFormat.RGBAFloat, false, true);
                    image.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
                    image.Apply();
                    RenderTexture.active = previous;
                    float maximumLuminance = 0.0f;
                    foreach (Color pixel in image.GetPixels())
                    {
                        maximumLuminance = Mathf.Max(maximumLuminance,
                            pixel.r * 0.2126f + pixel.g * 0.7152f + pixel.b * 0.0722f);
                    }
                    UnityEngine.Object.DestroyImmediate(image);
                    TestContext.WriteLine($"Production visible caustic peak: {maximumLuminance}");
                    Assert.That(maximumLuminance, Is.GreaterThan(0.0f),
                        "The saved benchmark camera should see gathered caustic radiance");
                }
                finally
                {
                    causticsImage.Release();
                }

            }
            finally
            {
                if (!string.IsNullOrEmpty(previousScenePath))
                {
                    EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
            }
        }

        [Test]
        public void PhotonTrace_BindsAllMeshTextureArrays()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            int methodStart = managerSource.IndexOf("private void SetPhotonTraceSceneParameters(int traceKernel)", StringComparison.Ordinal);
            int nextMethodStart = managerSource.IndexOf("private bool ShouldUseFrameAccumulation()", methodStart, StringComparison.Ordinal);

            Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(nextMethodStart, Is.GreaterThan(methodStart));
            string method = managerSource.Substring(methodStart, nextMethodStart - methodStart);
            Assert.That(method, Does.Contain("EnsureMeshTextureArrays()"));
            Assert.That(method, Does.Contain("\"_MeshAlbedoTextures\""));
            Assert.That(method, Does.Contain("\"_MeshMetallicRoughnessTextures\""));
            Assert.That(method, Does.Contain("\"_MeshNormalTextures\""));
        }

        [Test]
        public void CausticsDebugMode_UsesDedicatedGatherKernelWithoutDebugVariant()
        {
            string managerSource = System.IO.File.ReadAllText("Assets/Scripts/GameManager.cs");
            Assert.That(managerSource, Does.Contain(
                "enableCaustics && debugRenderMode == DebugRenderMode.Caustics"));
            Assert.That(managerSource, Does.Contain(
                "useDedicatedCausticsDebugKernel ? \"CSCausticsDebug\" : \"CSMain\""));
            Assert.That(managerSource, Does.Contain(
                "debugRenderMode == DebugRenderMode.FinalColor || debugRenderMode == DebugRenderMode.Caustics"));
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
