using DefaultNamespace;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class WaterManager : MonoBehaviour
{
    private static readonly int WaterEnabled = Shader.PropertyToID("_WaterEnabled");
    private static readonly int WaterCenter = Shader.PropertyToID("_WaterCenter");
    private static readonly int WaterSize = Shader.PropertyToID("_WaterSize");
    private static readonly int WaterDepth = Shader.PropertyToID("_WaterDepth");
    private static readonly int WaterColor = Shader.PropertyToID("_WaterColor");
    private static readonly int WaterSmoothness = Shader.PropertyToID("_WaterSmoothness");
    private static readonly int WaterOpacity = Shader.PropertyToID("_WaterOpacity");
    private static readonly int WaterAbsorptionStrength = Shader.PropertyToID("_WaterAbsorptionStrength");
    private static readonly int WaterRefraction = Shader.PropertyToID("_WaterRefraction");
    private static readonly int WaterWaveAmplitude = Shader.PropertyToID("_WaterWaveAmplitude");
    private static readonly int WaterWaveScale = Shader.PropertyToID("_WaterWaveScale");
    private static readonly int WaterWaveSpeed = Shader.PropertyToID("_WaterWaveSpeed");
    private static readonly int WaterTime = Shader.PropertyToID("_WaterTime");
    private static readonly int WaterMarchSteps = Shader.PropertyToID("_WaterMarchSteps");
    private static readonly int WaterRefinementSteps = Shader.PropertyToID("_WaterRefinementSteps");
    
    private Water _water;
    private GameManager _gameManager;

    public Water Water => _water;
    public bool HasWaterVolume => _water != null;

    public bool RegisterWater(Water water)
    {
        if (_water == water)
        {
            return true;
        }

        if (_water != null)
        {
            Debug.LogError(
                $"Only one active Water component is supported by WaterManager '{name}'. " +
                $"Disable '{_water.name}' before enabling '{water.name}'.",
                water);
            return false;
        }

        _water = water;
        NotifyGameManagerChanged();
        return true;
    }

    public void UnregisterWater(Water water)
    {
        if (_water == water)
        {
            _water = null;
            NotifyGameManagerChanged();
        }
    }

    private void NotifyGameManagerChanged()
    {
        if (_gameManager == null)
        {
            _gameManager = GetComponent<GameManager>();
        }

        _gameManager?.OnWaterChanged();
    }

    public void SetShaderParameters(ComputeShader shader, float time)
    {
        var waterEnabled = _water != null;
        var center = waterEnabled ? _water.TopCenter : Vector3.zero;
        var size = waterEnabled ? _water.Size : Vector2.one;
        var color = waterEnabled ? _water.Color : new Color32(255, 255, 255, 255);
        var colorVector = color.ToVector3();

        shader.SetInt(WaterEnabled, waterEnabled ? 1 : 0);
        shader.SetVector(WaterCenter, new Vector4(center.x, center.y, center.z, 0.0f));
        shader.SetVector(WaterSize, new Vector4(size.x, size.y, 0.0f, 0.0f));
        shader.SetFloat(WaterDepth, waterEnabled ? _water.Depth : 1.0f);
        shader.SetVector(WaterColor, new Vector4(colorVector.x, colorVector.y, colorVector.z, 0.0f));
        shader.SetFloat(WaterSmoothness, waterEnabled ? _water.Smoothness : 0.0f);
        shader.SetFloat(WaterOpacity, waterEnabled ? Mathf.Clamp01(_water.Opacity) : 0.0f);
        shader.SetFloat(WaterAbsorptionStrength, waterEnabled ? Mathf.Max(0.0f, _water.AbsorptionStrength) : 0.0f);
        shader.SetFloat(WaterRefraction, waterEnabled ? _water.RefractionIndex : 1.0f);
        shader.SetFloat(WaterWaveAmplitude, waterEnabled ? Mathf.Max(0.0f, _water.WaveAmplitude) : 0.0f);
        shader.SetFloat(WaterWaveScale, waterEnabled ? Mathf.Max(0.001f, _water.WaveScale) : 1.0f);
        shader.SetFloat(WaterWaveSpeed, waterEnabled ? Mathf.Max(0.0f, _water.WaveSpeed) : 0.0f);
        shader.SetFloat(WaterTime, time);
        shader.SetInt(WaterMarchSteps, waterEnabled ? Mathf.Clamp(_water.MarchSteps, 8, 64) : 8);
        shader.SetInt(WaterRefinementSteps, waterEnabled ? Mathf.Clamp(_water.RefinementSteps, 2, 8) : 2);
    }

    public int AddAccumulationStateHash(int hash)
    {
        return _water == null ? hash : _water.AddAccumulationStateHash(hash);
    }

    public int AddCausticPhotonStateHash(int hash, bool singleFrame, float renderTime)
    {
        if (_water == null)
        {
            return hash;
        }

        hash = GameManager.AddHash(hash, _water.GetInstanceID());
        hash = GameManager.AddHash(hash, _water.TopCenter);
        hash = GameManager.AddHash(hash, new Vector3(_water.Size.x, _water.Size.y, _water.Depth));
        hash = GameManager.AddHash(hash, _water.Color.r);
        hash = GameManager.AddHash(hash, _water.Color.g);
        hash = GameManager.AddHash(hash, _water.Color.b);
        hash = GameManager.AddHash(hash, _water.Smoothness);
        hash = GameManager.AddHash(hash, _water.Opacity);
        hash = GameManager.AddHash(hash, _water.AbsorptionStrength);
        hash = GameManager.AddHash(hash, _water.RefractionIndex);
        hash = GameManager.AddHash(hash, _water.WaveAmplitude);
        hash = GameManager.AddHash(hash, _water.WaveScale);
        hash = GameManager.AddHash(hash, _water.WaveSpeed);
        hash = GameManager.AddHash(hash, _water.MarchSteps);
        hash = GameManager.AddHash(hash, _water.RefinementSteps);
        if (!singleFrame && _water.WaveAmplitude > 0.0f && _water.WaveSpeed > 0.0f)
        {
            hash = GameManager.AddHash(hash, renderTime);
        }

        return hash;
    }

    public bool TryGetAutoFocusHit(Ray ray, float nearestDistance, out float hitDistance)
    {
        hitDistance = nearestDistance;
        if (_water == null || _water.Opacity >= 1.0f || Mathf.Abs(ray.direction.y) <= 0.000001f)
        {
            return false;
        }

        var center = _water.TopCenter;
        var size = _water.Size;
        var distance = (center.y - ray.origin.y) / ray.direction.y;
        var hitPoint = ray.origin + ray.direction * distance;
        var halfSize = size * 0.5f;
        if (distance <= 0.0f || distance >= nearestDistance
            || hitPoint.x < center.x - halfSize.x || hitPoint.x > center.x + halfSize.x
            || hitPoint.z < center.z - halfSize.y || hitPoint.z > center.z + halfSize.y)
        {
            return false;
        }

        hitDistance = distance;
        return true;
    }

    public bool TryGetCausticBounds(out Vector3 boundsMin, out Vector3 boundsMax)
    {
        if (_water == null)
        {
            boundsMin = boundsMax = Vector3.zero;
            return false;
        }

        var size = _water.Size;
        var waveHeight = Mathf.Max(0.001f, _water.WaveAmplitude);
        var halfSize = new Vector3(size.x * 0.5f, 0.0f, size.y * 0.5f);
        boundsMin = _water.TopCenter - halfSize + Vector3.down * (_water.Depth + waveHeight);
        boundsMax = _water.TopCenter + halfSize + Vector3.up * waveHeight;
        return true;
    }

    public bool IsAnimated => _water != null && _water.WaveAmplitude > 0.0f && _water.WaveSpeed > 0.0f;
}
