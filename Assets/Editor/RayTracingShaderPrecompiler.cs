using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Editor utility to force-compile the ray tracing compute shader from edit mode, so a slow or
// failing kernel compile shows up here (with timing and messages) instead of stalling Unity
// when you hit Play. Unity compiles compute kernels lazily on the first Dispatch, which is why
// problems only surfaced on Play; this dispatches once up front to trigger that work now.
public static class RayTracingShaderPrecompiler
{
    private const string ShaderPath = "Assets/Scripts/RayTracingCompute.compute";

    [MenuItem("Tools/Ray Tracing/Precompile Compute Shader")]
    private static void Precompile()
    {
        var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath);
        if (shader == null)
        {
            Debug.LogError($"Precompile failed: could not load compute shader at '{ShaderPath}'.");
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

        // 2) Force the real GPU dispatch (the lazy step Play triggers) on a tiny render target,
        //    timing how long the first dispatch takes. A pathological kernel will block here.
        int kernel = shader.FindKernel("CSMain");

        var rt = new RenderTexture(8, 8, 0, RenderTextureFormat.ARGBFloat)
        {
            enableRandomWrite = true
        };
        rt.Create();

        var stopwatch = Stopwatch.StartNew();
        try
        {
            shader.SetTexture(kernel, "Result", rt);
            // Single thread group; we only care about triggering compilation, not output.
            shader.Dispatch(kernel, 1, 1, 1);

            // Read back to force the GPU to actually run the kernel before we stop timing.
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var readback = new Texture2D(8, 8, TextureFormat.RGBAFloat, false);
            readback.ReadPixels(new Rect(0, 0, 8, 8), 0, 0);
            readback.Apply();
            RenderTexture.active = prev;
            Object.DestroyImmediate(readback);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Precompile dispatch threw: {e.Message}\n{e}");
            return;
        }
        finally
        {
            stopwatch.Stop();
            rt.Release();
            Object.DestroyImmediate(rt);
        }

        Debug.Log(
            $"Ray tracing compute shader precompiled and dispatched in " +
            $"{stopwatch.ElapsedMilliseconds} ms. Safe to enter Play mode.");
    }
}
