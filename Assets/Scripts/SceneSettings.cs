using PathTracing.Camera;
using PathTracing.Lighting;
using UnityEngine;

public sealed class SceneSettings
{
    public string SceneName;
    public Vector3 CameraPosition;
    public Vector3 CameraEuler;
    public CameraBehavior CameraBehavior = CameraBehavior.Free;
    public Vector3 CameraFocusPosition;
    // Zero means use the distance implied by CameraPosition.
    public float CameraOrbitZoom;
    public float FieldOfView = 60.0f;

    public int NumberOfPasses = 1;
    public float SubpixelJitterScale = 1.4f;
    public bool EnableFrameAccumulation = true;
    public bool EnableAdaptiveSampling = false;
    public int AdaptiveSamplingMinSamples = 16;
    public float AdaptiveSamplingRelativeError = 0.05f;
    public float AdaptiveSamplingAbsoluteError = 0.002f;
    public int AdaptiveSamplingMaxInterval = 8;
    public int NumBounces = 6;
    public int ShadowQuality = 0;
    public int TopLevelBvhMinObjectCount = 64;
    public int ShadowBvhMinObjectCount = 64;
    public float ShadowRandomness = 0.65f;
    public LightSamplingStrategy LightSamplingStrategy = LightSamplingStrategy.ImportanceSampled;
    public int LightSampleCount = 1;

    public bool EnableSpatialDenoising = true;
    public float DenoiserLuminanceSigma = 0.05f;
    public int DenoiserIterations = 1;

    public bool EnableCaustics = true;
    public int CausticPhotonCount = 65536;
    public float CausticGatherRadius = 0.015f;
    public int CausticSeed = 1;
    public float CausticIntensity = 1.0f;

    public bool EnableVolumetricFog = false;
    public float FogDensityScale = 1.0f;
    public float FogScatteringScale = 1.0f;
    public float FogInScatteringIntensity = 8.0f;
    public bool EnableFogMultipleScattering = false;

    public bool CameraAutoFocus = false;
    public float CameraFocalDistance = 18.0f;
    public CameraApertureMode CameraApertureMode = CameraApertureMode.LensRadius;
    public float CameraApertureRadius = 0.005f;
    public int CameraApertureBladeCount = 0;
    public float CameraApertureBladeRotation = 0.0f;
    public float CameraAnamorphicRatio = 1.0f;
    
    public float CameraMovementSpeed = 3.0f;
    public float LightFalloffScale = 0.08f;
    public float Exposure = 1.0f;
    public float FireflyClamp = 0.0f; // 0 (none), 1 (fully clamped)
    public bool RandomNoise = false;
    public Color32 SkyboxLightColor = new (95, 95, 105, 255);
    
    public bool EnableGlare = true;
    public float GlareThreshold = 1.0f;
    public float GlareSoftKnee = 0.1f;
    public float GlareIntensity = 1.0f;

    public bool EnableEnvironmentLighting = true;
    public int EnvironmentLightSampleCount = 1;
    public float EnvironmentHighlightThreshold = 0.0f;
    public float EnvironmentHighlightSoftKnee = 0.5f;
    public float EnvironmentHighlightIntensity = 0.0f;
    public int EnvironmentImportanceWidth = 512;
    public int EnvironmentImportanceHeight = 256;
    
    public float DirectionalLightIntensity = 1.0f;
    public float DirectionalLightAngularRadius = 0.27f;
    public Vector3 DirectionalLightRotation = new (50.0f, -30.0f, 0.0f);
}
