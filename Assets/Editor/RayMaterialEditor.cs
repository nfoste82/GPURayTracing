using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[CustomEditor(typeof(RayMaterial))]
public sealed class RayMaterialEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawControls((RayMaterial)target);
    }

    public static void DrawControls(RayMaterial material)
    {
        var serializedMaterial = new SerializedObject(material);
        serializedMaterial.Update();

        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("Type"));
        bool materialTypeChanged = EditorGUI.EndChangeCheck();
        if (materialTypeChanged)
        {
            serializedMaterial.ApplyModifiedProperties();
            if (material.Type != RayMaterial.MaterialType.Emissive)
            {
                RemoveEmissionComponents(material);
            }
            serializedMaterial.Update();
        }
        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("Color"));

        if (material.Type == RayMaterial.MaterialType.Emissive)
        {
            serializedMaterial.ApplyModifiedProperties();
            RayLight light = EnsureEmissionComponents(material);
            DrawEmissionIntensity(light);
            return;
        }

        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("AlbedoTexture"));
        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("Metallic"));
        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("MetallicRoughnessTexture"));
        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("NormalTexture"));
        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("NormalStrength"));
        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("ParallaxTexture"));
        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("TextureUvScale"));
        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("TextureUvRotation"));
        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("ParallaxStrength"));
        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("MinimumParallaxStrength"));
        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("InterpolateNormals"));
        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("Smoothness"));
        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("Opacity"));
        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("Specular"));
        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("Transmission"));
        EditorGUILayout.PropertyField(serializedMaterial.FindProperty("RefractionIndex"));
        serializedMaterial.ApplyModifiedProperties();
    }

    private static RayLight EnsureEmissionComponents(RayMaterial material)
    {
        GameObject gameObject = material.gameObject;
        RayLight light = gameObject.GetComponent<RayLight>();
        if (light == null)
        {
            light = Undo.AddComponent<RayLight>(gameObject);
            light.Intensity = 1.0f;
        }

        Undo.RecordObject(light, "Sync Emissive Ray Light");
        light.Color = material.Color;
        EditorUtility.SetDirty(light);

        Light unityLight = gameObject.GetComponent<Light>();
        if (unityLight == null)
        {
            unityLight = Undo.AddComponent<Light>(gameObject);
        }
        Undo.RecordObject(unityLight, "Sync Emissive Preview Light");
        unityLight.type = LightType.Point;
        unityLight.color = light.Color;
        unityLight.intensity = Mathf.Max(0.0f, light.Intensity);
        EditorUtility.SetDirty(unityLight);
        MarkSceneDirty(gameObject);
        return light;
    }

    private static void DrawEmissionIntensity(RayLight light)
    {
        EditorGUILayout.Space(4.0f);
        EditorGUILayout.LabelField("Emission", EditorStyles.boldLabel);
        var serializedLight = new SerializedObject(light);
        serializedLight.Update();
        EditorGUILayout.PropertyField(serializedLight.FindProperty("Intensity"));
        if (serializedLight.ApplyModifiedProperties())
        {
            Light unityLight = light.GetComponent<Light>();
            if (unityLight != null)
            {
                Undo.RecordObject(unityLight, "Sync Emissive Preview Light");
                unityLight.intensity = Mathf.Max(0.0f, light.Intensity);
                EditorUtility.SetDirty(unityLight);
            }
            MarkSceneDirty(light.gameObject);
        }
    }

    private static void RemoveEmissionComponents(RayMaterial material)
    {
        RayLight light = material.GetComponent<RayLight>();
        if (light != null)
        {
            Undo.DestroyObjectImmediate(light);
        }

        Light unityLight = material.GetComponent<Light>();
        if (unityLight != null)
        {
            Undo.DestroyObjectImmediate(unityLight);
        }
        MarkSceneDirty(material.gameObject);
    }

    private static void MarkSceneDirty(GameObject gameObject)
    {
        if (!Application.isPlaying)
        {
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
        }
    }
}
