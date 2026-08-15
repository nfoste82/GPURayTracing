using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;
using UnityEngine.Networking;

[DisallowMultipleComponent]
public class RemoteGltfRayTracingAsset : MonoBehaviour
{
    [Tooltip("HTTPS URL of a .glb or .gltf asset. Relative .bin and image URLs in a .gltf are resolved from this URL.")]
    public string Url;

    [Tooltip("Loads the configured asset when Play mode starts.")]
    public bool LoadOnStart = true;

    [Tooltip("Caches self-contained .glb downloads in Application.persistentDataPath and loads the cached copy first.")]
    public bool UseDiskCache = true;

    [Tooltip("Keeps imported Unity mesh renderers visible alongside the ray-traced result.")]
    public bool ShowPreviewsInPlayMode = true;

    public bool IsLoading { get; private set; }
    public bool IsLoaded { get; private set; }
    public string Error { get; private set; }
    public bool HasImportedCameraPose { get; private set; }
    public bool LoadedFromCache { get; private set; }
    public Vector3 ImportedCameraPosition { get; private set; }
    public Quaternion ImportedCameraRotation { get; private set; }
    public float ImportedCameraFieldOfView { get; private set; }
    public event Action<GameObject> Loaded;

    private GltfAsset _gltfAsset;
    private GameObject _contentRoot;

    private async void Start()
    {
        if (LoadOnStart)
        {
            await Load();
        }
    }

    public async Task<bool> Load()
    {
        if (IsLoading)
        {
            return false;
        }

        if (!IsRemoteGltfUrl(Url))
        {
            Error = "Remote glTF URL must be an absolute HTTP(S) URL.";
            Debug.LogError(Error, this);
            return false;
        }

        ClearLoadedAsset();
        IsLoading = true;
        Error = null;
        LoadedFromCache = false;
        try
        {
            _contentRoot = new GameObject("Remote glTF Content");
            _contentRoot.transform.SetParent(transform, false);
            _gltfAsset = _contentRoot.AddComponent<GltfAsset>();
            _gltfAsset.LoadOnStartup = false;
            string loadUrl = await GetLoadUrl();
            if (loadUrl == null)
            {
                return false;
            }

            bool success = await _gltfAsset.Load(loadUrl);
            if (!success)
            {
                Error = $"Failed to load glTF from {Url}. See the console for glTFast diagnostics.";
                Debug.LogError(Error, this);
                return false;
            }

            GltfRayTracingSetup.ConfigureHierarchy(_contentRoot, ShowPreviewsInPlayMode, _gltfAsset.Importer);
            ExtractAndRemoveImportedCameras();
            IsLoaded = true;
            Loaded?.Invoke(_contentRoot);
            return true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ClearLoadedAsset()
    {
        IsLoaded = false;
        if (_contentRoot == null)
        {
            return;
        }

        Destroy(_contentRoot);
        _contentRoot = null;
        _gltfAsset = null;
        HasImportedCameraPose = false;
        LoadedFromCache = false;
    }

    public GameObject DetachLoadedAsset()
    {
        GameObject contentRoot = _contentRoot;
        _contentRoot = null;
        _gltfAsset = null;
        IsLoaded = false;
        HasImportedCameraPose = false;
        LoadedFromCache = false;
        return contentRoot;
    }

    private void ExtractAndRemoveImportedCameras()
    {
        Camera sourceCamera = _contentRoot.GetComponentInChildren<Camera>(true);
        if (sourceCamera != null)
        {
            HasImportedCameraPose = true;
            ImportedCameraPosition = sourceCamera.transform.position;
            ImportedCameraRotation = sourceCamera.transform.rotation;
            ImportedCameraFieldOfView = sourceCamera.fieldOfView;
        }

        foreach (Camera camera in _contentRoot.GetComponentsInChildren<Camera>(true))
        {
            // glTFast creates a disabled camera GameObject beneath the node named in the glTF.
            // Remove that node too, so the imported hierarchy contains no camera object at all.
            GameObject cameraNode = camera.transform.parent != null
                ? camera.transform.parent.gameObject
                : camera.gameObject;
            DestroyImmediate(cameraNode);
        }
    }

    public static bool IsRemoteGltfUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && (uri.AbsolutePath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase));
    }

    public static string GetCachePath(string url)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
        string fileName = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant() + ".glb";
        return Path.Combine(Application.persistentDataPath, "RemoteGltfCache", fileName);
    }

    public void ClearCachedAsset()
    {
        if (IsGlbUrl(Url))
        {
            File.Delete(GetCachePath(Url));
        }
    }

    private async Task<string> GetLoadUrl()
    {
        if (!UseDiskCache || !IsGlbUrl(Url))
        {
            return Url;
        }

        string cachePath = GetCachePath(Url);
        if (File.Exists(cachePath))
        {
            LoadedFromCache = true;
            return new Uri(cachePath).AbsoluteUri;
        }

        string directory = Path.GetDirectoryName(cachePath);
        Directory.CreateDirectory(directory);
        string temporaryPath = cachePath + ".download";
        using var request = new UnityWebRequest(Url)
        {
            downloadHandler = new DownloadHandlerFile(temporaryPath)
        };
        UnityWebRequestAsyncOperation operation = request.SendWebRequest();
        while (!operation.isDone)
        {
            await Task.Yield();
        }

        if (request.result != UnityWebRequest.Result.Success)
        {
            File.Delete(temporaryPath);
            Error = $"Failed to download glTF from {Url}: {request.error}";
            Debug.LogError(Error, this);
            return null;
        }

        if (File.Exists(cachePath))
        {
            File.Delete(cachePath);
        }
        File.Move(temporaryPath, cachePath);
        return new Uri(cachePath).AbsoluteUri;
    }

    private static bool IsGlbUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
            && uri.AbsolutePath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
    }

    private void OnDestroy()
    {
        ClearLoadedAsset();
    }
}
