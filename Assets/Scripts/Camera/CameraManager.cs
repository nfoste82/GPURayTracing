using System;
using PathTracing;
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
    private const float DefaultOrbitZoom = 2.0f;
    
    private static readonly int CameraToWorld = Shader.PropertyToID("_CameraToWorld");
    private static readonly int CameraInverseProjection = Shader.PropertyToID("_CameraInverseProjection");
    private static readonly int FocalDistance = Shader.PropertyToID("_FocalDistance");
    private static readonly int ApertureRadius = Shader.PropertyToID("_ApertureRadius");
    private static readonly int ApertureBladeCount = Shader.PropertyToID("_ApertureBladeCount");
    private static readonly int ApertureBladeRotation = Shader.PropertyToID("_ApertureBladeRotation");
    private static readonly int AnamorphicRatio = Shader.PropertyToID("_AnamorphicRatio");
    private static readonly int FocusQueryUv = Shader.PropertyToID("_FocusQueryUv");
    private static readonly int FocusQueryResult = Shader.PropertyToID("_FocusQueryResult");

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

    [Tooltip("World-space orbit dolly speed. Set by model browsers to match the loaded model size.")]
    [Min(0.01f)]
    public float cameraOrbitZoomSpeed = 1.0f;
    
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
    
    [Range(0.0f, 0.2f)] 
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
    private float _lastSerializedOrbitZoom = DefaultOrbitZoom;

    private float PreviousFocalDistance = 100f;
    private bool HasAutoFocusState;
    internal bool AutoFocusSceneChanged;
    private Vector3 LastAutoFocusCameraPosition;
    private Quaternion LastAutoFocusCameraRotation;
    private int LastAutoFocusNumberOfPasses;
    private int LastAutoFocusWaterStateHash;
    private float AutoFocusTargetDistance;
    private float TimeSincePreviousFocusDistance = 1f;
    internal bool FocusQueryPending;
    internal bool FocusQueryInFlight;
    private bool HasClickedFocusPoint;
    internal Vector2 PendingFocusQueryUv;
    private Vector3 FocusQueryCameraPosition;
    private Vector3 FocusQueryCameraForward;
    private Vector3 ClickedFocusPoint;
    private bool ClickedFocusPointInFrustum;
    private int FocusQueryGeneration;
    private ComputeBuffer FocusQueryBuffer;
    private AsyncGPUReadbackRequest FocusReadbackRequest;
    private int FocusQueryDispatchGeneration;
    private Action ResetFocusAccumulation;
    private Action<AsyncGPUReadbackRequest> FocusReadbackCallback;

    private void Awake()
    {
        FocusReadbackCallback = CompleteFocusQuery;
    }

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

        if (cameraBehavior == CameraBehavior.OrbitFocusPoint)
        {
            InitializeOrbitFromSceneSettings(settings);
        }
    }

    private void OnDestroy()
    {
        if (FocusQueryInFlight)
        {
            FocusReadbackRequest.WaitForCompletion();
        }
        FocusQueryGeneration++;
        
        FocusQueryBuffer?.Release();
        FocusQueryBuffer = null;
    }

    public void HandleInput()
    {
        var camera = renderTextureCamera;
        
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

        var delta = Time.unscaledDeltaTime;
        
        if (Input.GetKey(KeyCode.W))
        {
            camera.transform.position += camera.transform.forward * delta * cameraMovementSpeed;
        }
        else if (Input.GetKey(KeyCode.S))
        {
            camera.transform.position -= camera.transform.forward * delta * cameraMovementSpeed;
        }
        
        if (Input.GetKey(KeyCode.A))
        {
            camera.transform.position -= camera.transform.right * delta * cameraMovementSpeed;
        }
        else if (Input.GetKey(KeyCode.D))
        {
            camera.transform.position += camera.transform.right * delta * cameraMovementSpeed;
        }

        var yaw = Input.GetKey(KeyCode.LeftArrow) ? -delta * 50.0f : Input.GetKey(KeyCode.RightArrow) ? delta * 50.0f : 0.0f;
        var pitch = Input.GetKey(KeyCode.UpArrow) ? delta * 50.0f : Input.GetKey(KeyCode.DownArrow) ? -delta * 50.0f : 0.0f;
        
        if (yaw != 0.0f || pitch != 0.0f)
        {
            Rotate(camera.transform, yaw, pitch);
        }
    }

    public void HandleFocusInput()
    {
        int focusMouseButton = cameraBehavior == CameraBehavior.OrbitFocusPoint ? 1 : 0;
        if (!enableClickToFocus || Input.GetMouseButtonDown(focusMouseButton) == false || renderTextureCamera == null)
        {
            return;
        }

        var pixelRect = renderTextureCamera.pixelRect;
        var mousePosition = Input.mousePosition;
        
        if (!pixelRect.Contains(mousePosition) || pixelRect.width <= 0.0f || pixelRect.height <= 0.0f)
        {
            return;
        }

        FocusRequested?.Invoke(new Vector2(
            (mousePosition.x - pixelRect.x) / pixelRect.width,
            (mousePosition.y - pixelRect.y) / pixelRect.height) * 2.0f - Vector2.one);
    }

    public event Action<Vector2> FocusRequested;

    public float GetApertureRadius()
    {
        if (cameraApertureMode == CameraApertureMode.Pinhole || IsTrackedFocusPointOutsideFrustum())
        {
            return 0.0f;
        }
        
        if (cameraApertureMode == CameraApertureMode.LensRadius)
        {
            return Mathf.Max(0.0f, cameraApertureRadius);
        }
        
        if (renderTextureCamera == null)
        {
            return 0.0f;
        }
        
        var focalLength = Mathf.Max(0.0f, renderTextureCamera.focalLength) * 0.001f;
        return focalLength / (2.0f * Mathf.Max(0.1f, cameraFStop)) * Mathf.Max(0.0f, cameraApertureScale);
    }

    public void SetShaderParameters(ComputeShader shader)
    {
        shader.SetMatrix(CameraToWorld, renderTextureCamera.cameraToWorldMatrix);
        shader.SetMatrix(CameraInverseProjection, renderTextureCamera.projectionMatrix.inverse);
        shader.SetFloat(FocalDistance, cameraFocalDistance);
        shader.SetFloat(ApertureRadius, GetApertureRadius());
        shader.SetInt(ApertureBladeCount, cameraApertureBladeCount >= 3 ? cameraApertureBladeCount : 0);
        shader.SetFloat(ApertureBladeRotation, cameraApertureBladeRotation * Mathf.Deg2Rad);
        shader.SetFloat(AnamorphicRatio, Mathf.Clamp(cameraAnamorphicRatio, 0.25f, 4.0f));
    }

    public void UpdateTrackedFocusPoint()
    {
        if (!enableClickToFocus || !trackClickedFocusPoint || !HasClickedFocusPoint || renderTextureCamera == null)
        {
            HasClickedFocusPoint = false;
            ClickedFocusPointInFrustum = false;
            return;
        }

        var viewportPoint = renderTextureCamera.WorldToViewportPoint(ClickedFocusPoint);
        
        ClickedFocusPointInFrustum = viewportPoint.z >= renderTextureCamera.nearClipPlane
            && viewportPoint.z <= renderTextureCamera.farClipPlane
            && viewportPoint.x >= 0.0f && viewportPoint.x <= 1.0f
            && viewportPoint.y >= 0.0f && viewportPoint.y <= 1.0f;

        if (!ClickedFocusPointInFrustum)
        {
            return;
        }

        if (cameraBehavior == CameraBehavior.OrbitFocusPoint)
        {
            cameraFocalDistance = DefaultOrbitZoom;
            PreviousFocalDistance = cameraFocalDistance;
            return;
        }

        cameraFocalDistance = Mathf.Max(
            0.1f,
            Vector3.Dot(ClickedFocusPoint - renderTextureCamera.transform.position, renderTextureCamera.transform.forward));
        PreviousFocalDistance = cameraFocalDistance;
    }

    public void UpdateAutoFocus(int numberOfPasses, int waterStateHash, Func<Ray, float> nearestIntersectionDistance)
    {
        if (!cameraAutoFocus)
        {
            HasAutoFocusState = false;
            return;
        }

        var cameraTransform = renderTextureCamera.transform;
        var inputsChanged = !HasAutoFocusState
                            || AutoFocusSceneChanged
                            || LastAutoFocusCameraPosition != cameraTransform.position
                            || LastAutoFocusCameraRotation != cameraTransform.rotation
                            || LastAutoFocusNumberOfPasses != numberOfPasses
                            || LastAutoFocusWaterStateHash != waterStateHash;

        if (inputsChanged)
        {
            AutoFocusTargetDistance = nearestIntersectionDistance(
                new Ray(cameraTransform.position, cameraTransform.forward));
            if (AutoFocusTargetDistance < 1.0f)
            {
                var modifier = Mathf.Lerp(1.75f, 1.0f, AutoFocusTargetDistance);
                AutoFocusTargetDistance = Mathf.Max(AutoFocusTargetDistance * modifier, 0.1f);
            }

            LastAutoFocusCameraPosition = cameraTransform.position;
            LastAutoFocusCameraRotation = cameraTransform.rotation;
            LastAutoFocusNumberOfPasses = numberOfPasses;
            LastAutoFocusWaterStateHash = waterStateHash;
            HasAutoFocusState = true;
        }

        cameraFocalDistance = Mathf.Lerp(
            PreviousFocalDistance,
            AutoFocusTargetDistance,
            Mathf.SmoothStep(0.0f, 1.0f, TimeSincePreviousFocusDistance));

        if (Mathf.Abs(cameraFocalDistance - AutoFocusTargetDistance) < 0.05f)
        {
            PreviousFocalDistance = cameraFocalDistance;
            TimeSincePreviousFocusDistance = 0.0f;
        }
        else
        {
            TimeSincePreviousFocusDistance += Time.unscaledDeltaTime;
        }
    }

    public bool IsTrackedFocusPointOutsideFrustum()
    {
        return enableClickToFocus
            && trackClickedFocusPoint
            && HasClickedFocusPoint
            && !ClickedFocusPointInFrustum;
    }
    
    public void SetAspect(float aspect)
    {
        if (renderTextureCamera != null) renderTextureCamera.aspect = aspect;
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
    
    internal void DispatchPendingFocusQuery(ComputeShader shader, Action<int> setShaderParameters, Action resetFrameAccumulation)
    {
        if (!FocusQueryPending || FocusQueryInFlight)
        {
            return;
        }

        FocusQueryBuffer ??= new ComputeBuffer(1, sizeof(float) * 4);

        var kernel = shader.FindKernel("CSFocusQuery");
        setShaderParameters(kernel);
        
        shader.SetVector(FocusQueryUv, PendingFocusQueryUv);
        shader.SetBuffer(kernel, FocusQueryResult, FocusQueryBuffer);
        ComputeDispatch.Dispatch(shader, kernel, 1, 1, 1);

        FocusQueryPending = false;
        FocusQueryInFlight = true;
        FocusQueryCameraPosition = renderTextureCamera.transform.position;
        FocusQueryCameraForward = renderTextureCamera.transform.forward;

        FocusQueryDispatchGeneration = FocusQueryGeneration;
        ResetFocusAccumulation = resetFrameAccumulation;
        FocusReadbackRequest = AsyncGPUReadback.Request(
            FocusQueryBuffer,
            FocusReadbackCallback);
    }

    private void CompleteFocusQuery(AsyncGPUReadbackRequest request)
    {
        if (FocusQueryDispatchGeneration != FocusQueryGeneration)
        {
            return;
        }

        FocusQueryInFlight = false;
        if (request.hasError)
        {
            Debug.LogWarning("GPU click-to-focus readback failed.", this);
            return;
        }

        var result = request.GetData<Vector4>()[0];
        if (result.w < 0.5f)
        {
            return;
        }

        var hitPosition = new Vector3(result.x, result.y, result.z);
        var focusDistance = Vector3.Dot(hitPosition - FocusQueryCameraPosition, FocusQueryCameraForward);
        if (focusDistance <= 0.0f)
        {
            return;
        }

        cameraAutoFocus = false;
        if (cameraBehavior == CameraBehavior.OrbitFocusPoint)
        {
            SetOrbitFocus(hitPosition);
        }
        cameraFocalDistance = cameraBehavior == CameraBehavior.OrbitFocusPoint
            ? _orbitDistance
            : Mathf.Max(0.1f, focusDistance);
        PreviousFocalDistance = cameraFocalDistance;
        ClickedFocusPoint = hitPosition;
        HasClickedFocusPoint = enableClickToFocus && trackClickedFocusPoint;
        UpdateTrackedFocusPoint();
        ResetFocusAccumulation?.Invoke();
    }

    private void InitializeOrbitFromSceneSettings(SceneSettings settings)
    {
        if (renderTextureCamera == null)
        {
            return;
        }
        
        var hasPosition = settings.CameraPosition != Vector3.zero;
        var hasZoom = settings.CameraOrbitZoom > 0.0f;
        
        if (hasPosition)
        {
            renderTextureCamera.transform.position = settings.CameraPosition;
            
            var forward = cameraFocusPosition - settings.CameraPosition;
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
        _lastSerializedOrbitZoom = _orbitDistance;
        renderTextureCamera.transform.position = cameraFocusPosition - renderTextureCamera.transform.forward * _orbitDistance;
        renderTextureCamera.transform.LookAt(cameraFocusPosition);
        cameraFocalDistance = _orbitDistance;
    }

    private void SetOrbitFocus(Vector3 focusPosition)
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

    private void InitializeOrbit(Camera rayCamera)
    {
        var offset = rayCamera.transform.position - cameraFocusPosition;
        if (offset.sqrMagnitude < 0.0001f)
        {
            offset = -rayCamera.transform.forward * DefaultOrbitZoom;
        }
        _orbitDistance = Mathf.Max(0.1f, cameraOrbitZoom);
        _lastSerializedOrbitZoom = _orbitDistance;
        _orbitYaw = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
        _orbitPitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(offset.y / offset.magnitude, -1.0f, 1.0f)) * Mathf.Rad2Deg, -MaxCameraPitch, MaxCameraPitch);
    }

    private void HandleOrbitInput(Camera camera)
    {
        var delta = Time.unscaledDeltaTime;
        var scale = Mathf.Max(0.0f, cameraMovementSpeed);
        var angle = delta * 20.0f * scale;
        
        if (Input.GetKey(KeyCode.A)) _orbitYaw -= angle;
        if (Input.GetKey(KeyCode.D)) _orbitYaw += angle;
        if (Input.GetKey(KeyCode.W)) _orbitPitch += angle;
        if (Input.GetKey(KeyCode.S)) _orbitPitch -= angle;
        
        _orbitPitch = Mathf.Clamp(_orbitPitch, -MaxCameraPitch, MaxCameraPitch);

        // Inspector edits occur outside the orbit input path. Use the serialized
        // value before applying keyboard or mouse-wheel zoom for this frame.
        if (!Mathf.Approximately(cameraOrbitZoom, _lastSerializedOrbitZoom))
        {
            _orbitDistance = Mathf.Max(0.1f, cameraOrbitZoom);
        }
        
        float dollySpeed = Mathf.Max(0.01f, cameraOrbitZoomSpeed);
        if (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.E)) _orbitDistance = Mathf.Max(0.1f, _orbitDistance - delta * dollySpeed);
        if (Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.Q)) _orbitDistance += delta * dollySpeed;
        float scroll = Input.mouseScrollDelta.y;
        if (!Mathf.Approximately(scroll, 0.0f))
        {
            _orbitDistance = Mathf.Max(0.1f, _orbitDistance - scroll * Mathf.Max(0.1f, _orbitDistance) * 0.15f);
        }
        
        cameraOrbitZoom = _orbitDistance;
        _lastSerializedOrbitZoom = _orbitDistance;
        
        var offset = new Vector3(Mathf.Sin(_orbitYaw * Mathf.Deg2Rad) * Mathf.Cos(_orbitPitch * Mathf.Deg2Rad), Mathf.Sin(_orbitPitch * Mathf.Deg2Rad), Mathf.Cos(_orbitYaw * Mathf.Deg2Rad) * Mathf.Cos(_orbitPitch * Mathf.Deg2Rad)) * _orbitDistance;
        camera.transform.position = cameraFocusPosition + offset;
        camera.transform.LookAt(cameraFocusPosition);
    }

    public void SetOrbitState(Vector3 focusPosition, Vector3 cameraPosition)
    {
        if (renderTextureCamera == null)
        {
            return;
        }

        cameraBehavior = CameraBehavior.OrbitFocusPoint;
        cameraFocusPosition = focusPosition;
        cameraOrbitZoom = Mathf.Max(0.1f, Vector3.Distance(cameraPosition, focusPosition));
        renderTextureCamera.transform.position = cameraPosition;
        renderTextureCamera.transform.LookAt(focusPosition);
        _activeBehavior = cameraBehavior;
        _hasActiveBehavior = true;
        InitializeOrbit(renderTextureCamera);
        cameraFocalDistance = _orbitDistance;
    }

    private static void Rotate(Transform transform, float yawDelta, float pitchDelta)
    {
        var euler = transform.eulerAngles;
        euler.x = Mathf.Clamp(Mathf.DeltaAngle(0.0f, euler.x) + pitchDelta, -MaxCameraPitch, MaxCameraPitch);
        euler.y += yawDelta;
        transform.eulerAngles = euler;
    }
}
