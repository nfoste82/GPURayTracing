using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(RemoteGltfRayTracingAsset))]
public class MaterialBallSceneController : MonoBehaviour
{
    private const string MaterialSurfaceName = "material_surface";
    private const string CameraNodeName = "camera";
    private const float LumensPerWatt = 683.0f;
    private const float AreaLightRadianceScale = 0.00684f;

    private RemoteGltfRayTracingAsset _remoteAsset;

    private void Awake()
    {
        _remoteAsset = GetComponent<RemoteGltfRayTracingAsset>();
        _remoteAsset.Loaded += ConfigureScene;
    }

    private void OnDestroy()
    {
        if (_remoteAsset != null)
        {
            _remoteAsset.Loaded -= ConfigureScene;
        }
    }

    private void ConfigureScene(GameObject root)
    {
        CopyImportedCamera();
        RemoveSourceCameraNode(root);
        ConfigureMaterial(root);
        AddAreaLights(root);
        ConfigureQuickControls(root);
    }

    public static void ConfigureEditableScene(GameObject root, GameManager manager, RayTracingQuickControls controls)
    {
        Camera sourceCamera = root.GetComponentInChildren<Camera>(true);
        if (sourceCamera != null && manager != null && manager.renderTextureCamera != null)
        {
            var targetCamera = manager.renderTextureCamera;
            targetCamera.transform.SetPositionAndRotation(sourceCamera.transform.position, sourceCamera.transform.rotation);
            targetCamera.fieldOfView = sourceCamera.fieldOfView * 2.0f;
            var orbitFocus = sourceCamera.transform.position + sourceCamera.transform.forward * 25.0f;
            manager.CameraManager.SetOrbitState(orbitFocus, sourceCamera.transform.position);
            manager.ResetFrameAccumulation();
        }

        RemoveSourceCameraNode(root);
        ConfigureMaterial(root);
        AddAreaLights(root);
        ConfigureQuickControls(root, controls);
    }

    private static void ConfigureMaterial(GameObject root)
    {
        Transform surface = FindChild(root.transform, MaterialSurfaceName);
        RayMaterial material = surface != null ? surface.GetComponent<RayMaterial>() : null;
        if (material == null)
        {
            Debug.LogWarning($"Material-ball scene did not contain '{MaterialSurfaceName}'.", root);
            return;
        }

        // Three.js converts hexadecimal material colors from sRGB to its linear working space.
        material.Color = ToLinearColor32(new Color32(255, 230, 189, 255));
        material.Type = RayMaterial.MaterialType.Metal;
        material.Metallic = 1.0f;
        material.Smoothness = 1.0f;
    }

    private static void AddAreaLights(GameObject root)
    {
        EnsureAreaLights(root);
    }

    public static void EnsureAreaLights(GameObject root)
    {
        AddAreaLight(root.transform, "light", 15.0f, 6327.84f);
        for (int i = 0; i < 4; i++)
        {
            AddAreaLight(root.transform, "light" + i, 24.36f, 11185.5f);
        }
    }

    private static void AddAreaLight(Transform root, string anchorName, float size, float watts)
    {
        Transform anchor = FindChild(root, anchorName);
        if (anchor == null)
        {
            Debug.LogWarning($"Material-ball scene did not contain light anchor '{anchorName}'.", root);
            return;
        }

        Transform existing = FindChild(anchor, "Area Light");
        var lightObject = existing != null ? existing.gameObject : new GameObject("Area Light");
        if (existing == null)
        {
            lightObject.transform.SetParent(anchor, false);
        }

        var meshFilter = lightObject.GetComponent<MeshFilter>() ?? lightObject.AddComponent<MeshFilter>();
        if (meshFilter.sharedMesh == null)
        {
            meshFilter.sharedMesh = CreateRectAreaLightQuad(size);
        }
        var light = lightObject.GetComponent<RayLight>() ?? lightObject.AddComponent<RayLight>();
        light.Color = Color.white;
        // Match the source's watts-to-luminance conversion, then scale into this renderer's
        // emitter units. The 24.36-unit ceiling panels evaluate to approximately 7.0.
        light.Intensity = watts * LumensPerWatt / (size * size * 4.0f * Mathf.PI) * AreaLightRadianceScale;
        if (lightObject.GetComponent<PathTracingObject>() == null)
        {
            lightObject.AddComponent<PathTracingObject>();
        }
    }

    private void ConfigureQuickControls(GameObject root)
    {
        ConfigureQuickControls(root, GetComponent<RayTracingQuickControls>());
    }

    private static void ConfigureQuickControls(GameObject root, RayTracingQuickControls controls)
    {
        if (controls == null)
        {
            return;
        }

        var entries = new List<RayTracingQuickControls.Entry>();
        foreach (RayMaterial material in root.GetComponentsInChildren<RayMaterial>(true))
        {
            entries.Add(RayTracingQuickControls.CreateEntry(
                "Material: " + material.name,
                material,
                "Color", "Type", "Metallic", "Smoothness", "Opacity", "Specular", "Transmission", "RefractionIndex"));
        }
        foreach (RayLight light in root.GetComponentsInChildren<RayLight>(true))
        {
            if (light.GetComponent<RayMaterial>() != null)
            {
                continue;
            }
            entries.Add(RayTracingQuickControls.CreateEntry("Light: " + light.transform.parent.name, light, "Color", "Intensity"));
        }
        controls.SetEntries(entries);
    }

    private static Color32 ToLinearColor32(Color32 srgb)
    {
        Color linear = ((Color)srgb).linear;
        return linear;
    }

    private void CopyImportedCamera()
    {
        var manager = GetComponentInParent<GameManager>();
        if (!_remoteAsset.HasImportedCameraPose || manager == null || manager.renderTextureCamera == null)
        {
            return;
        }

        var targetCamera = manager.renderTextureCamera;
        targetCamera.transform.SetPositionAndRotation(_remoteAsset.ImportedCameraPosition, _remoteAsset.ImportedCameraRotation);
        targetCamera.fieldOfView = _remoteAsset.ImportedCameraFieldOfView * 2.0f;
        var orbitFocus = _remoteAsset.ImportedCameraPosition
            + _remoteAsset.ImportedCameraRotation * Vector3.forward * 25.0f;
        manager.CameraManager.SetOrbitState(orbitFocus, _remoteAsset.ImportedCameraPosition);
        manager.ResetFrameAccumulation();
    }

    private static void RemoveSourceCameraNode(GameObject root)
    {
        var cameraNode = FindChild(root.transform, CameraNodeName);
        if (cameraNode != null)
        {
            if (Application.isPlaying)
            {
                Destroy(cameraNode.gameObject);
            }
            else
            {
                DestroyImmediate(cameraNode.gameObject);
            }
        }
    }

    private static Transform FindChild(Transform root, string childName)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child.name == childName)
            {
                return child;
            }
        }
        return null;
    }

    private static Mesh CreateRectAreaLightQuad(float size)
    {
        float halfSize = size * 0.5f;
        var mesh = new Mesh
        {
            vertices = new[]
            {
                new Vector3(-halfSize, -halfSize, 0.0f),
                new Vector3(halfSize, -halfSize, 0.0f),
                new Vector3(halfSize, halfSize, 0.0f),
                new Vector3(-halfSize, halfSize, 0.0f)
            },
            triangles = new[] { 0, 1, 2, 0, 2, 3 }
        };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}
