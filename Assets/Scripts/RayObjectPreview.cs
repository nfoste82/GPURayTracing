using UnityEngine;

[ExecuteAlways]
public class RayObjectPreview : MonoBehaviour
{
    [Tooltip("Global scene debugging override. Keeps all ray-object raster previews visible in Play mode.")]
    public static bool KeepRenderersEnabledInPlayMode;

    [SerializeField]
    private bool hideRendererInPlayMode = true;

    [SerializeField]
    private bool showUnityPointLightForRayLights = true;

    private const string PreviewMaterialName = "Ray Tracing Preview Material";
    private const string PreviewShaderName = "Hidden/RayTracing/ScenePreview";
    private static readonly Vector3 PreviewKeyLightDirection = new Vector3(0.35f, 0.8f, 0.45f).normalized;

    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;
    private SphereCollider _sphereCollider;
    private RayMaterial _rayMaterial;
    private RayLight _rayLight;
    private Light _unityLight;
    private Mesh _sphereMesh;
    private Vector3[] _sphereVertices;
    private float _previewRadius = -1.0f;
    private Vector3 _previewCenter;

    public bool HideRendererInPlayMode
    {
        get => hideRendererInPlayMode;
        set => hideRendererInPlayMode = value;
    }

    private void OnEnable()
    {
        SyncPreview();
    }

    private void OnValidate()
    {
        SyncPreview();
    }

    private void Update()
    {
        SyncPreview();
    }

    private void OnDestroy()
    {
        if (_sphereMesh != null)
        {
            DestroyPreviewObject(_sphereMesh);
        }

    }

    private void SyncPreview()
    {
        _sphereCollider = GetComponent<SphereCollider>();
        _rayMaterial = GetComponent<RayMaterial>();
        _rayLight = GetComponent<RayLight>();
        if (_rayMaterial == null && _rayLight == null)
        {
            return;
        }

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

        if (_sphereCollider != null)
        {
            SyncSphereMesh();
        }

        SyncMaterial();
        // The Scene view does not run the compute renderer, so editor Play mode still needs the
        // raster preview. Standalone players retain the compute-only default.
        _meshRenderer.enabled = !Application.isPlaying || Application.isEditor ||
            KeepRenderersEnabledInPlayMode || !hideRendererInPlayMode;
        SyncLight();
    }

    private void SyncSphereMesh()
    {
        if (_sphereMesh == null || _sphereVertices == null)
        {
            var sourceMesh = Resources.GetBuiltinResource<Mesh>("New-Sphere.fbx");
            if (sourceMesh == null)
            {
                return;
            }

            _sphereMesh = Instantiate(sourceMesh);
            _sphereMesh.name = "Ray Sphere Preview Mesh";
            _sphereMesh.hideFlags = HideFlags.HideAndDontSave;
            _sphereVertices = _sphereMesh.vertices;
        }

        if (Mathf.Approximately(_previewRadius, _sphereCollider.radius) && _previewCenter == _sphereCollider.center)
        {
            _meshFilter.sharedMesh = _sphereMesh;
            return;
        }

        var vertices = new Vector3[_sphereVertices.Length];
        for (int i = 0; i < _sphereVertices.Length; i++)
        {
            vertices[i] = _sphereVertices[i] * _sphereCollider.radius * 2.0f + _sphereCollider.center;
        }
        _sphereMesh.vertices = vertices;
        _sphereMesh.RecalculateBounds();
        _meshFilter.sharedMesh = _sphereMesh;
        _previewRadius = _sphereCollider.radius;
        _previewCenter = _sphereCollider.center;
    }

    private void SyncMaterial()
    {
        var material = _meshRenderer.sharedMaterial;
        var previewShader = Shader.Find(PreviewShaderName);
        if (material == null || material.name != PreviewMaterialName || material.shader != previewShader)
        {
            if (previewShader == null)
            {
                return;
            }

            material = new Material(previewShader)
            {
                name = PreviewMaterialName,
                hideFlags = HideFlags.HideAndDontSave
            };
            _meshRenderer.sharedMaterial = material;
        }

        Color color = _rayLight != null ? _rayLight.Color : _rayMaterial.Color;
        if (_rayLight != null)
        {
            color *= 2.0f * Mathf.Max(0.0f, _rayLight.Intensity);
            color.a = 1.0f;
            SetColor(material, "_EmissionColor", color);
            material.EnableKeyword("_EMISSION");
        }
        else
        {
            color.a = Mathf.Clamp01(_rayMaterial.Opacity);
        }

        bool isTransparent = color.a < 0.999f;
        material.SetFloat("_ZWrite", isTransparent ? 0.0f : 1.0f);
        material.renderQueue = isTransparent
            ? (int)UnityEngine.Rendering.RenderQueue.Transparent
            : (int)UnityEngine.Rendering.RenderQueue.Geometry;

        SetColor(material, "_BaseColor", color);
        SetColor(material, "_Color", color);
        SetColor(material, "_PreviewAmbientColor", GetPreviewAmbientColor());
        SetVector(material, "_PreviewKeyLightDirection", PreviewKeyLightDirection);
        SetTexture(material, "_BaseMap", _rayMaterial != null ? _rayMaterial.AlbedoTexture : null);
        SetTexture(material, "_MainTex", _rayMaterial != null ? _rayMaterial.AlbedoTexture : null);
        if (_rayMaterial != null)
        {
            material.SetTextureScale("_MainTex", _rayMaterial.TextureUvScale);
        }

        if (_rayMaterial != null)
        {
            SetFloat(material, "_Metallic", GetPreviewMetallic());
            SetFloat(material, "_Glossiness", _rayMaterial.Smoothness);
            SetFloat(material, "_Smoothness", _rayMaterial.Smoothness);
            SetTexture(material, "_MetallicGlossMap", _rayMaterial.MetallicRoughnessTexture);
            SetTexture(material, "_BumpMap", _rayMaterial.NormalTexture);
        }
    }

    private void SyncLight()
    {
        if (_rayLight == null || !showUnityPointLightForRayLights)
        {
            if (_unityLight != null)
            {
                _unityLight.enabled = false;
            }
            return;
        }

        _unityLight = GetComponent<Light>();
        if (_unityLight == null)
        {
            _unityLight = gameObject.AddComponent<Light>();
        }

        _unityLight.enabled = true;
        _unityLight.type = LightType.Point;
        _unityLight.color = _rayLight.Color;
        _unityLight.range = Mathf.Max(1.0f, _sphereCollider != null ? _sphereCollider.radius * 8.0f : 4.0f);
        _unityLight.intensity = Mathf.Max(0.0f, _rayLight.Intensity);
    }

    private float GetPreviewMetallic()
    {
        if (_rayMaterial.Type == RayMaterial.MaterialType.Metal && Mathf.Approximately(_rayMaterial.Metallic, 0.0f))
        {
            return 1.0f;
        }
        return _rayMaterial.Metallic;
    }

    private Color GetPreviewAmbientColor()
    {
        var manager = GetComponentInParent<GameManager>();
        return manager != null ? manager.Lighting.SkyboxLightColor : RenderSettings.ambientSkyColor;
    }

    private static void SetColor(Material material, string propertyName, Color value)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetColor(propertyName, value);
        }
    }

    private static void SetFloat(Material material, string propertyName, float value)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetFloat(propertyName, value);
        }
    }

    private static void SetVector(Material material, string propertyName, Vector3 value)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetVector(propertyName, value);
        }
    }

    private static void SetTexture(Material material, string propertyName, Texture value)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetTexture(propertyName, value);
        }
    }

    private static void DestroyPreviewObject(Object target)
    {
        if (Application.isPlaying)
        {
            Destroy(target);
        }
        else
        {
            DestroyImmediate(target);
        }
    }
}
