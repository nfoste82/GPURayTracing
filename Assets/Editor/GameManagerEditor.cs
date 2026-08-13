using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PathTracing.Camera;
using PathTracing.AccelerationStructures;
using PathTracing.Lighting;
using UnityEditor;
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
            DrawLightingProperty("lightSamplingStrategy", "Light Sampling Strategy");
            if (serializedObject.FindProperty("_lightingManager._lightSamplingStrategy").enumValueIndex != (int)LightSamplingStrategy.AllLights)
            {
                DrawLightingProperty("lightSampleCount", "Light Sample Count");
            }
            DrawLightingProperty("lightFalloffScale", "Local Light Falloff Scale");
            DrawDirectionalLighting(manager);
        });
        DrawSection(manager, "Image and Environment", true, () =>
        {
            DrawProperty("exposure");
            DrawLightingProperty("skyboxLightColor", "Skybox Light Color");
            DrawProperty("skyboxTexture");
        });
        DrawSection(manager, "Camera", true, () => DrawCameraSettings(manager));
        DrawSection(manager, "Denoising", false, DrawDenoising);
        DrawSection(manager, "Volumetric Fog", false, DrawVolumetricFog);
        DrawSection(manager, "Water", false, () => DrawWater(manager));
        DrawSection(manager, "Caustics", false, DrawCaustics);
        DrawSection(manager, "Terrain", true, () => DrawTerrain(manager));
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
        CameraManager cameraManager = manager.GetComponent<CameraManager>();
        if (cameraManager == null)
        {
            EditorGUILayout.HelpBox("CameraManager is required on the GameManager.", MessageType.Error);
            return;
        }
        var cameraObject = new SerializedObject(cameraManager);
        cameraObject.Update();
        if (cameraManager.renderTextureCamera == null && manager.renderTextureCamera != null)
        {
            cameraObject.FindProperty("renderTextureCamera").objectReferenceValue = manager.renderTextureCamera;
        }
        EditorGUILayout.LabelField("Camera Controls", EditorStyles.boldLabel);
        DrawCameraProperty(cameraObject, "cameraBehavior");
        DrawCameraProperty(cameraObject, "cameraMovementSpeed");
        if ((CameraBehavior)cameraObject.FindProperty("cameraBehavior").enumValueIndex == CameraBehavior.OrbitFocusPoint)
        {
            DrawCameraProperty(cameraObject, "cameraFocusPosition");
            DrawCameraProperty(cameraObject, "cameraOrbitZoom");
        }
        if (cameraManager.renderTextureCamera != null)
        {
            var unityCameraObject = new SerializedObject(cameraManager.renderTextureCamera);
            unityCameraObject.Update();
            EditorGUILayout.PropertyField(unityCameraObject.FindProperty("field of view"), new GUIContent("Field Of View"));
            unityCameraObject.ApplyModifiedProperties();
        }
        else
        {
            EditorGUILayout.HelpBox("Assign a render texture camera to edit its field of view here.", MessageType.Info);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Focus and Lens", EditorStyles.boldLabel);
        DrawCameraProperty(cameraObject, "cameraAutoFocus");
        DrawCameraProperty(cameraObject, "enableClickToFocus");
        using (new EditorGUI.DisabledScope(!cameraObject.FindProperty("enableClickToFocus").boolValue))
        {
            DrawCameraProperty(cameraObject, "trackClickedFocusPoint");
        }
        DrawCameraProperty(cameraObject, "autoFocusTransparentOpacityThreshold");
        DrawCameraProperty(cameraObject, "cameraFocalDistance");
        DrawCameraProperty(cameraObject, "cameraApertureMode");

        CameraApertureMode apertureMode = (CameraApertureMode)cameraObject
            .FindProperty("cameraApertureMode").enumValueIndex;
        if (apertureMode == CameraApertureMode.LensRadius)
        {
            DrawCameraProperty(cameraObject, "cameraApertureRadius");
        }
        else if (apertureMode == CameraApertureMode.FStop)
        {
            DrawCameraProperty(cameraObject, "cameraFStop");
            DrawCameraProperty(cameraObject, "cameraApertureScale");
        }

        DrawCameraProperty(cameraObject, "cameraApertureBladeCount");
        if (cameraObject.FindProperty("cameraApertureBladeCount").intValue >= 3)
        {
            DrawCameraProperty(cameraObject, "cameraApertureBladeRotation");
        }
        DrawCameraProperty(cameraObject, "cameraAnamorphicRatio");
        cameraObject.ApplyModifiedProperties();
    }

    private static void DrawCameraProperty(SerializedObject cameraObject, string propertyPath)
    {
        EditorGUILayout.PropertyField(cameraObject.FindProperty(propertyPath));
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
        SerializedProperty spatialManager = serializedObject.FindProperty("_spatialDenoisingManager");
        EditorGUILayout.PropertyField(spatialManager.FindPropertyRelative("enabled"), new GUIContent("Enable Spatial Denoising"));
        using (new EditorGUI.DisabledScope(!spatialManager.FindPropertyRelative("enabled").boolValue))
        {
            EditorGUILayout.PropertyField(spatialManager.FindPropertyRelative("iterations"));
            EditorGUILayout.PropertyField(spatialManager.FindPropertyRelative("depthSigma"));
            EditorGUILayout.PropertyField(spatialManager.FindPropertyRelative("normalPower"));
            EditorGUILayout.PropertyField(spatialManager.FindPropertyRelative("albedoSigma"));
            EditorGUILayout.PropertyField(spatialManager.FindPropertyRelative("luminanceSigma"));
        }

        EditorGUILayout.Space();
        SerializedProperty temporalManager = serializedObject.FindProperty("_temporalDenoisingManager");
        EditorGUILayout.PropertyField(temporalManager.FindPropertyRelative("enabled"), new GUIContent("Enable Temporal Denoising"));
        using (new EditorGUI.DisabledScope(!temporalManager.FindPropertyRelative("enabled").boolValue))
        {
            EditorGUILayout.PropertyField(temporalManager.FindPropertyRelative("temporalMaxHistoryLength"));
            EditorGUILayout.PropertyField(temporalManager.FindPropertyRelative("temporalMotionDistance"));
            EditorGUILayout.PropertyField(temporalManager.FindPropertyRelative("temporalMotionAngle"));
            EditorGUILayout.PropertyField(temporalManager.FindPropertyRelative("temporalDepthThreshold"));
            EditorGUILayout.PropertyField(temporalManager.FindPropertyRelative("temporalNormalThreshold"));
            EditorGUILayout.PropertyField(temporalManager.FindPropertyRelative("temporalCameraCutDistance"));
            EditorGUILayout.PropertyField(temporalManager.FindPropertyRelative("temporalCameraCutAngle"));
            EditorGUILayout.PropertyField(temporalManager.FindPropertyRelative("temporalVarianceGuidedFiltering"));
        }

        if (spatialManager.FindPropertyRelative("enabled").boolValue
            || temporalManager.FindPropertyRelative("enabled").boolValue)
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

    private static void DrawWater(GameManager manager)
    {
        Water water = manager.WaterInternal;
        if (water == null)
        {
            EditorGUILayout.HelpBox("Add a Water component beneath this GameManager to configure water rendering.", MessageType.Info);
            return;
        }

        var waterObject = new SerializedObject(water);
        waterObject.Update();
        EditorGUILayout.PropertyField(waterObject.FindProperty("Color"));
        EditorGUILayout.PropertyField(waterObject.FindProperty("Smoothness"));
        EditorGUILayout.PropertyField(waterObject.FindProperty("Opacity"));
        EditorGUILayout.PropertyField(waterObject.FindProperty("AbsorptionStrength"));
        EditorGUILayout.PropertyField(waterObject.FindProperty("RefractionIndex"));
        EditorGUILayout.PropertyField(waterObject.FindProperty("WaveAmplitude"));
        EditorGUILayout.PropertyField(waterObject.FindProperty("WaveScale"));
        EditorGUILayout.PropertyField(waterObject.FindProperty("WaveSpeed"));
        EditorGUILayout.PropertyField(waterObject.FindProperty("MarchSteps"));
        EditorGUILayout.PropertyField(waterObject.FindProperty("RefinementSteps"));
        waterObject.ApplyModifiedProperties();
    }

    private void DrawCaustics()
    {
        DrawProperty("enableCaustics");
        SerializedProperty caustics = serializedObject.FindProperty("_causticsManager");
        using (new EditorGUI.DisabledScope(!serializedObject.FindProperty("enableCaustics").boolValue))
        {
            EditorGUILayout.PropertyField(caustics.FindPropertyRelative("_photonCount"), new GUIContent("Caustic Photon Count"));
            EditorGUILayout.PropertyField(caustics.FindPropertyRelative("_gatherRadius"));
            EditorGUILayout.PropertyField(caustics.FindPropertyRelative("_intensity"));
        }
    }

    private static void DrawTerrain(GameManager manager)
    {
        RayTracingTerrain terrain = manager.GetComponentInChildren<RayTracingTerrain>(true);
        if (terrain == null)
        {
            EditorGUILayout.HelpBox("Add a RayTracingTerrain beneath this GameManager to configure terrain rendering.", MessageType.Info);
            return;
        }

        var terrainObject = new SerializedObject(terrain);
        terrainObject.Update();
        EditorGUILayout.PropertyField(terrainObject.FindProperty("Terrain"));
        EditorGUILayout.PropertyField(terrainObject.FindProperty("AccelerationResolution"));
        EditorGUILayout.PropertyField(terrainObject.FindProperty("MarchSteps"));
        EditorGUILayout.PropertyField(terrainObject.FindProperty("RefinementSteps"));
        EditorGUILayout.PropertyField(terrainObject.FindProperty("Seed"));
        terrainObject.ApplyModifiedProperties();
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
        if (property == null)
        {
            EditorGUILayout.HelpBox($"Serialized property '{propertyPath}' could not be found on GameManager.", MessageType.Warning);
            return;
        }

        EditorGUILayout.PropertyField(property, label == null ? null : new GUIContent(label));
    }

    private void DrawLightingProperty(string propertyName, string label = null)
    {
        SerializedProperty property = serializedObject.FindProperty("_lightingManager._" + propertyName);
        if (property == null)
        {
            EditorGUILayout.HelpBox($"Serialized lighting property '{propertyName}' could not be found on GameManager.", MessageType.Warning);
            return;
        }

        EditorGUILayout.PropertyField(property, label == null ? null : new GUIContent(label));
    }

    private static string GetManagerIdentifier(GameManager manager)
    {
        return GlobalObjectId.GetGlobalObjectIdSlow(manager).ToString();
    }

    private void DrawVideoCaptureSettings(GameManager manager)
    {
        SerializedProperty videoCapture = serializedObject.FindProperty("_videoCaptureManager");
        EditorGUILayout.PropertyField(
            videoCapture.FindPropertyRelative("samplesPerFrame"),
            new GUIContent("Quality Samples Per Output Frame"));
        EditorGUILayout.PropertyField(
            videoCapture.FindPropertyRelative("frameTimeStep"),
            new GUIContent("Output Frame Time Step (seconds)"));
        EditorGUILayout.PropertyField(
            videoCapture.FindPropertyRelative("duration"),
            new GUIContent("Video Duration (seconds)"));
        EditorGUILayout.PropertyField(videoCapture.FindPropertyRelative("outputFolder"));
        EditorGUILayout.PropertyField(videoCapture.FindPropertyRelative("encodeMp4"));
        if (videoCapture.FindPropertyRelative("encodeMp4").boolValue)
        {
            EditorGUILayout.PropertyField(videoCapture.FindPropertyRelative("ffmpegPath"));
        }

        int frameCount = VideoCaptureManager.CalculateFrameCount(manager.VideoCapture.duration, manager.VideoCapture.frameTimeStep);
        var overlay = manager.GetComponent<RayTracingBenchmarkOverlay>();
        float averageFrameMs = overlay != null ? overlay.AverageFrameMs : 0.0f;
        double estimateSeconds = VideoCaptureManager.EstimateCaptureSeconds(
            frameCount,
            manager.VideoCapture.samplesPerFrame,
            Mathf.Max(1, manager.numberOfPasses),
            averageFrameMs,
            manager.enableCaustics);
        string estimate = estimateSeconds > 0.0
            ? FormatDuration(estimateSeconds)
            : "available after frame statistics have been collected in Play mode";
        EditorGUILayout.HelpBox(
            $"Output frames: {frameCount:N0} ({manager.VideoCapture.duration:0.###} seconds / {manager.VideoCapture.frameTimeStep:0.######}-second timestep)\n" +
            $"Quality: {manager.VideoCapture.samplesPerFrame:N0} samples accumulated into each output frame\n" +
            $"Estimated render time: {estimate}\n" +
            "Changing quality samples does not change the output frame count. Lossless PNG frames are retained; ffmpeg creates video.mp4 after capture. Encoding and disk-write time are not included in the estimate.",
            MessageType.Info);

        if (manager.VideoCapture.IsEncodingActive)
        {
            EditorGUILayout.HelpBox($"Encoding MP4 with ffmpeg...\n{manager.VideoCapture.OutputPath}", MessageType.Info);
            Repaint();
        }
        else if (manager.VideoCapture.IsActive)
        {
            float progress = manager.VideoCapture.FrameCount > 0
                ? manager.VideoCapture.CompletedFrameCount / (float)manager.VideoCapture.FrameCount
                : 0.0f;
            EditorGUI.ProgressBar(
                EditorGUILayout.GetControlRect(false, 20.0f),
                progress,
                $"{manager.VideoCapture.CompletedFrameCount:N0} / {manager.VideoCapture.FrameCount:N0} frames");
            EditorGUILayout.LabelField("Output", manager.VideoCapture.DirectoryPath ?? string.Empty);
            if (GUILayout.Button("Cancel Capture"))
            {
                manager.VideoCapture.Cancel();
            }
            Repaint();
        }
        else
        {
            using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying || frameCount <= 0 || manager.VideoCapture.IsEncodingActive))
            {
                if (GUILayout.Button("Start Video Capture"))
                {
                    serializedObject.ApplyModifiedProperties();
                    manager.VideoCapture.Start();
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

        return bake.formatVersion == SceneBvhManager.BakeFormatVersion
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
        source.Append(SceneBvhManager.BakeFormatVersion).Append('|');
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
        return IsBakeCurrent(manager, entries);
    }

    public static bool IsBakeCurrent(GameManager manager, List<RayTracingBvhBakeAsset.MeshEntry> entries)
    {
        return IsBakeCurrent(FindBake(manager), entries);
    }

    public static bool IsBakeCurrent(RayTracingBvhBakeAsset bake, List<RayTracingBvhBakeAsset.MeshEntry> entries)
    {
        if (bake == null)
        {
            return false;
        }
        return bake.formatVersion == SceneBvhManager.BakeFormatVersion
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

            SaveBuiltTemplates(manager, entries, signature);
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

        bake.formatVersion = SceneBvhManager.BakeFormatVersion;
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

    public static RayTracingBvhBakeAsset FindBake(GameManager manager)
    {
        if (string.IsNullOrEmpty(manager.gameObject.scene.path))
        {
            return null;
        }

        string sceneGuid = AssetDatabase.AssetPathToGUID(manager.gameObject.scene.path);
        string managerId = GlobalObjectId.GetGlobalObjectIdSlow(manager).targetObjectId.ToString();
        string assetPath = $"{BakeFolder}/{sceneGuid}_{managerId}.asset";
        return AssetDatabase.LoadAssetAtPath<RayTracingBvhBakeAsset>(assetPath);
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
                if (manager == bakedManager)
                {
                    continue;
                }

                RayTracingBvhBakeAsset bake = FindBake(manager);
                if (bake == null)
                {
                    continue;
                }

                string referencedAssetPath = AssetDatabase.GetAssetPath(bake);
                if (!string.IsNullOrEmpty(referencedAssetPath))
                {
                    preservedPaths.Add(referencedAssetPath);
                }
                if (!string.IsNullOrEmpty(bake.streamingAssetsRelativePath))
                {
                    preservedPaths.Add("Assets/StreamingAssets/" + bake.streamingAssetsRelativePath);
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
