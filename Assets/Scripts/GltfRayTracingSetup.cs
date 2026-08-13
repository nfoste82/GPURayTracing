using UnityEngine;

public static class GltfRayTracingSetup
{
    public static void ConfigureHierarchy(GameObject root, bool showPreviewsInPlayMode = false)
    {
        foreach (MeshFilter meshFilter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (meshFilter.sharedMesh == null)
            {
                continue;
            }

            MeshRenderer meshRenderer = meshFilter.GetComponent<MeshRenderer>();
            if (meshRenderer == null)
            {
                ConfigureObject(meshFilter.gameObject, null, showPreviewsInPlayMode);
                continue;
            }

            Material[] materials = meshRenderer.sharedMaterials;
            if (materials.Length > 1 && meshFilter.sharedMesh.subMeshCount > 1)
            {
                CreateSubmeshObjects(meshFilter, materials, showPreviewsInPlayMode);
                meshRenderer.enabled = false;
                continue;
            }

            ConfigureObject(meshFilter.gameObject, meshRenderer.sharedMaterial, showPreviewsInPlayMode);
        }
    }

    public static void ApplyMaterial(RayMaterial rayMaterial, Material unityMaterial)
    {
        rayMaterial.Type = RayMaterial.MaterialType.Diffuse;
        rayMaterial.Color = GetColor(unityMaterial, Color.white, "_BaseColor", "_Color", "baseColorFactor");
        rayMaterial.AlbedoTexture = GetTexture(unityMaterial, "_BaseMap", "_MainTex", "baseColorTexture");
        rayMaterial.Metallic = GetFloat(unityMaterial, 0.0f, "_Metallic", "_MetallicFactor");
        rayMaterial.MetallicRoughnessTexture = GetTexture(unityMaterial, "_MetallicRoughnessMap", "_MetallicGlossMap");
        rayMaterial.NormalTexture = GetTexture(unityMaterial, "_BumpMap", "_NormalMap");
        rayMaterial.Smoothness = Mathf.Clamp01(1.0f - GetFloat(unityMaterial, 0.5f, "_Roughness", "_RoughnessFactor", "_Smoothness", "_Glossiness"));
        rayMaterial.Opacity = 1.0f;
        rayMaterial.InterpolateNormals = true;
    }

    private static void CreateSubmeshObjects(MeshFilter sourceFilter, Material[] materials, bool showPreviewsInPlayMode)
    {
        Mesh sourceMesh = sourceFilter.sharedMesh;
        int submeshCount = Mathf.Min(sourceMesh.subMeshCount, materials.Length);
        for (int submeshIndex = 0; submeshIndex < submeshCount; submeshIndex++)
        {
            GameObject child = new GameObject($"Ray Tracing {sourceMesh.name} {submeshIndex}");
            child.transform.SetParent(sourceFilter.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = CreateSubmesh(sourceMesh, submeshIndex);
            ConfigureObject(child, materials[submeshIndex], showPreviewsInPlayMode);
        }
    }

    private static Mesh CreateSubmesh(Mesh sourceMesh, int submeshIndex)
    {
        var mesh = new Mesh
        {
            name = $"{sourceMesh.name} Ray Tracing Submesh {submeshIndex}",
            indexFormat = sourceMesh.indexFormat
        };
        mesh.vertices = sourceMesh.vertices;
        mesh.normals = sourceMesh.normals;
        mesh.tangents = sourceMesh.tangents;
        mesh.uv = sourceMesh.uv;
        mesh.SetTriangles(sourceMesh.GetTriangles(submeshIndex), 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void ConfigureObject(GameObject gameObject, Material unityMaterial, bool showPreviewsInPlayMode)
    {
        RayMaterial rayMaterial = gameObject.GetComponent<RayMaterial>();
        if (rayMaterial == null)
        {
            rayMaterial = gameObject.AddComponent<RayMaterial>();
        }

        ApplyMaterial(rayMaterial, unityMaterial);
        // Scene previews need a renderer, but ray tracing only requires this MeshFilter.
        if (gameObject.GetComponent<MeshRenderer>() == null)
        {
            gameObject.AddComponent<MeshRenderer>();
        }
        if (gameObject.GetComponent<PathTracingObject>() == null)
        {
            gameObject.AddComponent<PathTracingObject>();
        }

        RayObjectPreview preview = gameObject.GetComponent<RayObjectPreview>();
        if (preview != null)
        {
            preview.HideRendererInPlayMode = !showPreviewsInPlayMode;
        }
    }

    private static Color GetColor(Material material, Color fallback, params string[] propertyNames)
    {
        if (material == null)
        {
            return fallback;
        }

        foreach (string propertyName in propertyNames)
        {
            if (material.HasProperty(propertyName))
            {
                return material.GetColor(propertyName);
            }
        }
        return fallback;
    }

    private static float GetFloat(Material material, float fallback, params string[] propertyNames)
    {
        if (material == null)
        {
            return fallback;
        }

        foreach (string propertyName in propertyNames)
        {
            if (material.HasProperty(propertyName))
            {
                return material.GetFloat(propertyName);
            }
        }
        return fallback;
    }

    private static Texture2D GetTexture(Material material, params string[] propertyNames)
    {
        if (material == null)
        {
            return null;
        }

        foreach (string propertyName in propertyNames)
        {
            if (material.HasProperty(propertyName) && material.GetTexture(propertyName) is Texture2D texture)
            {
                return texture;
            }
        }
        return null;
    }
}
