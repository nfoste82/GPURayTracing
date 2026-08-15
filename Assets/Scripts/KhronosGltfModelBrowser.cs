using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

[DisallowMultipleComponent]
[RequireComponent(typeof(RemoteGltfRayTracingAsset))]
public sealed class KhronosGltfModelBrowser : MonoBehaviour
{
    private const string ModelIndexUrl = "https://raw.githubusercontent.com/KhronosGroup/glTF-Sample-Assets/main/Models/model-index.json";
    private const string ModelRootUrl = "https://raw.githubusercontent.com/KhronosGroup/glTF-Sample-Assets/main/Models";
    private const float FramePadding = 1.25f;
    private const float MinimumModelBoundsDimension = 5.0f; // Keep models from being too small, triangles too small don't play well with BVH
    private const float PanelMargin = 12.0f;

    [Tooltip("Loads the Khronos model list when Play mode starts.")]
    public bool LoadCatalogOnStart = true;

    [Tooltip("Initial camera position for the empty display room.")]
    public Vector3 initialRoomCameraPosition;

    [Tooltip("Initial orbit target for the empty display room.")]
    public Vector3 initialRoomFocusPosition;

    [Tooltip("Removes every model previously loaded through this browser before loading another.")]
    public bool replaceExistingModels = true;

    private readonly List<Model> _models = new ();
    private readonly List<Model> _filteredModels = new ();
    private RemoteGltfRayTracingAsset _remoteAsset;
    private Vector2 _scrollPosition;
    private string _search = string.Empty;
    private string _catalogError;
    private string _selectedName;
    private GUIStyle _panelStyle;
    private GUIStyle _titleStyle;
    private GUIStyle _statusStyle;
    private GUIStyle _textFieldStyle;
    private GUIStyle _buttonStyle;
    private GUIStyle _checkboxStyle;
    private GUIStyle _minimizedButtonStyle;
    private bool _isMinimized;
    private Transform _loadedModelsParent;

    [Serializable]
    private sealed class ModelIndex
    {
        public Model[] models;
    }

    [Serializable]
    private sealed class Model
    {
        public string label;
        public string name;
        public Variants variants;
    }

    [Serializable]
    private sealed class Variants
    {
        public string glTFBinary;
    }

    private void Awake()
    {
        _remoteAsset = GetComponent<RemoteGltfRayTracingAsset>();
        _remoteAsset.LoadOnStart = false;
        _remoteAsset.UseDiskCache = true;
        _remoteAsset.ShowPreviewsInPlayMode = false;
        _remoteAsset.Loaded += FrameLoadedModel;

        var loadedModels = new GameObject("Loaded Khronos Models");
        loadedModels.transform.SetParent(transform, false);
        _loadedModelsParent = loadedModels.transform;
    }

    private void Start()
    {
        ApplyInitialRoomCamera();
        StartCoroutine(RegisterStaticSceneObjects());
        if (LoadCatalogOnStart)
        {
            StartCoroutine(LoadCatalog());
        }
    }

    private void ApplyInitialRoomCamera()
    {
        // Existing generated scenes predate these serialized fields. Preserve their configured
        // camera instead of replacing it with an origin-to-origin orbit on the first Play frame.
        if (initialRoomCameraPosition == Vector3.zero && initialRoomFocusPosition == Vector3.zero)
        {
            return;
        }

        var manager = GetComponentInParent<GameManager>();
        if (manager == null || manager.renderTextureCamera == null)
        {
            return;
        }

        // SetOrbitState marks the orbit behavior active. Without this, CameraManager's first
        // input update replaces the serialized room target with a two-unit default target.
        manager.CameraManager.SetOrbitState(initialRoomFocusPosition, initialRoomCameraPosition);
        manager.ResetFrameAccumulation();
    }

    private IEnumerator RegisterStaticSceneObjects()
    {
        // The room's prefab objects are enabled before GameManager.Start begins its deferred first
        // buffer build. Wait through that startup frame before registering and rebuilding them.
        yield return null;
        yield return null;
        
        var manager = GetComponentInParent<GameManager>();
        if (manager == null)
        {
            yield break;
        }

        foreach (var pathTracingObject in manager.GetComponentsInChildren<PathTracingObject>(true))
        {
            pathTracingObject.RefreshRegistration();
        }
        manager.RebuildBuffers();
    }

    private void OnDestroy()
    {
        if (_remoteAsset != null)
        {
            _remoteAsset.Loaded -= FrameLoadedModel;
        }
    }

    private IEnumerator LoadCatalog()
    {
        _catalogError = null;
        using var request = UnityWebRequest.Get(ModelIndexUrl);
        yield return request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success)
        {
            _catalogError = "Could not load Khronos model list: " + request.error;
            yield break;
        }

        var json = request.downloadHandler.text.Replace("\"glTF-Binary\"", "\"glTFBinary\"");
        ModelIndex index = JsonUtility.FromJson<ModelIndex>("{\"models\":" + json + "}");
        if (index?.models == null)
        {
            _catalogError = "Khronos returned an invalid model list.";
            yield break;
        }

        _models.Clear();
        foreach (var model in index.models)
        {
            if (model?.variants != null && !string.IsNullOrEmpty(model.variants.glTFBinary))
            {
                _models.Add(model);
            }
        }
        _models.Sort((left, right) => string.Compare(left.label ?? left.name, right.label ?? right.name, StringComparison.OrdinalIgnoreCase));
        RebuildFilteredModels();
    }

    private void RebuildFilteredModels()
    {
        _filteredModels.Clear();
        foreach (var model in _models)
        {
            string displayName = model.label ?? model.name;
            if (string.IsNullOrEmpty(_search) || displayName.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _filteredModels.Add(model);
            }
        }
        _scrollPosition = Vector2.zero;
    }

    private void SelectModel(Model model)
    {
        if (_remoteAsset.IsLoading || model == null)
        {
            return;
        }

        _selectedName = model.label ?? model.name;
        if (replaceExistingModels)
        {
            ClearLoadedModels();
        }
        else
        {
            _remoteAsset.DetachLoadedAsset();
        }
        _remoteAsset.Url = BuildModelUrl(model);
        _ = _remoteAsset.Load();
    }

    public static string BuildModelUrl(string modelName, string binaryFileName)
    {
        return ModelRootUrl + "/" + EscapePathSegment(modelName) + "/glTF-Binary/" + EscapePathSegment(binaryFileName);
    }

    private static string BuildModelUrl(Model model)
    {
        return BuildModelUrl(model.name, model.variants.glTFBinary);
    }

    private static string EscapePathSegment(string value)
    {
        return Uri.EscapeDataString(value);
    }

    private void FrameLoadedModel(GameObject root)
    {
        root.transform.SetParent(_loadedModelsParent, true);
        Bounds bounds;
        if (!TryGetWorldBounds(root, out bounds))
        {
            return;
        }

        root.transform.localScale *= GetMinimumUniformScaleFactor(bounds);
        if (!TryGetWorldBounds(root, out bounds))
        {
            return;
        }

        root.transform.position -= GetFloorPlacementOffset(bounds);
        if (!TryGetWorldBounds(root, out bounds))
        {
            return;
        }

        GameManager manager = GetComponentInParent<GameManager>();
        if (manager == null || manager.renderTextureCamera == null)
        {
            return;
        }

        float distance = GetFramingDistance(bounds, manager.renderTextureCamera.fieldOfView, manager.renderTextureCamera.aspect);
        Vector3 cameraPosition = bounds.center + new Vector3(0.0f, bounds.extents.y * 0.35f, -distance);
        manager.CameraManager.cameraAutoFocus = false;
        manager.CameraManager.cameraOrbitZoomSpeed = GetOrbitZoomSpeed(bounds);
        manager.CameraManager.SetOrbitState(bounds.center, cameraPosition);
        manager.ResetFrameAccumulation();
    }

    private void ClearLoadedModels()
    {
        // The remote loader owns its current root even after it is parented here, so clear it
        // before removing the additive roots that were loaded through this browser.
        _remoteAsset.ClearLoadedAsset();
        foreach (Transform child in _loadedModelsParent)
        {
            Destroy(child.gameObject);
        }
    }

    private static bool TryGetWorldBounds(GameObject root, out Bounds bounds)
    {
        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        bool hasBounds = false;
        bounds = default;
        foreach (MeshFilter filter in filters)
        {
            if (filter.sharedMesh == null)
            {
                continue;
            }

            Bounds meshBounds = filter.sharedMesh.bounds;
            Matrix4x4 localToWorld = filter.transform.localToWorldMatrix;
            Vector3 center = meshBounds.center;
            Vector3 extents = meshBounds.extents;
            Bounds worldBounds = new Bounds(localToWorld.MultiplyPoint3x4(center), Vector3.zero);
            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        worldBounds.Encapsulate(localToWorld.MultiplyPoint3x4(center + Vector3.Scale(extents, new Vector3(x, y, z))));
                    }
                }
            }
            if (hasBounds)
            {
                bounds.Encapsulate(worldBounds);
            }
            else
            {
                bounds = worldBounds;
                hasBounds = true;
            }
        }
        return hasBounds;
    }

    private static Vector3 GetFloorPlacementOffset(Bounds bounds)
    {
        // Center the display model horizontally, then place its lowest point on the room floor.
        return new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
    }

    private static float GetMinimumUniformScaleFactor(Bounds bounds)
    {
        float largestDimension = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        return largestDimension > Mathf.Epsilon
            ? Mathf.Max(1.0f, MinimumModelBoundsDimension / largestDimension)
            : 1.0f;
    }

    private static float GetFramingDistance(Bounds bounds, float verticalFieldOfView, float aspect)
    {
        float halfVerticalFov = Mathf.Max(0.01f, verticalFieldOfView * Mathf.Deg2Rad * 0.5f);
        float halfHorizontalFov = Mathf.Atan(Mathf.Tan(halfVerticalFov) * Mathf.Max(0.01f, aspect));
        float verticalDistance = bounds.extents.y / Mathf.Tan(halfVerticalFov);
        float horizontalDistance = bounds.extents.x / Mathf.Tan(halfHorizontalFov);
        return Mathf.Max(0.1f, Mathf.Max(verticalDistance, horizontalDistance) * FramePadding + bounds.extents.z);
    }

    private static float GetOrbitZoomSpeed(Bounds bounds)
    {
        return Mathf.Max(0.1f, bounds.extents.magnitude);
    }

    private void OnGUI()
    {
        if (_panelStyle == null)
        {
            _panelStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(20, 20, 20, 20) };
            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold };
            _statusStyle = new GUIStyle(GUI.skin.label) { fontSize = 24, wordWrap = true };
            _textFieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 24 };
            _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 24 };
            _checkboxStyle = new GUIStyle(GUI.skin.button) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _minimizedButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = 28, fontStyle = FontStyle.Bold };
        }

        if (_isMinimized)
        {
            if (GUI.Button(new Rect(12.0f, 12.0f, 48.0f, 48.0f), new GUIContent("M", "Open model browser"), _minimizedButtonStyle))
            {
                _isMinimized = false;
            }
            return;
        }

        const float width = 580.0f;
        const float rowHeight = 56.0f;
        float height = Screen.height - (PanelMargin * 2.0f);
        GUILayout.BeginArea(new Rect(PanelMargin, PanelMargin, width, height), _panelStyle);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Khronos glTF Models", _titleStyle);
        if (GUILayout.Button("_", _buttonStyle, GUILayout.Width(56.0f), GUILayout.Height(40.0f)))
        {
            _isMinimized = true;
        }
        GUILayout.EndHorizontal();
        GUILayout.Label("Self-contained GLB samples. Downloads are cached locally.", _statusStyle);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(replaceExistingModels ? "X" : string.Empty, _checkboxStyle, GUILayout.Width(32.0f), GUILayout.Height(32.0f)))
        {
            replaceExistingModels = !replaceExistingModels;
        }
        GUILayout.Label("Replace existing models", _statusStyle);
        GUILayout.EndHorizontal();
        string search = GUILayout.TextField(_search, _textFieldStyle, GUILayout.Height(rowHeight));
        if (!string.Equals(search, _search, StringComparison.Ordinal))
        {
            _search = search;
            RebuildFilteredModels();
        }

        if (_remoteAsset.IsLoading)
        {
            GUILayout.Label("Loading " + _selectedName + "...", _statusStyle);
        }
        else if (!string.IsNullOrEmpty(_remoteAsset.Error))
        {
            GUILayout.Label(_remoteAsset.Error, _statusStyle);
        }
        else if (_remoteAsset.IsLoaded)
        {
            GUILayout.Label(_selectedName + (_remoteAsset.LoadedFromCache ? " (cached)" : " (downloaded)"), _statusStyle);
        }

        if (!string.IsNullOrEmpty(_catalogError))
        {
            GUILayout.Label(_catalogError, _statusStyle);
        }
        else if (_models.Count == 0)
        {
            GUILayout.Label("Loading model list...", _statusStyle);
        }
        else
        {
            // The panel height is Screen.height. As the final layout element, the scroll view
            // receives every remaining pixel after the measured header and status messages.
            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.ExpandHeight(true));
            foreach (Model model in _filteredModels)
            {
                GUI.enabled = !_remoteAsset.IsLoading;
                if (GUILayout.Button(model.label ?? model.name, _buttonStyle, GUILayout.Height(rowHeight - 4.0f)))
                {
                    SelectModel(model);
                }
            }
            GUI.enabled = true;
            GUILayout.EndScrollView();
        }
        GUILayout.EndArea();
    }
}
