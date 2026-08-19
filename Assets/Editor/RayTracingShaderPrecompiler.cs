using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Editor utility to force-compile the ray tracing compute shader from edit mode, so a slow or
// failing kernel compile shows up here (with timing and messages) instead of stalling Unity
// when you hit Play. Unity compiles compute kernels lazily on the first Dispatch, which is why
// problems only surfaced on Play; this dispatches every bounded production variant up front to
// trigger that work now.
public static class RayTracingShaderPrecompiler
{
    private const string ShaderPath = "Assets/Scripts/RayTracingCompute.compute";
    private const string CausticsShaderPath = "Assets/Resources/RayTracingCaustics.compute";
    private const string ProgressTitle = "Precompiling ray tracing shader";
    private const string StatsPath = "Library/RayTracingShaderCompileStats.csv";

    private readonly struct Variant
    {
        public readonly bool Debug;
        public readonly bool Fog;
        public readonly bool Terrain;

        public Variant(bool debug, bool fog, bool terrain)
        {
            Debug = debug;
            Fog = fog;
            Terrain = terrain;
        }

        public string Label => $"debug={(Debug ? 1 : 0)};fog={(Fog ? 1 : 0)};terrain={(Terrain ? 1 : 0)}";
        public string Type => Debug ? "Debug" : "Final-color";
    }

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader")]
    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/All Variants")]
    private static void PrecompileAllVariants()
    {
        Precompile(-1);
    }

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Final Color - Default")]
    private static void PrecompileDefaultVariant()
    {
        Precompile(0);
    }

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Final Color - Fog")]
    private static void PrecompileFinalColorFogVariant()
    {
        Precompile(1);
    }

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Final Color - Terrain")]
    private static void PrecompileFinalColorTerrainVariant()
    {
        Precompile(2);
    }

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Final Color - Fog + Terrain")]
    private static void PrecompileFinalColorFogTerrainVariant()
    {
        Precompile(3);
    }

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Debug - Default")]
    private static void PrecompileDebugVariant()
    {
        Precompile(4);
    }

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Debug - Fog")]
    private static void PrecompileDebugFogVariant()
    {
        Precompile(5);
    }

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Debug - Terrain")]
    private static void PrecompileDebugTerrainVariant()
    {
        Precompile(6);
    }

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Debug - Fog + Terrain")]
    private static void PrecompileDebugFogTerrainVariant()
    {
        Precompile(7);
    }

    private static void Precompile(int selectedVariant)
    {
        var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
        if (shader == null)
        {
            Debug.LogError($"Precompile failed: could not load compute shader at '{ShaderPath}'.");
            return;
        }

        var allVariants = CreateVariants();
        var variants = selectedVariant >= 0
            ? new[] { allVariants[selectedVariant] }
            : allVariants;
        var stats = new StringBuilder();
        stats.AppendLine("timestamp,unityVersion,buildTarget,graphicsDevice,shaderHash,variant,coldDispatchMs,warmDispatchMs");
        // ComputeShader is not a UnityEngine.Shader, so ShaderUtil.ClearCachedData cannot be used
        // here. Clear Unity's generated shader cache, then force-reimport the asset before the
        // first dispatch of the selected keyword combination.
        ClearShaderCache();
        AssetDatabase.ImportAsset(ShaderPath, ImportAssetOptions.ForceUpdate);
        shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
        if (shader == null)
        {
            Debug.LogError($"Precompile failed after reimport: could not load compute shader at '{ShaderPath}'.");
            return;
        }

        // 1) Force the HLSL -> backend compile and surface any compile messages. This is the
        //    step that was hanging; if it errors, the messages explain why.
        var messages = ShaderUtil.GetComputeShaderMessages(shader);
        bool hasError = false;
        foreach (var message in messages)
        {
            string formatted =
                $"[{message.platform}] {message.message}\n{message.messageDetails}";
            if (message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
            {
                hasError = true;
                Debug.LogError($"Compute shader error: {formatted}");
            }
            else
            {
                Debug.LogWarning($"Compute shader warning: {formatted}");
            }
        }

        if (hasError)
        {
            Debug.LogError("Precompile aborted: the compute shader has compile errors (see above).");
            return;
        }

        // 2) Force the real GPU dispatch (the lazy step Play triggers) on a tiny render target.
        // Fog and terrain deliberately isolate their large paths behind keywords, so warm the
        // bounded DEBUG_RENDER x FOG_ENABLED x TERRAIN_ENABLED matrix here rather than making a
        // scene switch discover a cold variant.
        int kernel = shader.FindKernel("CSMain");

        var rt = new RenderTexture(8, 8, 0, RenderTextureFormat.ARGBFloat)
        {
            enableRandomWrite = true
        };
        rt.Create();
        var featureColor = new RenderTexture(8, 8, 0, RenderTextureFormat.ARGBFloat)
        {
            enableRandomWrite = true
        };
        featureColor.Create();
        var featureScalar = new RenderTexture(8, 8, 0, RenderTextureFormat.RFloat)
        {
            enableRandomWrite = true
        };
        featureScalar.Create();
        var terrainCells = new ComputeBuffer(1, sizeof(float) * 2);
        terrainCells.SetData(new[] { Vector2.zero });
        var terrainHeights = new ComputeBuffer(1, sizeof(float));
        terrainHeights.SetData(new[] { 0.0f });
        var dummyTextureArrays = CreateDummyTextureArrays();
        var dummyStructuredBuffers = CreateDummyStructuredBuffers();

        var totalStopwatch = Stopwatch.StartNew();
        var completedVariants = 0;
        long totalColdDispatchMs = 0;
        long totalWarmDispatchMs = 0;
        bool cancelled = false;
        try
        {
            shader.SetInt("_CausticsEnabled", 0);
            shader.SetInt("_EnvironmentLightEnabled", 0);
            shader.SetInt("_NumLights", 0);
            shader.SetInt("_NumberOfPasses", 1);
            shader.SetInt("_NumBounces", 1);
            shader.SetVector("_FogBoundsMin", Vector3.zero);
            shader.SetVector("_FogBoundsMax", Vector3.one);
            shader.SetVector("_FogScatteringAlbedo", Vector3.zero);
            shader.SetFloat("_FogDensity", 0.0f);
            shader.SetFloat("_FogInScatteringIntensity", 0.0f);
            shader.SetInt("_FogMultipleScattering", 0);
            shader.SetTexture(kernel, "_SkyboxTexture", Texture2D.blackTexture);
            shader.SetTexture(kernel, "_MeshAlbedoTextures", dummyTextureArrays[0]);
            shader.SetTexture(kernel, "_MeshMetallicRoughnessTextures", dummyTextureArrays[1]);
            shader.SetTexture(kernel, "_MeshNormalTextures", dummyTextureArrays[2]);
            shader.SetTexture(kernel, "_MeshParallaxTextures", dummyTextureArrays[3]);
            shader.SetBuffer(kernel, "_EnvironmentConditionalCdf", dummyStructuredBuffers[0]);
            shader.SetBuffer(kernel, "_EnvironmentMarginalCdf", dummyStructuredBuffers[1]);
            shader.SetBuffer(kernel, "_Spheres", dummyStructuredBuffers[2]);
            shader.SetBuffer(kernel, "_Lights", dummyStructuredBuffers[3]);
            shader.SetBuffer(kernel, "_MeshLightTriangleCdf", dummyStructuredBuffers[4]);
            shader.SetBuffer(kernel, "_Triangles", dummyStructuredBuffers[5]);
            shader.SetBuffer(kernel, "_Meshes", dummyStructuredBuffers[6]);
            shader.SetBuffer(kernel, "_BvhNodes", dummyStructuredBuffers[7]);
            shader.SetBuffer(kernel, "_TopLevelBvhNodes", dummyStructuredBuffers[8]);
            shader.SetBuffer(kernel, "_ShadowBvhNodes", dummyStructuredBuffers[9]);
            shader.SetBuffer(kernel, "_CausticPhotons", dummyStructuredBuffers[10]);
            shader.SetBuffer(kernel, "_CausticPhotonMetadata", dummyStructuredBuffers[11]);
            shader.SetBuffer(kernel, "_CausticGridCellHeads", dummyStructuredBuffers[12]);
            shader.SetBuffer(kernel, "_CausticPhotonNext", dummyStructuredBuffers[13]);
            shader.SetTexture(kernel, "Result", rt);
            shader.SetTexture(kernel, "AccumulationResult", featureColor);
            shader.SetTexture(kernel, "Beauty", featureColor);
            shader.SetTexture(kernel, "FeatureNormal", featureColor);
            shader.SetTexture(kernel, "FeatureAlbedo", featureColor);
            shader.SetTexture(kernel, "FeatureDepth", featureScalar);
            shader.SetTexture(kernel, "FeatureIdentity", featureScalar);
            shader.SetTexture(kernel, "FeatureValidity", featureScalar);
            shader.SetTexture(kernel, "_TerrainAlphamap", Texture2D.blackTexture);
            shader.SetTexture(kernel, "_TerrainLayer0", Texture2D.whiteTexture);
            shader.SetTexture(kernel, "_TerrainLayer1", Texture2D.whiteTexture);
            shader.SetTexture(kernel, "_TerrainLayer2", Texture2D.whiteTexture);
            shader.SetTexture(kernel, "_TerrainLayer3", Texture2D.whiteTexture);
            shader.SetTexture(kernel, "_TerrainNormal0", Texture2D.normalTexture);
            shader.SetTexture(kernel, "_TerrainNormal1", Texture2D.normalTexture);
            shader.SetTexture(kernel, "_TerrainNormal2", Texture2D.normalTexture);
            shader.SetTexture(kernel, "_TerrainNormal3", Texture2D.normalTexture);
            shader.SetTexture(kernel, "_TerrainMask0", Texture2D.whiteTexture);
            shader.SetTexture(kernel, "_TerrainMask1", Texture2D.whiteTexture);
            shader.SetTexture(kernel, "_TerrainMask2", Texture2D.whiteTexture);
            shader.SetTexture(kernel, "_TerrainMask3", Texture2D.whiteTexture);
            shader.SetBuffer(kernel, "_TerrainCells", terrainCells);
            shader.SetBuffer(kernel, "_TerrainHeights", terrainHeights);
            shader.SetVector("_TerrainSize", Vector3.one);
            shader.SetInt("_TerrainCellResolution", 1);
            shader.SetInt("_TerrainHeightmapResolution", 1);

            for (var variantIndex = 0; variantIndex < variants.Length; variantIndex++)
            {
                var variant = variants[variantIndex];
                var remaining = variants.Length - variantIndex - 1;
                var progressText = variants.Length == 1
                    ? $"{variant.Label} variant compiling. 1 of 1 variant"
                    : $"{variant.Label} variant compiling. {remaining} of {variants.Length} variants remaining";
                if (EditorUtility.DisplayCancelableProgressBar(
                        ProgressTitle, progressText, variantIndex / (float)variants.Length))
                {
                    cancelled = true;
                    break;
                }

                SetKeyword(shader, "DEBUG_RENDER", variant.Debug);
                SetKeyword(shader, "FOG_ENABLED", variant.Fog);
                SetKeyword(shader, "TERRAIN_ENABLED", variant.Terrain);

                var coldStopwatch = Stopwatch.StartNew();
                PathTracing.ComputeDispatch.Dispatch(shader, kernel, 1, 1, 1);
                coldStopwatch.Stop();
                var warmStopwatch = Stopwatch.StartNew();
                PathTracing.ComputeDispatch.Dispatch(shader, kernel, 1, 1, 1);
                warmStopwatch.Stop();
                totalColdDispatchMs += coldStopwatch.ElapsedMilliseconds;
                totalWarmDispatchMs += warmStopwatch.ElapsedMilliseconds;

                var timestamp = System.DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                var shaderHash = AssetDatabase.GetAssetDependencyHash(ShaderPath).ToString();
                var row = string.Join(",", Quote(timestamp), Quote(Application.unityVersion),
                    Quote(EditorUserBuildSettings.activeBuildTarget.ToString()), Quote(SystemInfo.graphicsDeviceName),
                    Quote(shaderHash), Quote(variant.Label), coldStopwatch.ElapsedMilliseconds,
                    warmStopwatch.ElapsedMilliseconds);
                stats.AppendLine(row);
                AppendStatsRow(stats, row);
                Debug.Log($"Ray tracing shader variant ({variant.Label}) cold={coldStopwatch.ElapsedMilliseconds} ms, " +
                    $"warm={warmStopwatch.ElapsedMilliseconds} ms.");
                completedVariants++;
            }

            if (!cancelled)
            {
                // Read back to force the GPU to execute the queued dispatches before completion.
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var readback = new Texture2D(8, 8, TextureFormat.RGBAFloat, false);
                readback.ReadPixels(new Rect(0, 0, 8, 8), 0, 0);
                readback.Apply();
                RenderTexture.active = prev;
                Object.DestroyImmediate(readback);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Precompile dispatch threw: {e.Message}\n{e}");
            return;
        }
        finally
        {
            totalStopwatch.Stop();
            EditorUtility.ClearProgressBar();
            SetKeyword(shader, "DEBUG_RENDER", false);
            SetKeyword(shader, "FOG_ENABLED", false);
            SetKeyword(shader, "TERRAIN_ENABLED", false);
            terrainCells.Release();
            terrainHeights.Release();
            foreach (var buffer in dummyStructuredBuffers)
            {
                buffer.Release();
            }
            foreach (var textureArray in dummyTextureArrays)
            {
                Object.DestroyImmediate(textureArray);
            }
            rt.Release();
            featureColor.Release();
            featureScalar.Release();
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(featureColor);
            Object.DestroyImmediate(featureScalar);
        }

        if (cancelled)
        {
            Debug.LogWarning($"Ray tracing shader precompile cancelled after {completedVariants} of {variants.Length} variants.");
            return;
        }

        var statsPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, StatsPath);
        Debug.Log(
            $"Ray tracing compute shader recompiled and dispatched across {variants.Length} selected variant(s) in " +
            $"{totalStopwatch.ElapsedMilliseconds} ms. Cold dispatch total={totalColdDispatchMs} ms, " +
            $"warm dispatch total={totalWarmDispatchMs} ms. Stats appended to '{statsPath}'. Safe to enter Play mode.");
    }

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader/Caustics")]
    private static void PrecompileCaustics()
    {
        var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(CausticsShaderPath);
        if (shader == null)
        {
            Debug.LogError($"Precompile failed: could not load compute shader at '{CausticsShaderPath}'.");
            return;
        }

        ClearShaderCache();
        AssetDatabase.ImportAsset(CausticsShaderPath, ImportAssetOptions.ForceUpdate);
        shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(CausticsShaderPath);
        var messages = ShaderUtil.GetComputeShaderMessages(shader);
        foreach (var message in messages)
        {
            var formatted = $"[{message.platform}] {message.message}\n{message.messageDetails}";
            if (message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
            {
                Debug.LogError($"Caustics compute shader error: {formatted}");
            }
            else
            {
                Debug.LogWarning($"Caustics compute shader warning: {formatted}");
            }
        }

        int kernel = shader.FindKernel("CSCausticsDebug");
        var result = new RenderTexture(8, 8, 0, RenderTextureFormat.ARGBFloat) { enableRandomWrite = true };
        result.Create();
        try
        {
            shader.SetInt("_NumberOfPasses", 1);
            shader.SetInt("_NumBounces", 1);
            shader.SetInt("_NumCausticTargetPairs", 0);
            shader.SetTexture(kernel, "Result", result);
            PathTracing.ComputeDispatch.Dispatch(shader, kernel, 1, 1, 1);
            Debug.Log("Caustics compute shader precompile dispatched successfully.");
        }
        finally
        {
            result.Release();
            Object.DestroyImmediate(result);
        }
    }

    private static Variant[] CreateVariants()
    {
        var variants = new Variant[8];
        var index = 0;
        for (var debug = 0; debug <= 1; debug++)
        {
            for (var fog = 0; fog <= 1; fog++)
            {
                for (var terrain = 0; terrain <= 1; terrain++)
                {
                    variants[index++] = new Variant(debug != 0, fog != 0, terrain != 0);
                }
            }
        }

        return variants;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static ComputeBuffer[] CreateDummyStructuredBuffers()
    {
        var buffers = new[]
        {
            new ComputeBuffer(1, 4),
            new ComputeBuffer(1, 4),
            new ComputeBuffer(1, 92),
            new ComputeBuffer(1, 88),
            new ComputeBuffer(1, 4),
            new ComputeBuffer(1, 260),
            new ComputeBuffer(1, 48),
            new ComputeBuffer(1, 48),
            new ComputeBuffer(1, 48),
            new ComputeBuffer(1, 48),
            new ComputeBuffer(1, 36),
            new ComputeBuffer(1, 4),
            new ComputeBuffer(1, 4),
            new ComputeBuffer(1, 4)
        };

        return buffers;
    }

    private static Texture2DArray[] CreateDummyTextureArrays()
    {
        var arrays = new Texture2DArray[4];
        for (var i = 0; i < arrays.Length; i++)
        {
            arrays[i] = new Texture2DArray(1, 1, 1, TextureFormat.RGBA32, false)
            {
                name = "RayTracingPrecompileDummyTextureArray"
            };
            arrays[i].SetPixels(new[] { Color.white }, 0, 0);
            arrays[i].Apply(false, true);
        }

        return arrays;
    }

    private static void ClearShaderCache()
    {
        var projectPath = Directory.GetParent(Application.dataPath).FullName;
        var shaderCachePath = Path.Combine(projectPath, "Library", "ShaderCache");
        var shaderCacheDatabasePath = Path.Combine(projectPath, "Library", "ShaderCache.db");

        try
        {
            if (Directory.Exists(shaderCachePath))
            {
                Directory.Delete(shaderCachePath, true);
            }

            if (File.Exists(shaderCacheDatabasePath))
            {
                File.Delete(shaderCacheDatabasePath);
            }

            Debug.Log("Cleared Unity's generated shader cache before precompilation.");
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning($"Could not fully clear Unity's generated shader cache: {exception.Message}");
        }
    }

    private static void AppendStatsRow(StringBuilder stats, string row)
    {
        var statsPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, StatsPath);
        var includeHeader = !File.Exists(statsPath) || new FileInfo(statsPath).Length == 0;
        File.AppendAllText(statsPath, includeHeader
            ? stats.ToString()
            : row + System.Environment.NewLine);
    }

    private static void SetKeyword(ComputeShader shader, string keyword, bool enabled)
    {
        if (enabled)
        {
            shader.EnableKeyword(keyword);
        }
        else
        {
            shader.DisableKeyword(keyword);
        }
    }
}
