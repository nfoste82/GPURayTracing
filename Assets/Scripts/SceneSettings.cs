using PathTracing.Camera;
using PathTracing.Lighting;
using PathTracing.Sampling;
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
    public int SobolDimensionLimit = 328;
    public int SamplingSeed = 1;

    public bool EnablePathGuiding = false;
    public float PathGuidingMixtureWeight = 0.5f;
    public int PathGuidingMinimumSamples = 32;

    public bool EnableAdaptiveSampling = true;
    public int AdaptiveSamplingMinSamples = 2;
    public bool EnableAdaptiveBootstrap = false;
    public int AdaptiveBootstrapFrames = 96;
    public float AdaptiveBootstrapResolutionScale = 0.5f;
    public int AdaptiveGuidanceMaxUpdates = 2;
    public int AdaptiveGuidanceHistoryFrames = 2;
    public float AdaptiveLuminanceErrorWeight = -2.0f;
    public float AdaptiveSpatialDisagreementPriority = 4.0f;
    public int AdaptiveReclassificationInterval = 4;
    public float AdaptiveHighestBucketSampleRate = 3.0f;
    public int AdaptiveMaxPathsPerPixel = 4;
    public int AdaptiveBootstrapGroupDivisor = 16;

    public int NumBounces = 6;
    public int ShadowQuality = 0;
    public int TopLevelBvhMinObjectCount = 64;
    public int ShadowBvhMinObjectCount = 64;
    public float ShadowRandomness = 0.65f;
    public LightSamplingStrategy LightSamplingStrategy = LightSamplingStrategy.ImportanceSampled;
    public int LightSampleCount = 1;
    // Four candidates is the project default; scene-specific settings can override it.
    public int InitialRisCandidateCount = 4;
    // Experimental and incomplete; local RIS is the default production path.
    public bool TemporalRisEnabled = false;
    public int TemporalRisHistoryMCap = 1;
    // Experimental and incomplete; local RIS is the default production path.
    public bool SpatialRisEnabled = false;
    public int SpatialRisNeighborCount = 4;

    public bool EnableSpatialDenoising = true;
    public float DenoiserLuminanceSigma = 0.05f;
    public int DenoiserIterations = 1;

    public bool EnableCaustics = true;
    public int CausticPhotonCount = 131072;
    public float CausticGatherRadius = 0.025f;
    public float CausticGatherRadiusDecayRate = 0.35f;
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
