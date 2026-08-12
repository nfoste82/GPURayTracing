using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PathTracing.Camera;
using PathTracing.Lighting;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[CustomEditor(typeof(GameManager))]
public sealed class GameManagerEditor : Editor
{
    private const string FoldoutPrefix = "GPURayTracing.GameManagerInspector";

    private enum BakeStatus
    {
        NotBaked,
        Baked,
        OutOfDate
    }

    private void OnEnable()
    {
        EditorApplication.projectChanged += Repaint;
        EditorApplication.hierarchyChanged += Repaint;
    }

    private void OnDisable()
    {
        EditorApplication.projectChanged -= Repaint;
        EditorApplication.hierarchyChanged -= Repaint;
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        var manager = (GameManager)target;

        DrawSection(manager, "Render Quality", true, () =>
        {
            DrawProperty("renderResolutionPercent", "Render Resolution (%)");
            DrawProperty("numberOfPasses");
            DrawProperty("numBounces");
            DrawProperty("shadowQuality");
            DrawProperty("shadowRandomness", "Local Light Shadow Randomness");
            DrawProperty("subpixelJitterScale");
            DrawProperty("enableFrameAccumulation");
            DrawProperty("_singleFrame", "Render Paused View");
            DrawProperty("fireflyClamp");
            DrawProperty("randomNoise");
            DrawProperty("parallaxMaximumStrengthAngle");
        });
        DrawSection(manager, "Lighting", true, () =>
        {
            DrawProperty("lightSamplingStrategy");
            if (serializedObject.FindProperty("lightSamplingStrategy").enumValueIndex != (int)LightSamplingStrategy.AllLights)
            {
                DrawProperty("lightSampleCount");
            }
            DrawProperty("lightFalloffScale", "Local Light Falloff Scale");
            DrawDirectionalLighting(manager);
        });
        DrawSection(manager, "Image and Environment", true, () =>
        {
            DrawProperty("exposure");
            DrawProperty("_skyboxLightColor", "Skybox Light Color");
            DrawProperty("skyboxTexture");
        });
        DrawSection(manager, "Camera", true, () => DrawCameraSettings(manager));
        DrawSection(manager, "Denoising", false, DrawDenoising);
        DrawSection(manager, "Volumetric Fog", false, DrawVolumetricFog);
        DrawSection(manager, "Caustics", false, DrawCaustics);
        DrawSection(manager, "Acceleration Structures", false, () =>
        {
            DrawProperty("topLevelBvhMinObjectCount");
            DrawProperty("shadowBvhMinObjectCount");
        });
        DrawSection(manager, "BVH Baking", false, () => DrawBvhBaking(manager));
        DrawSection(manager, "Video Capture", false, () => DrawVideoCaptureSettings(manager));
        DrawSection(manager, "Image Export", true, () => DrawImageExport(manager));
        DrawSection(manager, "Diagnostics", false, () =>
        {
            DrawProperty("profileStartup");
            DrawProperty("debugRenderMode");
            DrawProperty("maxLightSamples");
        });
        DrawSection(manager, "Setup", true, () =>
        {
            DrawProperty("shader");
            DrawProperty("renderTextureCamera");
        });

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawBvhBaking(GameManager manager)
    {
        List<RayTracingBvhBakeAsset.MeshEntry> entries = RayTracingBvhBakeUtility.GetMeshEntries(manager);
        string signature = RayTracingBvhBakeUtility.CalculateSignature(entries);
        BakeStatus status = GetBakeStatus(manager.EditorBvhBake, signature);

        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || string.IsNullOrEmpty(manager.gameObject.scene.path)))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Bake BVH", GUILayout.Width(200.0f)))
                {
                    serializedObject.ApplyModifiedProperties();
                    RayTracingBvhBakeUtility.Bake(manager, entries, signature);
                    serializedObject.Update();
                }

                GUILayout.Label($"Status: {GetStatusLabel(status)}", EditorStyles.boldLabel, GUILayout.Width(180.0f));
            }
        }

        DrawProperty("bakeBvhUponExit", "Bake upon exit");
    }

    private void DrawCameraSettings(GameManager manager)
    {
        EditorGUILayout.LabelField("Camera Controls", EditorStyles.boldLabel);
        DrawProperty("cameraBehavior");
        DrawProperty("cameraMovementSpeed");
        if ((CameraBehavior)serializedObject.FindProperty("cameraBehavior").enumValueIndex == CameraBehavior.OrbitFocusPoint)
        {
            DrawProperty("cameraFocusPosition");
            DrawProperty("cameraOrbitZoom");
        }
        if (manager.renderTextureCamera != null)
        {
            var cameraObject = new SerializedObject(manager.renderTextureCamera);
            cameraObject.Update();
            EditorGUILayout.PropertyField(cameraObject.FindProperty("field of view"), new GUIContent("Field Of View"));
            cameraObject.ApplyModifiedProperties();
        }
        else
        {
            EditorGUILayout.HelpBox("Assign a render texture camera to edit its field of view here.", MessageType.Info);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Focus and Lens", EditorStyles.boldLabel);
        DrawProperty("cameraAutoFocus");
        DrawProperty("enableClickToFocus");
        using (new EditorGUI.DisabledScope(!serializedObject.FindProperty("enableClickToFocus").boolValue))
        {
            DrawProperty("trackClickedFocusPoint");
        }
        DrawProperty("autoFocusTransparentOpacityThreshold");
        DrawProperty("cameraFocalDistance");
        DrawProperty("cameraApertureMode");

        CameraApertureMode apertureMode = (CameraApertureMode)serializedObject
            .FindProperty("cameraApertureMode").enumValueIndex;
        if (apertureMode == CameraApertureMode.LensRadius)
        {
            DrawProperty("cameraApertureRadius");
        }
        else if (apertureMode == CameraApertureMode.FStop)
        {
            DrawProperty("cameraFStop");
            DrawProperty("cameraApertureScale");
        }

        DrawProperty("cameraApertureBladeCount");
        if (serializedObject.FindProperty("cameraApertureBladeCount").intValue >= 3)
        {
            DrawProperty("cameraApertureBladeRotation");
        }
        DrawProperty("cameraAnamorphicRatio");
    }

    private static void DrawDirectionalLighting(GameManager manager)
    {
        RayDirectionalLight[] directionalLights = manager.GetComponentsInChildren<RayDirectionalLight>(true);
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Directional Lighting", EditorStyles.boldLabel);
        if (directionalLights.Length == 0)
        {
            EditorGUILayout.HelpBox("No RayDirectionalLight component is present beneath this GameManager.", MessageType.Info);
            return;
        }

        for (int i = 0; i < directionalLights.Length; i++)
        {
            RayDirectionalLight directionalLight = directionalLights[i];
            var lightObject = new SerializedObject(directionalLight);
            lightObject.Update();
            if (directionalLights.Length > 1)
            {
                EditorGUILayout.LabelField(directionalLight.name, EditorStyles.miniBoldLabel);
            }
            EditorGUILayout.PropertyField(lightObject.FindProperty("Intensity"));
            EditorGUILayout.PropertyField(lightObject.FindProperty("AngularRadius"));
            lightObject.ApplyModifiedProperties();
        }
    }

    private void DrawDenoising()
    {
        DrawProperty("enableSpatialDenoising");
        using (new EditorGUI.DisabledScope(!serializedObject.FindProperty("enableSpatialDenoising").boolValue))
        {
            DrawProperty("spatialDenoiserIterations");
            DrawProperty("spatialDenoiserDepthSigma");
            DrawProperty("spatialDenoiserNormalPower");
            DrawProperty("spatialDenoiserAlbedoSigma");
            DrawProperty("spatialDenoiserLuminanceSigma");
        }

        EditorGUILayout.Space();
        DrawProperty("enableTemporalDenoising");
        using (new EditorGUI.DisabledScope(!serializedObject.FindProperty("enableTemporalDenoising").boolValue))
        {
            DrawProperty("temporalMaxHistoryLength");
            DrawProperty("temporalMotionDistance");
            DrawProperty("temporalMotionAngle");
            DrawProperty("temporalDepthThreshold");
            DrawProperty("temporalNormalThreshold");
            DrawProperty("temporalCameraCutDistance");
            DrawProperty("temporalCameraCutAngle");
            DrawProperty("temporalVarianceGuidedFiltering");
        }

        if (serializedObject.FindProperty("enableSpatialDenoising").boolValue
            || serializedObject.FindProperty("enableTemporalDenoising").boolValue)
        {
            EditorGUILayout.Space();
            DrawProperty("causticPreservationThreshold");
        }
    }

    private void DrawVolumetricFog()
    {
        DrawProperty("enableVolumetricFog");
        using (new EditorGUI.DisabledScope(!serializedObject.FindProperty("enableVolumetricFog").boolValue))
        {
            DrawProperty("fogDensityScale");
            DrawProperty("fogScatteringScale");
            DrawProperty("fogInScatteringIntensity");
            DrawProperty("enableFogMultipleScattering");
        }
    }

    private void DrawCaustics()
    {
            DrawProperty("enableCaustics");
            using (new EditorGUI.DisabledScope(!serializedObject.FindProperty("enableCaustics").boolValue))
            {
                DrawProperty("causticPhotonCount");
                DrawProperty("causticGatherRadius");
                DrawProperty("causticIntensity");
            }
    }

    private void DrawSection(GameManager manager, string title, bool defaultExpanded, Action content)
    {
        string key = $"{FoldoutPrefix}.{GetManagerIdentifier(manager)}.{title}";
        bool expanded = EditorPrefs.GetBool(key, defaultExpanded);
        expanded = EditorGUILayout.BeginFoldoutHeaderGroup(expanded, title);
        EditorPrefs.SetBool(key, expanded);
        if (expanded)
        {
            EditorGUI.indentLevel++;
            content();
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2.0f);
        }
        EditorGUILayout.EndFoldoutHeaderGroup();
    }

    private void DrawProperty(string propertyPath, string label = null)
    {
        SerializedProperty property = serializedObject.FindProperty(propertyPath);
        EditorGUILayout.PropertyField(property, label == null ? null : new GUIContent(label));
    }

    private static string GetManagerIdentifier(GameManager manager)
    {
        return GlobalObjectId.GetGlobalObjectIdSlow(manager).ToString();
    }

    private void DrawVideoCaptureSettings(GameManager manager)
    {
        EditorGUILayout.PropertyField(
            serializedObject.FindProperty("videoSamplesPerFrame"),
            new GUIContent("Quality Samples Per Output Frame"));
        EditorGUILayout.PropertyField(
            serializedObject.FindProperty("videoFrameTimeStep"),
            new GUIContent("Output Frame Time Step (seconds)"));
        EditorGUILayout.PropertyField(
            serializedObject.FindProperty("videoDuration"),
            new GUIContent("Video Duration (seconds)"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("videoOutputFolder"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("videoEncodeMp4"));
        if (serializedObject.FindProperty("videoEncodeMp4").boolValue)
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("videoFfmpegPath"));
        }

        int frameCount = GameManager.CalculateVideoFrameCount(manager.videoDuration, manager.videoFrameTimeStep);
        var overlay = manager.GetComponent<RayTracingBenchmarkOverlay>();
        float averageFrameMs = overlay != null ? overlay.AverageFrameMs : 0.0f;
        double estimateSeconds = GameManager.EstimateVideoCaptureSeconds(
            frameCount,
            manager.videoSamplesPerFrame,
            Mathf.Max(1, manager.numberOfPasses),
            averageFrameMs,
            manager.enableCaustics);
        string estimate = estimateSeconds > 0.0
            ? FormatDuration(estimateSeconds)
            : "available after frame statistics have been collected in Play mode";
        EditorGUILayout.HelpBox(
            $"Output frames: {frameCount:N0} ({manager.videoDuration:0.###} seconds / {manager.videoFrameTimeStep:0.######}-second timestep)\n" +
            $"Quality: {manager.videoSamplesPerFrame:N0} samples accumulated into each output frame\n" +
            $"Estimated render time: {estimate}\n" +
            "Changing quality samples does not change the output frame count. Lossless PNG frames are retained; ffmpeg creates video.mp4 after capture. Encoding and disk-write time are not included in the estimate.",
            MessageType.Info);

        if (manager.IsVideoEncodingActive)
        {
            EditorGUILayout.HelpBox($"Encoding MP4 with ffmpeg...\n{manager.VideoOutputPath}", MessageType.Info);
            Repaint();
        }
        else if (manager.IsVideoCaptureActive)
        {
            float progress = manager.VideoCaptureFrameCount > 0
                ? manager.VideoCaptureCompletedFrameCount / (float)manager.VideoCaptureFrameCount
                : 0.0f;
            EditorGUI.ProgressBar(
                EditorGUILayout.GetControlRect(false, 20.0f),
                progress,
                $"{manager.VideoCaptureCompletedFrameCount:N0} / {manager.VideoCaptureFrameCount:N0} frames");
            EditorGUILayout.LabelField("Output", manager.VideoCaptureDirectory ?? string.Empty);
            if (GUILayout.Button("Cancel Capture"))
            {
                manager.CancelVideoCapture();
            }
            Repaint();
        }
        else
        {
            using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying || frameCount <= 0 || manager.IsVideoEncodingActive))
            {
                if (GUILayout.Button("Start Video Capture"))
                {
                    serializedObject.ApplyModifiedProperties();
                    manager.StartVideoCapture();
                }
            }
        }
    }

    private static void DrawImageExport(GameManager manager)
    {
        using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying))
        {
            if (GUILayout.Button("Save Image"))
            {
                string path = EditorUtility.SaveFilePanel("Save Ray-Traced Image", string.Empty, "ray-traced-image", "png");
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }

                try
                {
                    manager.ExportCurrentRenderPng(path);
                    Debug.Log($"Saved ray-traced image to '{path}'.", manager);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, manager);
                    EditorUtility.DisplayDialog("Save Image Failed", exception.Message, "OK");
                }
            }
        }

        if (!EditorApplication.isPlaying)
        {
            EditorGUILayout.HelpBox("Enter Play mode to save the current ray-traced render.", MessageType.Info);
        }
    }

    private static string FormatDuration(double totalSeconds)
    {
        if (totalSeconds < 60.0)
        {
            return $"{totalSeconds:0.0} seconds";
        }

        TimeSpan duration = TimeSpan.FromSeconds(totalSeconds);
        return duration.TotalHours >= 1.0
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }

    private static BakeStatus GetBakeStatus(RayTracingBvhBakeAsset bake, string signature)
    {
        if (bake == null)
        {
            return BakeStatus.NotBaked;
        }

        return bake.formatVersion == GameManager.BvhBakeFormatVersion
            && bake.sceneSignature == signature
            && RayTracingBvhBakeUtility.IsBakeBinaryUsable(bake)
            ? BakeStatus.Baked
            : BakeStatus.OutOfDate;
    }

    private static string GetStatusLabel(BakeStatus status)
    {
        switch (status)
        {
            case BakeStatus.Baked: return "Baked";
            case BakeStatus.OutOfDate: return "Bake is out-of-date";
            default: return "Not baked";
        }
    }
}

public static class RayTracingBvhBakeUtility
{
    private const string BakeFolder = "Assets/Generated/RayTracingBvhBakes";
    private const string StreamingBakeFolder = "Assets/StreamingAssets/RayTracingBvhBakes";

    public static List<RayTracingBvhBakeAsset.MeshEntry> GetMeshEntries(GameManager manager)
    {
        var entriesByKey = new Dictionary<string, RayTracingBvhBakeAsset.MeshEntry>();
        foreach (PathTracingObject rayObject in manager.GetComponentsInChildren<PathTracingObject>(true))
        {
            if (!rayObject.isActiveAndEnabled)
            {
                continue;
            }

            var filter = rayObject.GetComponent<MeshFilter>();
            var material = rayObject.GetComponent<RayMaterial>();
            var light = rayObject.GetComponent<RayLight>();
            if (filter == null || filter.sharedMesh == null || (material == null && light == null))
            {
                continue;
            }

            bool interpolateNormals = material != null && material.InterpolateNormals;
            string identity = GetMeshIdentity(filter.sharedMesh);
            string key = identity + (interpolateNormals ? ":smooth" : ":flat");
            entriesByKey[key] = new RayTracingBvhBakeAsset.MeshEntry
            {
                mesh = filter.sharedMesh,
                meshIdentity = identity,
                interpolateNormals = interpolateNormals,
                dependencyHash = GetMeshDependencyHash(filter.sharedMesh),
                vertexCount = filter.sharedMesh.vertexCount,
                indexCount = GetMeshIndexCount(filter.sharedMesh)
            };
        }

        var keys = new List<string>(entriesByKey.Keys);
        keys.Sort(StringComparer.Ordinal);
        var entries = new List<RayTracingBvhBakeAsset.MeshEntry>(keys.Count);
        foreach (string key in keys)
        {
            entries.Add(entriesByKey[key]);
        }
        return entries;
    }

    public static string CalculateSignature(List<RayTracingBvhBakeAsset.MeshEntry> entries)
    {
        var source = new StringBuilder(entries.Count * 80);
        source.Append(GameManager.BvhBakeFormatVersion).Append('|');
        foreach (var entry in entries)
        {
            source.Append(entry.meshIdentity).Append('|');
            source.Append(entry.interpolateNormals ? '1' : '0').Append('|');
            source.Append(entry.dependencyHash);
            source.Append(';');
        }

        using (var hash = SHA256.Create())
        {
            byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(source.ToString()));
            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }
    }

    public static bool IsBakeCurrent(GameManager manager)
    {
        var entries = GetMeshEntries(manager);
        return IsBakeCurrent(manager.EditorBvhBake, entries);
    }

    public static bool IsBakeCurrent(RayTracingBvhBakeAsset bake, List<RayTracingBvhBakeAsset.MeshEntry> entries)
    {
        if (bake == null)
        {
            return false;
        }
        return bake.formatVersion == GameManager.BvhBakeFormatVersion
            && bake.sceneSignature == CalculateSignature(entries)
            && IsBakeBinaryUsable(bake);
    }

    public static void Bake(GameManager manager, List<RayTracingBvhBakeAsset.MeshEntry> entries = null, string signature = null)
    {
        if (string.IsNullOrEmpty(manager.gameObject.scene.path))
        {
            Debug.LogError("Save the scene before baking its ray tracing BVH.", manager);
            return;
        }

        entries = entries ?? GetMeshEntries(manager);
        signature = signature ?? CalculateSignature(entries);
        try
        {
            for (int i = 0; i < entries.Count; i++)
            {
                EditorUtility.DisplayProgressBar("Baking ray tracing BVH", entries[i].mesh.name, entries.Count == 0 ? 1.0f : i / (float)entries.Count);
                manager.EditorBuildMeshBvhTemplate(entries[i].mesh, entries[i].interpolateNormals);
                manager.EditorGetMeshBvhTemplateCounts(entries[i].mesh, entries[i].interpolateNormals, out int triangleCount, out int nodeCount);
                var entry = entries[i];
                entry.triangleCount = triangleCount;
                entry.nodeCount = nodeCount;
                entries[i] = entry;
            }

            string assetPath = SaveBuiltTemplates(manager, entries, signature);
            var bake = AssetDatabase.LoadAssetAtPath<RayTracingBvhBakeAsset>(assetPath);
            AssignBake(manager, bake, "Assign baked ray tracing BVH");
            Debug.Log($"Baked {entries.Count:N0} ray tracing mesh BVHs.", manager);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    public static string SaveBuiltTemplates(GameManager manager, List<RayTracingBvhBakeAsset.MeshEntry> entries, string signature)
    {
        EnsureFolder(BakeFolder);
        EnsureFolder(StreamingBakeFolder);
        string sceneGuid = AssetDatabase.AssetPathToGUID(manager.gameObject.scene.path);
        string managerId = GlobalObjectId.GetGlobalObjectIdSlow(manager).targetObjectId.ToString();
        string fileStem = $"{sceneGuid}_{managerId}";
        string assetPath = $"{BakeFolder}/{fileStem}.asset";
        string binaryAssetPath = $"{StreamingBakeFolder}/{fileStem}.bytes";
        var bake = AssetDatabase.LoadAssetAtPath<RayTracingBvhBakeAsset>(assetPath);
        if (bake == null)
        {
            bake = ScriptableObject.CreateInstance<RayTracingBvhBakeAsset>();
            AssetDatabase.CreateAsset(bake, assetPath);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            manager.EditorGetMeshBvhTemplateCounts(entries[i].mesh, entries[i].interpolateNormals, out int triangleCount, out int nodeCount);
            var entry = entries[i];
            entry.triangleCount = triangleCount;
            entry.nodeCount = nodeCount;
            entries[i] = entry;
        }

        bake.formatVersion = GameManager.BvhBakeFormatVersion;
        bake.sceneSignature = signature;
        bake.streamingAssetsRelativePath = $"RayTracingBvhBakes/{fileStem}.bytes";
        bake.meshes = entries;
        manager.EditorWriteMeshBvhBake(Path.GetFullPath(binaryAssetPath), bake);
        EditorUtility.SetDirty(bake);
        AssetDatabase.ImportAsset(binaryAssetPath, ImportAssetOptions.ForceUpdate);
        AssetDatabase.SaveAssets();
        DeleteStaleSceneBakes(manager, sceneGuid, assetPath, binaryAssetPath);
        return assetPath;
    }

    private static void DeleteStaleSceneBakes(
        GameManager bakedManager,
        string sceneGuid,
        string currentAssetPath,
        string currentBinaryAssetPath)
    {
        string sceneBakePrefix = sceneGuid + "_";
        var preservedPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            currentAssetPath,
            currentBinaryAssetPath
        };

        foreach (GameObject root in bakedManager.gameObject.scene.GetRootGameObjects())
        {
            foreach (GameManager manager in root.GetComponentsInChildren<GameManager>(true))
            {
                if (manager == bakedManager || manager.EditorBvhBake == null)
                {
                    continue;
                }

                string referencedAssetPath = AssetDatabase.GetAssetPath(manager.EditorBvhBake);
                if (!string.IsNullOrEmpty(referencedAssetPath))
                {
                    preservedPaths.Add(referencedAssetPath);
                }
                if (!string.IsNullOrEmpty(manager.EditorBvhBake.streamingAssetsRelativePath))
                {
                    preservedPaths.Add("Assets/StreamingAssets/" + manager.EditorBvhBake.streamingAssetsRelativePath);
                }
            }
        }

        int deletedCount = DeleteStaleBakeAssets(BakeFolder, ".asset", sceneBakePrefix, preservedPaths);
        deletedCount += DeleteStaleBakeAssets(StreamingBakeFolder, ".bytes", sceneBakePrefix, preservedPaths);
        if (deletedCount > 0)
        {
            Debug.Log($"Deleted {deletedCount:N0} stale ray tracing BVH bake files for scene '{bakedManager.gameObject.scene.name}'.", bakedManager);
        }
    }

    private static int DeleteStaleBakeAssets(
        string folder,
        string extension,
        string sceneBakePrefix,
        HashSet<string> preservedPaths)
    {
        int deletedCount = 0;
        foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { folder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string fileName = Path.GetFileName(path);
            if (preservedPaths.Contains(path)
                || !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                || !fileName.StartsWith(sceneBakePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (AssetDatabase.DeleteAsset(path))
            {
                deletedCount++;
            }
        }
        return deletedCount;
    }

    public static void AssignBake(GameManager manager, RayTracingBvhBakeAsset bake, string undoName)
    {
        Undo.RecordObject(manager, undoName);
        var serializedManager = new SerializedObject(manager);
        serializedManager.FindProperty("bvhBake").objectReferenceValue = bake;
        serializedManager.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(manager);
        EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
        EditorSceneManager.SaveScene(manager.gameObject.scene);
    }

    public static bool IsBakeBinaryUsable(RayTracingBvhBakeAsset bake)
    {
        if (bake == null || string.IsNullOrEmpty(bake.streamingAssetsRelativePath))
        {
            return false;
        }

        string path = Path.Combine(Application.streamingAssetsPath, bake.streamingAssetsRelativePath);
        if (!File.Exists(path))
        {
            return false;
        }

        long expectedLength = 12L;
        foreach (var entry in bake.meshes)
        {
            expectedLength += 8L + entry.triangleCount * 160L + entry.nodeCount * 40L;
        }
        return new FileInfo(path).Length == expectedLength;
    }

    private static string GetMeshIdentity(Mesh mesh)
    {
        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long localId))
        {
            return guid + ":" + localId;
        }
        return "scene:" + mesh.name + ":" + mesh.vertexCount + ":" + GetMeshIndexCount(mesh);
    }

    private static string GetMeshDependencyHash(Mesh mesh)
    {
        string path = AssetDatabase.GetAssetPath(mesh);
        return string.IsNullOrEmpty(path)
            ? $"scene:{mesh.vertexCount}:{GetMeshIndexCount(mesh)}"
            : AssetDatabase.GetAssetDependencyHash(path).ToString();
    }

    private static int GetMeshIndexCount(Mesh mesh)
    {
        int count = 0;
        for (int i = 0; i < mesh.subMeshCount; i++)
        {
            count += checked((int)mesh.GetIndexCount(i));
        }
        return count;
    }

    private static void EnsureFolder(string path)
    {
        string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        string name = Path.GetFileName(path);
        if (!AssetDatabase.IsValidFolder(path))
        {
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
