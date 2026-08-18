using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public sealed class RayTracingSceneGalleryWindow : EditorWindow
{
    private const string WindowTitle = "Ray Tracing Gallery";
    private const string GeneratedSceneFolder = "Assets/Scenes/Generated/";
    private const string ThumbnailFolder = "Assets/Editor/RayTracingSceneGalleryThumbnails/Current/";

    private static readonly GalleryEntry[] ShowcaseScenes =
    {
        new ("Getting Started", "GettingStarted", "A compact room for evaluating diffuse, metal, and glass materials.", "Low", null),
        new ("Glass", "Glass", "Reflection, refraction, and transparent-material absorption.", "Medium", null),
        new ("Cornell Box", "CornellBox", "Enclosed indirect lighting and recursive reflections.", "Medium", null),
        new ("Caustics", "Caustics", "Photon-mapped caustics through refractive objects.", "High", null),
        new ("Parallax Mapping", "ParallaxMapping", "Material parallax, normal maps, and textured surfaces.", "High", null),
        new ("Teapot Materials", "TeapotMaterials", "A ray-traced mesh with a range of material responses.", "High", null),
        new ("Water", "Water", "Animated water reflection, refraction (with caustics), and absorption.", "High", null),
        new ("Volumetric Fog", "VolumetricFog", "Homogeneous volumetric fog.", "High", null),
        new ("Khronos glTF Browser", "KhronosGltfBrowser", "Imports and browses Khronos glTF assets.", "Variable", "Requires network access."),
        new ("Terrain", "Terrain", "Path-traced Unity terrain with layered materials.", "High", null)
    };

    private static readonly GalleryEntry[] StressScenes =
    {
        new ("Many Spheres", "ManySpheres", "Sphere-count stress and benchmark workload.", "Stress test", null),
        new ("Many Meshes", "ManyMeshes", "Mesh-count stress and benchmark workload.", "Stress test", null),
        new ("Many Lights", "ManyLights", "Light-count stress and benchmark workload.", "Stress test", null),
        new ("Environment Mapping", "Environment_Mapping", "69k-triangle Stanford bunny lit only by environment mapping.", "Low", null),
    };

    private static readonly GalleryEntry[] TestScenes =
    {
        new ("Demofox Glossy Reflections", "DemofoxGlossyReflections", "Glossy metal reflections across a smoothness range.", "Medium", null),
        new ("Demofox Refraction Index", "DemofoxRefractionIndex", "Glass refraction distortion across increasing IOR values.", "Medium", null),
        new ("Demofox Rough Refraction", "DemofoxRoughRefraction", "Frosted glass refraction across a smoothness range.", "Medium", null),
        new ("Demofox Absorption", "DemofoxAbsorption", "Distance-based RGB glass absorption comparisons.", "Medium", null)
    };

    private Vector2 _scrollPosition;

    [MenuItem("Window/Ray Tracing/Scene Gallery")]
    public static void Open()
    {
        GetWindow<RayTracingSceneGalleryWindow>(
            typeof(RayTracingQuickControlsWindow),
            typeof(EditorWindow).Assembly.GetType("UnityEditor.InspectorWindow"));
    }

    private void OnEnable()
    {
        titleContent = new GUIContent(WindowTitle);
        minSize = new Vector2(300.0f, 360.0f);
    }

    public static string[] GetThumbnailScenePaths()
    {
        var paths = new string[ShowcaseScenes.Length + TestScenes.Length + StressScenes.Length];
        int index = 0;
        foreach (GalleryEntry entry in ShowcaseScenes)
        {
            paths[index++] = entry.Path;
        }
        foreach (GalleryEntry entry in TestScenes)
        {
            paths[index++] = entry.Path;
        }
        foreach (GalleryEntry entry in StressScenes)
        {
            paths[index++] = entry.Path;
        }
        return paths;
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Ray Tracing Scene Gallery", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Choose a showcase scene to explore the renderer, or use the separate stress section for performance workloads.", MessageType.Info);

        _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
        DrawSection("Start Here And Showcases", ShowcaseScenes);
        EditorGUILayout.Space(10.0f);
        DrawSection("Test Scenes", TestScenes);
        EditorGUILayout.Space(10.0f);
        DrawSection("Benchmarks And Stress Fixtures", StressScenes);
        EditorGUILayout.EndScrollView();
    }

    private static void DrawSection(string title, GalleryEntry[] entries)
    {
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        foreach (GalleryEntry entry in entries)
        {
            DrawEntry(entry);
        }
    }

    private static void DrawEntry(GalleryEntry entry)
    {
        SceneAsset scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(entry.Path);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawPreview(entry, scene);
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField(entry.Name, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(entry.Description, EditorStyles.wordWrappedLabel);
                    EditorGUILayout.LabelField("Expected cost: " + entry.Cost, EditorStyles.miniLabel);
                    if (!string.IsNullOrEmpty(entry.Requirement))
                    {
                        EditorGUILayout.LabelField(entry.Requirement, EditorStyles.miniLabel);
                    }
                }
            }

            using (new EditorGUI.DisabledScope(scene == null))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open Scene"))
                {
                    Schedule(() => OpenScene(entry.Path, false));
                }
                if (GUILayout.Button("Open And Play"))
                {
                    Schedule(() => OpenScene(entry.Path, true));
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(scene == null ? "Generate Scene" : "Regenerate Scene"))
                {
                    Schedule(() => GenerateScene(entry, scene != null));
                }
                using (new EditorGUI.DisabledScope(scene == null))
                {
                    if (GUILayout.Button("Regenerate Thumbnail"))
                    {
                        Schedule(() => RayTracingSceneCapture.CaptureGalleryThumbnail(entry.Path));
                    }
                }
            }

            if (scene == null)
            {
                EditorGUILayout.HelpBox("Scene asset is missing. Generate project scenes from Tools > Ray Tracing > Generate Scenes.", MessageType.Warning);
            }
        }
        }

    private static void Schedule(System.Action action)
    {
        EditorApplication.delayCall += () => action();
    }

    private static void DrawPreview(GalleryEntry entry, SceneAsset scene)
    {
        Texture preview = AssetDatabase.LoadAssetAtPath<Texture2D>(ThumbnailFolder + entry.SceneName + ".png");
        if (preview == null && scene != null)
        {
            preview = AssetPreview.GetAssetPreview(scene);
        }
        if (preview == null && scene != null)
        {
            preview = AssetPreview.GetMiniThumbnail(scene);
        }
        GUIContent content = preview == null ? EditorGUIUtility.IconContent("SceneAsset Icon") : new GUIContent(preview);
        GUILayout.Label(content, GUILayout.Width(72.0f), GUILayout.Height(54.0f));
    }

    private static void OpenScene(string path, bool playAfterOpening)
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            return;
        }

        EditorSceneManager.OpenScene(path);
        if (playAfterOpening)
        {
            EditorApplication.delayCall += EnterPlayMode;
        }
    }

    private static void GenerateScene(GalleryEntry entry, bool sceneExists)
    {
        if (sceneExists && !EditorUtility.DisplayDialog(
                "Regenerate Scene",
                $"Regenerate '{entry.Name}' from the scene generator?\n\nHand edits to this generated scene will be lost.",
                "Regenerate",
                "Cancel"))
        {
            return;
        }

        RayTracingSceneGenerator.GenerateScenes(new[] { entry.Path }, true);
        AssetDatabase.Refresh();
    }

    private static void EnterPlayMode()
    {
        if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.isPlaying = true;
        }
    }

    private readonly struct GalleryEntry
    {
        public readonly string Name;
        public readonly string Description;
        public readonly string Cost;
        public readonly string Requirement;
        public readonly string Path;
        public readonly string SceneName;

        public GalleryEntry(string name, string sceneName, string description, string cost, string requirement)
        {
            Name = name;
            Description = description;
            Cost = cost;
            Requirement = requirement;
            SceneName = sceneName;
            Path = GeneratedSceneFolder + sceneName + ".unity";
        }
    }
}
