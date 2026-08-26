using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Forces the renderer compute assets through their first tiny dispatch in edit mode. Unity compiles
// compute kernels lazily, so timing the dispatch is the useful signal rather than asset loading.
public static class RayTracingShaderPrecompiler
{
    private const string MainShaderPath = "Assets/Scripts/RayTracingCompute.compute";
    private const string WaterShaderPath = "Assets/Resources/RayTracingWater.compute";
    private const string DebugShaderPath = "Assets/Resources/RayTracingDebug.compute";
    private const string AdaptiveTraceShaderPath = "Assets/Resources/RayTracingAdaptiveTrace.compute";
    private const string AdaptiveSchedulerShaderPath = "Assets/Resources/RayTracingAdaptiveScheduler.compute";
    private const string UtilityShaderPath = "Assets/Resources/RayTracingUtility.compute";
    private const string FeaturesShaderPath = "Assets/Resources/RayTracingFeatures.compute";
    private const string FocusShaderPath = "Assets/Resources/RayTracingFocus.compute";
    private const string RegressionProbeShaderPath = "Assets/Resources/RayTracingRegressionProbe.compute";
    private const string CausticsShaderPath = "Assets/Resources/RayTracingCaustics.compute";
    private const string ProgressTitle = "Precompiling ray tracing shaders";
    private const string StatsPath = "Library/RayTracingShaderCompileStats.csv";
    private const string StatsHeader = "timestamp,unityVersion,buildTarget,graphicsDevice,shaderAsset,kernel,shaderHash,variant,coldDispatchMs,warmDispatchMs";

    private enum VariantSet { None, FogTerrain, Terrain }

    private sealed class ShaderAsset
    {
        public readonly string Label;
        public readonly string Path;
        public readonly string[] Kernels;
        public readonly VariantSet Variants;

        public ShaderAsset(string label, string path, VariantSet variants, params string[] kernels)
        {
            Label = label;
            Path = path;
            Kernels = kernels;
            Variants = variants;
        }
    }

    private readonly struct Variant
    {
        public readonly bool Fog;
        public readonly bool Terrain;

        public Variant(bool fog, bool terrain)
        {
            Fog = fog;
            Terrain = terrain;
        }

        public string Label => $"fog={(Fog ? 1 : 0)};terrain={(Terrain ? 1 : 0)}";
    }

    private static readonly ShaderAsset Main = new ShaderAsset("Main Final Color", MainShaderPath, VariantSet.FogTerrain, "CSMain");
    private static readonly ShaderAsset Water = new ShaderAsset("Water Final Color", WaterShaderPath, VariantSet.FogTerrain, "CSMain");
    private static readonly ShaderAsset DebugShader = new ShaderAsset("Debug", DebugShaderPath, VariantSet.FogTerrain, "CSDebugMain");
    private static readonly ShaderAsset AdaptiveTrace = new ShaderAsset("Adaptive Trace", AdaptiveTraceShaderPath, VariantSet.FogTerrain,
        "CSAdaptiveTrace", "CSAdaptiveTraceReference");
    private static readonly ShaderAsset AdaptiveScheduler = new ShaderAsset("Adaptive Scheduler", AdaptiveSchedulerShaderPath, VariantSet.None,
         "ClearAdaptiveSamplingState", "ClearAdaptiveGroupState", "ClearAdaptiveScheduler", "ClearAdaptiveWorkList",
         "ClearAdaptiveFrameMetadata", "RecordAdaptiveRetiredPaths", "CSAdaptiveClassifyGroups", "CSAdaptiveApplyBucketRemap",
        "CSAdaptiveCompactGroupWorkList", "CSBuildAdaptiveDispatchArgs", "CSAdaptiveDiagnostics");
    private static readonly ShaderAsset Utility = new ShaderAsset("Utility", UtilityShaderPath, VariantSet.None,
        "ClearAccumulation", "UpscaleAdaptiveBootstrap", "SeedAdaptiveBootstrap", "ComposeAdaptiveBootstrap");
    private static readonly ShaderAsset Features = new ShaderAsset("Features", FeaturesShaderPath, VariantSet.FogTerrain, "CSFeatures");
    private static readonly ShaderAsset Focus = new ShaderAsset("Focus", FocusShaderPath, VariantSet.Terrain, "CSFocusQuery");
    private static readonly ShaderAsset RegressionProbe = new ShaderAsset("Regression Probe", RegressionProbeShaderPath, VariantSet.None, "CSRegressionProbe");
    private static readonly ShaderAsset[] RendererAssets = { Main, Water, DebugShader, AdaptiveTrace, AdaptiveScheduler, Utility, Features, Focus, RegressionProbe };

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Main Final Color/All Fog + Terrain Variants")]
    private static void PrecompileMainAllVariants() => Precompile(new[] { Main }, true);

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Main Final Color/Default (Fog Off, Terrain Off)")]
    private static void PrecompileMainDefault() => Precompile(new[] { Main }, true, 0);

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Main Final Color/Fog (Terrain Off)")]
    private static void PrecompileMainFog() => Precompile(new[] { Main }, true, 1);

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Main Final Color/Terrain (Fog Off)")]
    private static void PrecompileMainTerrain() => Precompile(new[] { Main }, true, 2);

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Main Final Color/Fog + Terrain")]
    private static void PrecompileMainFogTerrain() => Precompile(new[] { Main }, true, 3);

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Water Final Color/Default (Fog Off, Terrain Off)")]
    private static void PrecompileWaterDefault() => Precompile(new[] { Water }, true, 0);

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Debug/All Fog + Terrain Variants")]
    private static void PrecompileDebugAllVariants() => Precompile(new[] { DebugShader }, true);

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Adaptive Trace/All Kernels and Fog + Terrain Variants")]
    private static void PrecompileAdaptiveTrace() => Precompile(new[] { AdaptiveTrace }, true);

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Adaptive Scheduler/All Runtime Kernels")]
    private static void PrecompileAdaptiveScheduler() => Precompile(new[] { AdaptiveScheduler }, true);

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Utility/All Runtime Kernels")]
    private static void PrecompileUtility() => Precompile(new[] { Utility }, true);

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Features/All Fog + Terrain Variants")]
    private static void PrecompileFeatures() => Precompile(new[] { Features }, true);

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Focus/All Terrain Variants")]
    private static void PrecompileFocus() => Precompile(new[] { Focus }, true);

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Regression Probe")]
    private static void PrecompileRegressionProbe() => Precompile(new[] { RegressionProbe }, true);

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/All Renderer Assets")]
    private static void PrecompileAllRendererAssets() => Precompile(RendererAssets, true);

    // Run in a separate Unity process with -executeMethod RayTracingShaderPrecompiler.PrecompileFromCommandLine.
    // -rayTracingPrecompileAllVariants selects every split renderer asset; otherwise only the normal main path is warmed.
    public static void PrecompileFromCommandLine()
    {
        string requestedAsset = GetCommandLineArgumentValue("-rayTracingPrecompileAsset");
        string requestedVariant = GetCommandLineArgumentValue("-rayTracingPrecompileVariant");
        bool allAssets = HasCommandLineArgument("-rayTracingPrecompileAllVariants");
        ShaderAsset[] assets = string.IsNullOrEmpty(requestedAsset)
            ? allAssets ? RendererAssets : new[] { Main }
            : GetRequestedAssets(requestedAsset);
        int selectedVariant = GetRequestedVariant(requestedVariant, assets);
        if (!string.IsNullOrEmpty(requestedVariant) && selectedVariant < 0)
        {
            EditorApplication.Exit(1);
            return;
        }
        bool succeeded = Precompile(assets, HasCommandLineArgument("-rayTracingColdShaderPrecompile"),
            selectedVariant >= 0 ? selectedVariant : assets.Length == 1 && assets[0] == Main && !allAssets ? 0 : -1);
        EditorApplication.Exit(succeeded ? 0 : 1);
    }

    private static ShaderAsset[] GetRequestedAssets(string value)
    {
        foreach (var asset in RendererAssets)
        {
            if (string.Equals(asset.Label, value, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(asset.Path), value, System.StringComparison.OrdinalIgnoreCase))
            {
                return new[] { asset };
            }
        }

        Debug.LogError($"Unknown -rayTracingPrecompileAsset '{value}'. Use one of: " +
            string.Join(", ", System.Array.ConvertAll(RendererAssets, asset => Path.GetFileNameWithoutExtension(asset.Path))));
        return System.Array.Empty<ShaderAsset>();
    }

    private static int GetRequestedVariant(string value, ShaderAsset[] assets)
    {
        if (string.IsNullOrEmpty(value)) return -1;
        if (assets.Length != 1)
        {
            Debug.LogError("-rayTracingPrecompileVariant requires exactly one selected shader asset.");
            return -1;
        }

        var variants = CreateVariants(assets[0].Variants);
        for (var index = 0; index < variants.Length; index++)
        {
            if (string.Equals(variants[index].Label, value, System.StringComparison.OrdinalIgnoreCase)) return index;
        }

        Debug.LogError($"Unknown -rayTracingPrecompileVariant '{value}'. Use one of: " +
            string.Join(", ", System.Array.ConvertAll(variants, variant => variant.Label)));
        return -1;
    }

    private static bool Precompile(ShaderAsset[] assets, bool coldCompile, int selectedMainVariant = -1)
    {
        if (assets.Length == 0)
        {
            return false;
        }

        if (coldCompile)
        {
            // Clear once for a selected set, then force each selected asset to rebuild before timing.
            ClearShaderCache();
            foreach (var asset in assets)
            {
                AssetDatabase.ImportAsset(asset.Path, ImportAssetOptions.ForceUpdate);
            }
        }

        var resources = new DummyResources();
        int completed = 0;
        int total = CountDispatches(assets, selectedMainVariant);
        long coldTotal = 0;
        long warmTotal = 0;
        var totalStopwatch = Stopwatch.StartNew();
        try
        {
            foreach (var asset in assets)
            {
                var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(asset.Path);
                if (shader == null)
                {
                    Debug.LogError($"Precompile failed: could not load '{asset.Path}'.");
                    return false;
                }

                if (!LogCompileMessages(asset.Label, shader)) return false;
                var variants = CreateVariants(asset.Variants);
                for (var kernelIndex = 0; kernelIndex < asset.Kernels.Length; kernelIndex++)
                {
                    int kernel = shader.FindKernel(asset.Kernels[kernelIndex]);
                    BindResources(shader, kernel, resources);
                    for (var variantIndex = 0; variantIndex < variants.Length; variantIndex++)
                    {
                        if (selectedMainVariant >= 0 && variantIndex != selectedMainVariant) continue;
                        var variant = variants[variantIndex];
                        if (EditorUtility.DisplayCancelableProgressBar(ProgressTitle,
                                $"{asset.Label} / {asset.Kernels[kernelIndex]} / {variant.Label} ({completed + 1} of {total})",
                                completed / (float)total))
                        {
                            Debug.LogWarning($"Ray tracing precompile cancelled after {completed} of {total} dispatches.");
                            return false;
                        }

                        SetKeyword(shader, "FOG_ENABLED", variant.Fog);
                        SetKeyword(shader, "TERRAIN_ENABLED", variant.Terrain);
                        var cold = Stopwatch.StartNew();
                        PathTracing.ComputeDispatch.Dispatch(shader, kernel, 1, 1, 1);
                        cold.Stop();
                        var warm = Stopwatch.StartNew();
                        PathTracing.ComputeDispatch.Dispatch(shader, kernel, 1, 1, 1);
                        warm.Stop();
                        coldTotal += cold.ElapsedMilliseconds;
                        warmTotal += warm.ElapsedMilliseconds;
                        AppendStatsRow(asset.Path, asset.Kernels[kernelIndex], variant.Label, cold.ElapsedMilliseconds, warm.ElapsedMilliseconds);
                        Debug.Log($"{asset.Label} {asset.Kernels[kernelIndex]} ({variant.Label}) first={cold.ElapsedMilliseconds} ms, warm={warm.ElapsedMilliseconds} ms.");
                        completed++;
                    }
                }
            }

            resources.WaitForGpu();
        }
        catch (System.Exception exception)
        {
            Debug.LogError($"Precompile dispatch threw: {exception.Message}\n{exception}");
            return false;
        }
        finally
        {
            totalStopwatch.Stop();
            EditorUtility.ClearProgressBar();
            resources.Dispose();
        }

        var statsPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, StatsPath);
        Debug.Log($"Ray tracing precompile completed {completed} dispatches in {totalStopwatch.ElapsedMilliseconds} ms. First dispatch total={coldTotal} ms, warm dispatch total={warmTotal} ms. Stats appended to '{statsPath}'.");
        return true;
    }

    private static int CountDispatches(ShaderAsset[] assets, int selectedMainVariant)
    {
        int count = 0;
        foreach (var asset in assets)
        {
            int variants = selectedMainVariant >= 0 ? 1 : CreateVariants(asset.Variants).Length;
            count += asset.Kernels.Length * variants;
        }
        return count;
    }

    private static Variant[] CreateVariants(VariantSet set)
    {
        if (set == VariantSet.None) return new[] { new Variant(false, false) };
        if (set == VariantSet.Terrain) return new[] { new Variant(false, false), new Variant(false, true) };
        return new[] { new Variant(false, false), new Variant(true, false), new Variant(false, true), new Variant(true, true) };
    }

    private static bool LogCompileMessages(string label, ComputeShader shader)
    {
        bool hasError = false;
        foreach (var message in ShaderUtil.GetComputeShaderMessages(shader))
        {
            string formatted = $"[{message.platform}] {message.message}\n{message.messageDetails}";
            if (message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
            {
                hasError = true;
                Debug.LogError($"{label} compute shader error: {formatted}");
            }
            else Debug.LogWarning($"{label} compute shader warning: {formatted}");
        }
        return !hasError;
    }

    private static void BindResources(ComputeShader shader, int kernel, DummyResources r)
    {
        shader.SetInt("_CausticsEnabled", 0); shader.SetInt("_EnvironmentLightEnabled", 0); shader.SetInt("_NumLights", 0);
        shader.SetInt("_NumberOfPasses", 1); shader.SetInt("_NumBounces", 1); shader.SetInt("_AccumulatedFrameCount", 1);
        shader.SetInt("_SampleOffset", 0); shader.SetInt("_AdaptiveBucketCount", 16); shader.SetInt("_AdaptiveGroupWidth", 1);
        shader.SetInt("_AdaptiveGroupHeight", 1); shader.SetInt("_AdaptiveGroupCount", 1); shader.SetInt("_AdaptiveWorkListCapacity", 1);
            shader.SetInt("_AdaptiveSamplingMinSamples", 1); shader.SetInt("_AdaptiveMaxPathsPerPixel", 1);
        shader.SetVector("_FogBoundsMin", Vector3.zero); shader.SetVector("_FogBoundsMax", Vector3.one); shader.SetVector("_TerrainSize", Vector3.one);
        shader.SetTexture(kernel, "Result", r.Color); shader.SetTexture(kernel, "AccumulationResult", r.Color); shader.SetTexture(kernel, "Beauty", r.Color);
        shader.SetTexture(kernel, "FeatureNormal", r.Color); shader.SetTexture(kernel, "FeatureAlbedo", r.Color); shader.SetTexture(kernel, "FeatureDepth", r.Scalar);
        shader.SetTexture(kernel, "FeatureIdentity", r.Scalar); shader.SetTexture(kernel, "FeatureValidity", r.Scalar); shader.SetTexture(kernel, "_SkyboxTexture", Texture2D.blackTexture);
        shader.SetTexture(kernel, "_MeshAlbedoTextures", r.TextureArray); shader.SetTexture(kernel, "_MeshMetallicRoughnessTextures", r.TextureArray);
        shader.SetTexture(kernel, "_MeshNormalTextures", r.TextureArray); shader.SetTexture(kernel, "_MeshParallaxTextures", r.TextureArray);
        shader.SetTexture(kernel, "_TerrainAlphamap", Texture2D.blackTexture); shader.SetTexture(kernel, "_TerrainLayer0", Texture2D.whiteTexture);
        shader.SetTexture(kernel, "_TerrainLayer1", Texture2D.whiteTexture); shader.SetTexture(kernel, "_TerrainLayer2", Texture2D.whiteTexture); shader.SetTexture(kernel, "_TerrainLayer3", Texture2D.whiteTexture);
        shader.SetTexture(kernel, "_TerrainNormal0", Texture2D.normalTexture); shader.SetTexture(kernel, "_TerrainNormal1", Texture2D.normalTexture); shader.SetTexture(kernel, "_TerrainNormal2", Texture2D.normalTexture); shader.SetTexture(kernel, "_TerrainNormal3", Texture2D.normalTexture);
        shader.SetTexture(kernel, "_TerrainMask0", Texture2D.whiteTexture); shader.SetTexture(kernel, "_TerrainMask1", Texture2D.whiteTexture); shader.SetTexture(kernel, "_TerrainMask2", Texture2D.whiteTexture); shader.SetTexture(kernel, "_TerrainMask3", Texture2D.whiteTexture);
        shader.SetTexture(kernel, "AdaptiveSamplingState", r.Color); shader.SetTexture(kernel, "AdaptiveSamplingM2", r.Color); shader.SetTexture(kernel, "AdaptiveBootstrapPriority", r.Color);
        foreach (var name in r.FloatBufferNames) shader.SetBuffer(kernel, name, r.FloatBuffer);
        foreach (var name in r.StructuredBufferNames) shader.SetBuffer(kernel, name, r.GetStructuredBuffer(name));
        foreach (var name in r.AdaptiveBufferNames) shader.SetBuffer(kernel, name, r.GetAdaptiveBuffer(name));
    }

    private sealed class DummyResources : System.IDisposable
    {
        public readonly RenderTexture Color = CreateTexture(RenderTextureFormat.ARGBFloat);
        public readonly RenderTexture Scalar = CreateTexture(RenderTextureFormat.RFloat);
        public readonly Texture2DArray TextureArray;
        public readonly ComputeBuffer FloatBuffer = new ComputeBuffer(1, 4);
        private readonly ComputeBuffer sphereBuffer = new ComputeBuffer(1, 92);
        private readonly ComputeBuffer lightBuffer = new ComputeBuffer(1, 88);
        private readonly ComputeBuffer triangleBuffer = new ComputeBuffer(1, 260);
        private readonly ComputeBuffer meshAndBvhBuffer = new ComputeBuffer(1, 48);
        private readonly ComputeBuffer causticPhotonBuffer = new ComputeBuffer(1, 36);
        private readonly ComputeBuffer terrainCellBuffer = new ComputeBuffer(1, 8);
        private readonly ComputeBuffer float4Buffer = new ComputeBuffer(64, 16);
        private readonly ComputeBuffer uint2Buffer = new ComputeBuffer(64, 8);
        private readonly ComputeBuffer uintBuffer = new ComputeBuffer(64, 4);
        public readonly string[] FloatBufferNames = { "_EnvironmentConditionalCdf", "_EnvironmentMarginalCdf", "_MeshLightTriangleCdf", "_CausticPhotonMetadata", "_CausticGridCellHeads", "_CausticPhotonNext", "_TerrainHeights" };
        public readonly string[] StructuredBufferNames = { "_Spheres", "_Lights", "_Triangles", "_Meshes", "_BvhNodes", "_TopLevelBvhNodes", "_ShadowBvhNodes", "_CausticPhotons", "_TerrainCells", "RegressionResults", "_FocusQueryResult" };
        public readonly string[] AdaptiveBufferNames = { "AdaptiveWorkList", "AdaptiveTraceWorkList", "AdaptiveGroupState", "AdaptiveGroupInfo", "AdaptiveProbeGroups", "AdaptiveGroupBucket", "AdaptiveGroupExtraDemand", "AdaptiveRawBucketDemand", "AdaptiveWorkListMetadata", "AdaptiveDispatchArgs" };

        public DummyResources()
        {
            TextureArray = new Texture2DArray(1, 1, 1, TextureFormat.RGBA32, false);
            TextureArray.SetPixels(new[] { UnityEngine.Color.white }, 0, 0);
            TextureArray.Apply(false, true);
        }

        public ComputeBuffer GetStructuredBuffer(string name)
        {
            switch (name)
            {
                case "_Spheres": return sphereBuffer;
                case "_Lights": return lightBuffer;
                case "_Triangles": return triangleBuffer;
                case "_Meshes":
                case "_BvhNodes":
                case "_TopLevelBvhNodes":
                case "_ShadowBvhNodes": return meshAndBvhBuffer;
                case "_CausticPhotons": return causticPhotonBuffer;
                case "_TerrainCells": return terrainCellBuffer;
                default: return float4Buffer;
            }
        }

        public ComputeBuffer GetAdaptiveBuffer(string name)
        {
            switch (name)
            {
                case "AdaptiveWorkList":
                case "AdaptiveTraceWorkList": return uint2Buffer;
                case "AdaptiveGroupState":
                case "AdaptiveGroupInfo":
                case "AdaptiveProbeGroups": return float4Buffer;
                default: return uintBuffer;
            }
        }

        public void WaitForGpu()
        {
            var previous = RenderTexture.active;
            RenderTexture.active = Color;
            var readback = new Texture2D(1, 1, TextureFormat.RGBAFloat, false);
            readback.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); readback.Apply();
            RenderTexture.active = previous;
            Object.DestroyImmediate(readback);
        }

        public void Dispose()
        {
            FloatBuffer.Release(); sphereBuffer.Release(); lightBuffer.Release(); triangleBuffer.Release();
            meshAndBvhBuffer.Release(); causticPhotonBuffer.Release(); terrainCellBuffer.Release();
            float4Buffer.Release(); uint2Buffer.Release(); uintBuffer.Release(); Color.Release(); Scalar.Release();
            Object.DestroyImmediate(TextureArray); Object.DestroyImmediate(Color); Object.DestroyImmediate(Scalar);
        }

        private static RenderTexture CreateTexture(RenderTextureFormat format)
        {
            var texture = new RenderTexture(8, 8, 0, format) { enableRandomWrite = true };
            texture.Create();
            return texture;
        }
    }

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Caustics")]
    private static void PrecompileCaustics()
    {
        var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(CausticsShaderPath);
        if (shader == null) { Debug.LogError($"Precompile failed: could not load '{CausticsShaderPath}'."); return; }
        ClearShaderCache(); AssetDatabase.ImportAsset(CausticsShaderPath, ImportAssetOptions.ForceUpdate);
        shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(CausticsShaderPath);
        if (shader == null || !LogCompileMessages("Caustics", shader)) return;
        using (var resources = new DummyResources())
        {
            int kernel = shader.FindKernel("CSCausticsDebug");
            BindResources(shader, kernel, resources);
            shader.SetInt("_NumCausticTargetPairs", 0);
            PathTracing.ComputeDispatch.Dispatch(shader, kernel, 1, 1, 1);
            resources.WaitForGpu();
        }
        Debug.Log("Caustics compute shader precompile dispatched successfully.");
    }

    private static void AppendStatsRow(string assetPath, string kernel, string variant, long coldMilliseconds, long warmMilliseconds)
    {
        var statsPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, StatsPath);
        bool hasV2Header = false;
        if (File.Exists(statsPath))
        {
            foreach (var line in File.ReadLines(statsPath)) if (line == StatsHeader) { hasV2Header = true; break; }
        }
        string timestamp = System.DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        string hash = AssetDatabase.GetAssetDependencyHash(assetPath).ToString();
        string row = string.Join(",", Quote(timestamp), Quote(Application.unityVersion), Quote(EditorUserBuildSettings.activeBuildTarget.ToString()), Quote(SystemInfo.graphicsDeviceName), Quote(assetPath), Quote(kernel), Quote(hash), Quote(variant), coldMilliseconds, warmMilliseconds);
        File.AppendAllText(statsPath, (hasV2Header ? string.Empty : StatsHeader + System.Environment.NewLine) + row + System.Environment.NewLine);
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static bool HasCommandLineArgument(string argument)
    {
        foreach (var value in System.Environment.GetCommandLineArgs()) if (value == argument) return true;
        return false;
    }

    private static string GetCommandLineArgumentValue(string argument)
    {
        var arguments = System.Environment.GetCommandLineArgs();
        for (var index = 0; index + 1 < arguments.Length; index++)
        {
            if (arguments[index] == argument) return arguments[index + 1];
        }

        return null;
    }

    private static void ClearShaderCache()
    {
        var projectPath = Directory.GetParent(Application.dataPath).FullName;
        try
        {
            var cache = Path.Combine(projectPath, "Library", "ShaderCache");
            var database = Path.Combine(projectPath, "Library", "ShaderCache.db");
            if (Directory.Exists(cache)) Directory.Delete(cache, true);
            if (File.Exists(database)) File.Delete(database);
            Debug.Log("Cleared Unity's generated shader cache before precompilation.");
        }
        catch (System.Exception exception) { Debug.LogWarning($"Could not fully clear Unity's generated shader cache: {exception.Message}"); }
    }

    private static void SetKeyword(ComputeShader shader, string keyword, bool enabled)
    {
        if (enabled) shader.EnableKeyword(keyword); else shader.DisableKeyword(keyword);
    }
}
