using UnityEngine;

public sealed class SceneSettings
{
    public string SceneName;
    public Vector3 CameraPosition;
    public Vector3 CameraEuler;
    public float FieldOfView = 60.0f;

    public int NumberOfPasses = 1;
    public bool EnableFrameAccumulation = true;
    public int NumBounces = 6;
    public int ShadowQuality = 0;
    public int TopLevelBvhMinObjectCount = 64;
    public int ShadowBvhMinObjectCount = 64;
    public float ShadowRandomness = 0.65f;
    public GameManager.LightSamplingStrategy LightSamplingStrategy = GameManager.LightSamplingStrategy.ImportanceSampled;
    public int LightSampleCount = 1;

    public bool EnableCaustics = false;
    public int CausticPhotonCount = 65536;
    public float CausticGatherRadius = 0.025f;
    public int CausticSeed = 1;
    public float CausticIntensity = 4.0f;

    public float FogDensityScale = 1.0f;
    public float FogScatteringScale = 1.0f;
    public float FogInScatteringIntensity = 8.0f;
    public bool EnableFogMultipleScattering = false;

    public bool CameraAutoFocus = false;
    public float CameraFocalDistance = 18.0f;
    public GameManager.CameraApertureMode CameraApertureMode = GameManager.CameraApertureMode.LensRadius;
    public float LightFalloffScale = 0.08f;
    public float Exposure = 1.0f;
    public float FireflyClamp = 1.0f;
    public bool RandomNoise = false;
    public Color32 SkyboxLightColor = new Color32(95, 95, 105, 255);
}
