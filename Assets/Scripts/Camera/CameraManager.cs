using PathTracing.Camera;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Owns the camera reference, camera controls, and camera/lens configuration.
/// GameManagerEditor intentionally draws this component from the GameManager inspector.
/// </summary>
[DisallowMultipleComponent]
public sealed class CameraManager : MonoBehaviour
{
    private const float MaxCameraPitch = 89.0f;
    public const float DefaultOrbitZoom = 2.0f;

    public Camera renderTextureCamera;

    [Tooltip("Camera movement speed in world units per second.")]
    [Min(0.01f)] 
    public float cameraMovementSpeed = 3.0f;
    
    [Tooltip("Selects how keyboard input controls the camera.")]
    public CameraBehavior cameraBehavior = CameraBehavior.Free;
    
    [Tooltip("The world-space point used by the orbit camera.")]
    public Vector3 cameraFocusPosition;
    
    [Tooltip("Distance from the orbit focus point.")]
    [Min(0.1f)] 
    public float cameraOrbitZoom = DefaultOrbitZoom;
    
    [Tooltip("Continuously focuses the center of the image.")]
    public bool cameraAutoFocus = true;
    
    [Tooltip("Left-click the rendered Game view to focus on a ray-traced surface.")]
    public bool enableClickToFocus = true;
    
    [Tooltip("Keeps a successful click-to-focus world point in focus as the camera moves.")]
    public bool trackClickedFocusPoint = true;
    
    [Range(0.0f, 1.0f)] 
    public float autoFocusTransparentOpacityThreshold = 0.5f;
    
    [Min(0.1f)] 
    public float cameraFocalDistance = 100f;
    
    public CameraApertureMode cameraApertureMode = CameraApertureMode.LensRadius;
    
    [Range(0.0f, 0.1f)] 
    public float cameraApertureRadius = 0.005f;
    
    [Range(0.7f, 32.0f)] 
    public float cameraFStop = 2.8f;
    
    [Range(0.01f, 100.0f)] 
    public float cameraApertureScale = 1.0f;
    
    [Range(0, 16)] 
    public int cameraApertureBladeCount;
    
    [Range(0.0f, 360.0f)] 
    public float cameraApertureBladeRotation;
    
    [Range(0.25f, 4.0f)] 
    public float cameraAnamorphicRatio = 1.0f;

    private CameraBehavior _activeBehavior;
    private bool _hasActiveBehavior;
    private float _orbitYaw;
    private float _orbitPitch;
    private float _orbitDistance = DefaultOrbitZoom;

    internal float PreviousFocalDistance = 100f;
    internal bool HasAutoFocusState;
    internal bool AutoFocusSceneChanged;
    internal Vector3 LastAutoFocusCameraPosition;
    internal Quaternion LastAutoFocusCameraRotation;
    internal int LastAutoFocusNumberOfPasses;
    internal int LastAutoFocusWaterStateHash;
    internal float AutoFocusTargetDistance;
    internal float TimeSincePreviousFocusDistance = 1f;
    internal bool FocusQueryPending;
    internal bool FocusQueryInFlight;
    internal bool HasClickedFocusPoint;
    internal Vector2 PendingFocusQueryUv;
    internal Vector3 FocusQueryCameraPosition;
    internal Vector3 FocusQueryCameraForward;
    internal Vector3 ClickedFocusPoint;
    internal bool ClickedFocusPointInFrustum;
    internal int FocusQueryGeneration;
    internal ComputeBuffer FocusQueryBuffer;
    internal AsyncGPUReadbackRequest FocusReadbackRequest;

    public void InitSceneSettings(SceneSettings settings)
    {
        cameraAutoFocus = settings.CameraAutoFocus;
        cameraFocalDistance = settings.CameraFocalDistance;
        cameraApertureMode = settings.CameraApertureMode;
        cameraApertureRadius = settings.CameraApertureRadius;
        cameraApertureBladeCount = settings.CameraApertureBladeCount;
        cameraApertureBladeRotation = settings.CameraApertureBladeRotation;
        cameraAnamorphicRatio = settings.CameraAnamorphicRatio;
        cameraMovementSpeed = settings.CameraMovementSpeed;
        cameraBehavior = settings.CameraBehavior;
        cameraFocusPosition = settings.CameraFocusPosition;

        if (cameraBehavior == CameraBehavior.OrbitFocusPoint && renderTextureCamera != null)
        {
            InitializeOrbitFromSceneSettings(settings);
        }
    }

    public void HandleInput()
    {
        Camera camera = renderTextureCamera;
        
        if (camera == null) return;
        
        if (!_hasActiveBehavior || _activeBehavior != cameraBehavior)
        {
            SwitchBehavior(camera);
        }
        
        if (cameraBehavior == CameraBehavior.OrbitFocusPoint)
        {
            HandleOrbitInput(camera);
            return;
        }

        float delta = Time.unscaledDeltaTime;
        
        if (Input.GetKey(KeyCode.W)) camera.transform.position += camera.transform.forward * delta * cameraMovementSpeed;
        else if (Input.GetKey(KeyCode.S)) camera.transform.position -= camera.transform.forward * delta * cameraMovementSpeed;
        
        if (Input.GetKey(KeyCode.A)) camera.transform.position -= camera.transform.right * delta * cameraMovementSpeed;
        else if (Input.GetKey(KeyCode.D)) camera.transform.position += camera.transform.right * delta * cameraMovementSpeed;

        float yaw = Input.GetKey(KeyCode.LeftArrow) ? -delta * 50.0f : Input.GetKey(KeyCode.RightArrow) ? delta * 50.0f : 0.0f;
        float pitch = Input.GetKey(KeyCode.UpArrow) ? delta * 50.0f : Input.GetKey(KeyCode.DownArrow) ? -delta * 50.0f : 0.0f;
        
        if (yaw != 0.0f || pitch != 0.0f)
        {
            Rotate(camera.transform, yaw, pitch);
        }
    }

    public void HandleFocusInput()
    {
        if (!enableClickToFocus || Input.GetMouseButtonDown(0) == false || renderTextureCamera == null)
        {
            return;
        }

        Rect pixelRect = renderTextureCamera.pixelRect;
        Vector2 mousePosition = Input.mousePosition;
        if (!pixelRect.Contains(mousePosition) || pixelRect.width <= 0.0f || pixelRect.height <= 0.0f)
        {
            return;
        }

        // The query itself remains in GameManager because it needs the renderer's scene buffers.
        FocusRequested?.Invoke(new Vector2(
            (mousePosition.x - pixelRect.x) / pixelRect.width,
            (mousePosition.y - pixelRect.y) / pixelRect.height) * 2.0f - Vector2.one);
    }

    public event System.Action<Vector2> FocusRequested;

    public float GetApertureRadius()
    {
        if (cameraApertureMode == CameraApertureMode.Pinhole) return 0.0f;
        
        if (cameraApertureMode == CameraApertureMode.LensRadius) return Mathf.Max(0.0f, cameraApertureRadius);
        
        if (renderTextureCamera == null) return 0.0f;
        
        var focalLength = Mathf.Max(0.0f, renderTextureCamera.focalLength) * 0.001f;
        return focalLength / (2.0f * Mathf.Max(0.7f, cameraFStop)) * Mathf.Max(0.0f, cameraApertureScale);
    }

    internal int AddAccumulationStateHash(int hash, bool trackedFocusPointOutsideFrustum)
    {
        hash = GameManager.AddHash(hash, cameraFocalDistance);
        hash = GameManager.AddHash(hash, (int)cameraApertureMode);
        hash = GameManager.AddHash(hash, cameraApertureRadius);
        hash = GameManager.AddHash(hash, cameraFStop);
        hash = GameManager.AddHash(hash, cameraApertureScale);
        hash = GameManager.AddHash(hash, cameraApertureBladeCount);
        hash = GameManager.AddHash(hash, cameraApertureBladeRotation);
        hash = GameManager.AddHash(hash, cameraAnamorphicRatio);
        hash = GameManager.AddHash(hash, trackedFocusPointOutsideFrustum ? 1 : 0);
        hash = GameManager.AddHash(hash, renderTextureCamera.cameraToWorldMatrix);
        return GameManager.AddHash(hash, renderTextureCamera.projectionMatrix);
    }

    public void SetAspect(float aspect)
    {
        if (renderTextureCamera != null) renderTextureCamera.aspect = aspect;
    }

    public void InitializeOrbitFromSceneSettings(SceneSettings settings)
    {
        if (renderTextureCamera == null) return;
        
        bool hasPosition = settings.CameraPosition != Vector3.zero;
        bool hasZoom = settings.CameraOrbitZoom > 0.0f;
        
        if (hasPosition)
        {
            renderTextureCamera.transform.position = settings.CameraPosition;
            
            Vector3 forward = cameraFocusPosition - settings.CameraPosition;
            if (forward.sqrMagnitude > 0.0001f)
            {
                renderTextureCamera.transform.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
            }
            cameraOrbitZoom = hasZoom ? settings.CameraOrbitZoom : Vector3.Distance(settings.CameraPosition, cameraFocusPosition);
        }
        else if (hasZoom)
        {
            cameraOrbitZoom = settings.CameraOrbitZoom;
        }
        else
        {
            cameraOrbitZoom = DefaultOrbitZoom;
        }
        
        _orbitDistance = Mathf.Max(0.1f, cameraOrbitZoom);
        renderTextureCamera.transform.position = cameraFocusPosition - renderTextureCamera.transform.forward * _orbitDistance;
        renderTextureCamera.transform.LookAt(cameraFocusPosition);
        cameraFocalDistance = _orbitDistance;
    }

    public void SetOrbitFocus(Vector3 focusPosition)
    {
        cameraFocusPosition = focusPosition;
        _orbitDistance = DefaultOrbitZoom;
        cameraOrbitZoom = _orbitDistance;
        InitializeOrbit(renderTextureCamera);
    }

    private void SwitchBehavior(Camera camera)
    {
        _activeBehavior = cameraBehavior;
        _hasActiveBehavior = true;
        
        if (cameraBehavior == CameraBehavior.OrbitFocusPoint)
        {
            cameraFocusPosition = camera.transform.position + camera.transform.forward * DefaultOrbitZoom;
            cameraOrbitZoom = DefaultOrbitZoom;
            _orbitDistance = DefaultOrbitZoom;
            InitializeOrbit(camera);
        }
    }

    private void InitializeOrbit(Camera camera)
    {
        Vector3 offset = camera.transform.position - cameraFocusPosition;
        if (offset.sqrMagnitude < 0.0001f)
        {
            offset = -camera.transform.forward * DefaultOrbitZoom;
        }
        _orbitDistance = Mathf.Max(0.1f, cameraOrbitZoom);
        _orbitYaw = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
        _orbitPitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(offset.y / offset.magnitude, -1.0f, 1.0f)) * Mathf.Rad2Deg, -MaxCameraPitch, MaxCameraPitch);
    }

    private void HandleOrbitInput(Camera camera)
    {
        float delta = Time.unscaledDeltaTime;
        float scale = Mathf.Max(0.0f, cameraMovementSpeed);
        float angle = delta * 20.0f * scale;
        
        if (Input.GetKey(KeyCode.A)) _orbitYaw -= angle;
        if (Input.GetKey(KeyCode.D)) _orbitYaw += angle;
        if (Input.GetKey(KeyCode.W)) _orbitPitch += angle;
        if (Input.GetKey(KeyCode.S)) _orbitPitch -= angle;
        
        _orbitPitch = Mathf.Clamp(_orbitPitch, -MaxCameraPitch, MaxCameraPitch);
        
        if (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.E)) _orbitDistance = Mathf.Max(0.1f, _orbitDistance - delta * scale);
        if (Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.Q)) _orbitDistance += delta * scale;
        
        cameraOrbitZoom = _orbitDistance;
        
        var offset = new Vector3(Mathf.Sin(_orbitYaw * Mathf.Deg2Rad) * Mathf.Cos(_orbitPitch * Mathf.Deg2Rad), Mathf.Sin(_orbitPitch * Mathf.Deg2Rad), Mathf.Cos(_orbitYaw * Mathf.Deg2Rad) * Mathf.Cos(_orbitPitch * Mathf.Deg2Rad)) * _orbitDistance;
        camera.transform.position = cameraFocusPosition + offset;
        camera.transform.LookAt(cameraFocusPosition);
    }

    private static void Rotate(Transform transform, float yawDelta, float pitchDelta)
    {
        var euler = transform.eulerAngles;
        euler.x = Mathf.Clamp(Mathf.DeltaAngle(0.0f, euler.x) + pitchDelta, -MaxCameraPitch, MaxCameraPitch);
        euler.y += yawDelta;
        transform.eulerAngles = euler;
    }
}
