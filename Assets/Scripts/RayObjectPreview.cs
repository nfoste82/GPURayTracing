using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(SphereCollider))]
public class RayObjectPreview : MonoBehaviour
{
    [SerializeField]
    private bool hideMeshRendererInPlayMode = true;

    [SerializeField]
    private bool showUnityPointLightForRayLights = true;

    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;
    private SphereCollider _sphereCollider;
    private RayMaterial _rayMaterial;
    private RayLight _rayLight;
    private Light _unityLight;
    private float _previewRadius = -1.0f;
    private Vector3 _previewCenter;

    private void Reset()
    {
        EnsurePreviewComponents();
        SyncPreview();
    }

    private void OnEnable()
    {
        EnsurePreviewComponents();
        SyncPreview();
    }

    private void OnValidate()
    {
        if (!Application.isPlaying)
        {
            EnsurePreviewComponents();
            SyncPreview();
        }
    }

    private void Update()
    {
        SyncPreview();
    }

    private void EnsurePreviewComponents()
    {
        _sphereCollider = GetComponent<SphereCollider>();
        _rayMaterial = GetComponent<RayMaterial>();
        _rayLight = GetComponent<RayLight>();

        _meshFilter = GetComponent<MeshFilter>();
        if (_meshFilter == null)
        {
            _meshFilter = gameObject.AddComponent<MeshFilter>();
        }

        _meshRenderer = GetComponent<MeshRenderer>();
        if (_meshRenderer == null)
        {
            _meshRenderer = gameObject.AddComponent<MeshRenderer>();
        }

        if (_meshFilter.sharedMesh == null)
        {
            _meshFilter.sharedMesh = CreateSphereMesh(_sphereCollider.center, _sphereCollider.radius);
            _previewRadius = _sphereCollider.radius;
            _previewCenter = _sphereCollider.center;
        }

        if (!IsUsablePreviewMaterial(_meshRenderer.sharedMaterial))
        {
            _meshRenderer.sharedMaterial = GetPreviewMaterial(_rayMaterial, _rayLight);
        }

        if (_rayLight != null && showUnityPointLightForRayLights)
        {
            _unityLight = GetComponent<Light>();
            if (_unityLight == null)
            {
                _unityLight = gameObject.AddComponent<Light>();
            }
        }
        else
        {
            _unityLight = GetComponent<Light>();
        }
    }

    private void SyncPreview()
    {
        if (_sphereCollider == null || _meshRenderer == null)
        {
            return;
        }

        if (!Mathf.Approximately(_previewRadius, _sphereCollider.radius) || _previewCenter != _sphereCollider.center)
        {
            _meshFilter.sharedMesh = CreateSphereMesh(_sphereCollider.center, _sphereCollider.radius);
            _previewRadius = _sphereCollider.radius;
            _previewCenter = _sphereCollider.center;
        }

        _meshRenderer.enabled = !Application.isPlaying || !hideMeshRendererInPlayMode;

        var material = _meshRenderer.sharedMaterial;
        if (IsUsablePreviewMaterial(material))
        {
            material.color = GetPreviewColor(_rayMaterial, _rayLight);
        }

        if (_unityLight != null)
        {
            bool showLight = _rayLight != null && showUnityPointLightForRayLights;
            _unityLight.enabled = showLight;
            _unityLight.type = LightType.Point;
            _unityLight.color = GetPreviewColor(null, _rayLight);
            _unityLight.range = Mathf.Max(1.0f, _sphereCollider.radius * 8.0f);
            _unityLight.intensity = showLight ? 1.0f : 0.0f;
        }
    }

    private static Material GetPreviewMaterial(RayMaterial rayMaterial, RayLight rayLight)
    {
        var shader = Shader.Find("Standard") ?? Shader.Find("Diffuse");
        var material = new Material(shader)
        {
            name = "Ray Object Preview Material",
            color = GetPreviewColor(rayMaterial, rayLight)
        };
        return material;
    }

    private static bool IsUsablePreviewMaterial(Material material)
    {
        return material != null && material.shader != null;
    }

    private static Color GetPreviewColor(RayMaterial rayMaterial, RayLight rayLight)
    {
        if (rayLight != null)
        {
            return rayLight.Color;
        }

        if (rayMaterial != null)
        {
            return rayMaterial.Color;
        }
        return Color.white;
    }

    private static Mesh CreateSphereMesh(Vector3 center, float radius)
    {
        var temporary = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var sourceMesh = temporary.GetComponent<MeshFilter>().sharedMesh;
        var mesh = Instantiate(sourceMesh);
        mesh.name = "Ray Sphere Preview Mesh";
        mesh.hideFlags = HideFlags.HideAndDontSave;

        var vertices = mesh.vertices;
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = vertices[i] * radius * 2.0f + center;
        }
        mesh.vertices = vertices;
        mesh.RecalculateBounds();

        if (Application.isPlaying)
        {
            Destroy(temporary);
        }
        else
        {
            DestroyImmediate(temporary);
        }

        return mesh;
    }
}
