﻿using System;
using System.Collections.Generic;
using System.Linq;
using DefaultNamespace;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    public ComputeShader shader;
    public Camera renderTextureCamera;
    
    [Header("Quality settings (Higher quality -> Slower)")]
    [Range(1, 32)]
    public int numberOfPasses = 1;

    [Range(0, 5)]
    public int shadowQuality = 2;

    [Range(0f, 1.5f)] 
    public float shadowRandomness = 0.3f;

    [Header("Misc settings")]
    public bool cameraAutoFocus = true;
    
    [Range(0.1f, 100f)]
    public float cameraFocalDistance = 100f;

    [Range(0.6f, 1.0f)]
    public float groundSmoothness = 0.98f;

    private float previousFocalDistance = 100f;
    private float timeSincePreviousFocusDistance = 1f;

    public bool randomNoise = false;
    
    public Color _ambientLightColor;
    public Color32 _skyboxLightColor = new Color32(123, 107, 101, 255);
    
    public Texture skyboxTexture;
    public Texture checkerboardTexture;

    private Vector4 _ambientLightColorAsVector;
    private Vector4 _skyboxLightColorAsVector;
    
    private RenderTexture _outputTexture;
    private Vector2Int _textureSize;
    
    private List<Sphere> _spheres = new List<Sphere>();
    private List<GameObject> _sphereObjects = new List<GameObject>();
    private ComputeBuffer _sphereBuffer;

    private List<Sphere> _lights = new List<Sphere>();
    private List<GameObject> _lightObjects = new List<GameObject>();
    private ComputeBuffer _lightBuffer;
    
    [Header("Render single frame")] 
    public bool _singleFrame = false;

    private bool _running = true;
    
    private static bool _buffersNeedRebuilding = false;
    private static readonly List<RayTracingObject> _rayTracingObjects = new List<RayTracingObject>();
    private static readonly List<MeshObject> _meshObjects = new List<MeshObject>();
    private static readonly List<Vector3> _vertices = new List<Vector3>();
    private static readonly List<int> _indices = new List<int>();
    
    private ComputeBuffer _meshObjectBuffer;
    private ComputeBuffer _vertexBuffer;
    private ComputeBuffer _indexBuffer;
    
    private struct MeshObject
    {
        public Matrix4x4 localToWorldMatrix;
        public int indices_offset;
        public int indices_count;
    }

    private struct Sphere
    {
        public Vector3 position;
        public Vector3 color;
        public Vector3 emission;
        public float radius;
        public float smoothness;
        public float opacity;
        public float refraction;
        
        public float Intersect(Vector3 origin, Vector3 direction)
        {
            var diffToSphere = position - origin;
            var b = Vector3.Dot(diffToSphere, direction);

            // ray is pointing away from sphere (b < 0)
            if (b < 0f)
            {
                return -1.0f;
            }
            
            var c = diffToSphere.sqrMagnitude - radius * radius;

            var discriminant = (b * b) - c; 

            // A negative discriminant corresponds to ray missing sphere 
            if (discriminant < 0.0f)
            {
                return -1.0f;
            } 

            // Ray now found to intersect sphere, compute smallest t value of intersection
            var hitDistance = b - Mathf.Sqrt(discriminant) - 0.001f;

            // If hit distance is negative, ray started inside sphere so clamp it to zero
            if (hitDistance < 0.0f)
            {
                hitDistance = 0.0f;
            }

            return hitDistance;
        }
    }
    
    private void Start()
    {
        _textureSize = new Vector2Int(Screen.width, Screen.height);
        _outputTexture = new RenderTexture(_textureSize.x, _textureSize.y, 24)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Point
        };
        _outputTexture.Create();
    }

    private void Update()
    {
        if (_buffersNeedRebuilding)
        {
            RebuildBuffers();
        }
        
        HandleInputForCamera(renderTextureCamera);

        if (Input.GetKeyDown(KeyCode.T))
        {
            _singleFrame = !_singleFrame;
            if (!_singleFrame)
            {
                _running = true;
                EnableRealtimeSettings();
            }
            else
            {
                _running = false;
                EnableSingleFrameSettings();
            }
        }

        if (Input.GetKeyDown(KeyCode.Space))
        {
            _running = true;
            EnableRealtimeSettings();
        }
    }

    private void EnableSingleFrameSettings()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 10;
        Time.timeScale = 0.0f;
    }

    private void EnableRealtimeSettings()
    {
        QualitySettings.vSyncCount = 2;
        Application.targetFrameRate = 60;
        Time.timeScale = 1.0f;
    }
    
    private void HandleInputForCamera(Camera camera)
    {
        if (Input.GetKey(KeyCode.W))
        {
            camera.transform.position += camera.transform.forward * Time.deltaTime * 3f;
        }
        else if (Input.GetKey(KeyCode.S))
        {
            camera.transform.position -= camera.transform.forward * Time.deltaTime * 3f;
        }
        
        if (Input.GetKey(KeyCode.A))
        {
            camera.transform.position -= camera.transform.right * Time.deltaTime * 3f;
        }
        else if (Input.GetKey(KeyCode.D))
        {
            camera.transform.position += camera.transform.right * Time.deltaTime * 3f;
        }
        
        if (Input.GetKey(KeyCode.LeftArrow))
        {
            camera.transform.eulerAngles += new Vector3(0f, -Time.deltaTime * 50f, 0f);
        }
        else if (Input.GetKey(KeyCode.RightArrow))
        {
            camera.transform.eulerAngles += new Vector3(0f, Time.deltaTime * 50f, 0f);
        }
        
        if (Input.GetKey(KeyCode.UpArrow))
        {
            camera.transform.eulerAngles += new Vector3(Time.deltaTime * 50f, 0f, 0f);
        }
        else if (Input.GetKey(KeyCode.DownArrow))
        {
            camera.transform.eulerAngles += new Vector3(-Time.deltaTime * 50f, 0f, 0f);
        }
    }
    
    private void OnDestroy()
    {
        _sphereBuffer?.Release();
        _lightBuffer?.Release();
    }

    private void UpdateTextureFromCompute(int kernelHandle)
    {
        shader.SetTexture(kernelHandle, "Result", _outputTexture);
        int threadGroupsX = Mathf.CeilToInt(_textureSize.x / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(_textureSize.y / 8.0f);
        shader.Dispatch(kernelHandle, threadGroupsX, threadGroupsY, 1);
    }
    
    private void OnRenderImage(RenderTexture src, RenderTexture dest)
    {
        if (_running)
        {
            var autoFocusDistance = (cameraAutoFocus)
                ? GetNearestIntersectionDistanceForAutoFocus(new Ray(renderTextureCamera.transform.position,
                    renderTextureCamera.transform.forward))
                : cameraFocalDistance;

            if (cameraAutoFocus && autoFocusDistance < 1.0f)
            {
                var modifier = Mathf.Lerp(1.75f, 1.0f, autoFocusDistance);
                autoFocusDistance *= modifier;

                autoFocusDistance = Mathf.Max(autoFocusDistance, 0.1f);
                float targetFocusDistance = autoFocusDistance;

                autoFocusDistance = Mathf.Lerp(previousFocalDistance, autoFocusDistance,
                    Mathf.SmoothStep(0.0f, 1.0f, timeSincePreviousFocusDistance));

                if (Mathf.Abs(autoFocusDistance - targetFocusDistance) < 0.05f)
                {
                    previousFocalDistance = autoFocusDistance;
                    timeSincePreviousFocusDistance = 0.0f;
                }
                else
                {
                    timeSincePreviousFocusDistance += Time.deltaTime;
                }
            }

            cameraFocalDistance = autoFocusDistance;

            UpdateSpheres();

            var kernelHandle = shader.FindKernel("CSMain");

            SetShaderParameters(kernelHandle);
            UpdateTextureFromCompute(kernelHandle);
            
            if (_singleFrame)
            {
                _running = false;
                EnableSingleFrameSettings();
            }
        }

        Graphics.Blit(_outputTexture, dest);
    }

    private void UpdateSpheres()
    {
        for (int i = 0; i < _spheres.Count; ++i)
        {
            var sphere = _spheres[i];

            sphere.position = _sphereObjects[i].transform.position;

            sphere.radius = _sphereObjects[i].GetComponent<SphereCollider>().radius;
            
            var material = _sphereObjects[i].GetComponent<RayMaterial>();
            sphere.color = material.Color.ToVector3();
            sphere.refraction = material.RefractionIndex;
            sphere.opacity = material.Opacity;
            sphere.smoothness = material.Smoothness;
            
            _spheres[i] = sphere;
        }
        
        for (int i = 0; i < _lights.Count; ++i)
        {
            var sphere = _lights[i];

            sphere.position = _lightObjects[i].transform.position;
            
            sphere.radius = _lightObjects[i].GetComponent<SphereCollider>().radius;
            
            var light = _lightObjects[i].GetComponent<RayLight>();
            sphere.emission = light.Color.ToVector3();
            
            _lights[i] = sphere;
        }

        _sphereBuffer.SetData(_spheres);
        _lightBuffer.SetData(_lights);
    }

    public void RebuildBuffers()
    {
        _sphereBuffer?.Release();
        _lightBuffer?.Release();

        if (_spheres.Count > 0)
        {
            shader.SetInt("_NumSpheres", _spheres.Count);
            
            _sphereBuffer = new ComputeBuffer(_spheres.Count, 52);
            _sphereBuffer.SetData(_spheres);
        }

        if (_lights.Count > 0)
        {
            shader.SetInt("_NumLights", _lights.Count);
            
            _lightBuffer = new ComputeBuffer(_lights.Count, 52);
            _lightBuffer.SetData(_lights);
        }
    }

    public void RegisterObject(RayTracingObject obj)
    {
        _rayTracingObjects.Add(obj);
        _buffersNeedRebuilding = true;

        var material = obj.GetComponent<RayMaterial>();

        if (material != null)
        {
            var sphere = new Sphere
            {
                position = obj.transform.position,
                color = material.Color.ToVector3(),
                smoothness = material.Smoothness,
                radius = obj.GetComponent<SphereCollider>().radius,
                opacity = material.Opacity,
                refraction = material.RefractionIndex,
            };
            _spheres.Add(sphere);
            _sphereObjects.Add(obj.gameObject);
        }
        else
        {
            var rayLight = obj.GetComponent<RayLight>();
            
            var sphere = new Sphere
            {
                position = obj.transform.position,
                radius = obj.GetComponent<SphereCollider>().radius,
                emission = rayLight.Color.ToVector3()
            };
            _lights.Add(sphere);
            _lightObjects.Add(obj.gameObject);
        }
    }
    
    public void UnregisterObject(RayTracingObject obj)
    {
        _rayTracingObjects.Remove(obj);
        _buffersNeedRebuilding = true;
        
        // TODO: Implement me (need a mapping of GameObject -> Sphere/Light)
        
    }

//    private void RebuildMeshObjectBuffers()
//    {
//        if (!_meshObjectsNeedRebuilding)
//        {
//            return;
//        }
//
//        _meshObjectsNeedRebuilding = false;
//        //_currentSample = 0;
//
//        // Clear all lists
//        _meshObjects.Clear();
//        _vertices.Clear();
//        _indices.Clear();
//
//        // Loop over all objects and gather their data
//        foreach (RayTracingObject obj in _rayTracingObjects)
//        {
//            Mesh mesh = obj.GetComponent<MeshFilter>().sharedMesh;
//
//            // Add vertex data
//            int firstVertex = _vertices.Count;
//            _vertices.AddRange(mesh.vertices);
//
//            // Add index data - if the vertex buffer wasn't empty before, the
//            // indices need to be offset
//            int firstIndex = _indices.Count;
//            var indices = mesh.GetIndices(0);
//            _indices.AddRange(indices.Select(index => index + firstVertex));
//
//            // Add the object itself
//            _meshObjects.Add(new MeshObject()
//            {
//                localToWorldMatrix = obj.transform.localToWorldMatrix,
//                indices_offset = firstIndex,
//                indices_count = indices.Length
//            });
//        }
//
//        CreateComputeBuffer(ref _meshObjectBuffer, _meshObjects, 72);
//        CreateComputeBuffer(ref _vertexBuffer, _vertices, 12);
//        CreateComputeBuffer(ref _indexBuffer, _indices, 4);
//    }

    private static void CreateComputeBuffer<T>(ref ComputeBuffer buffer, List<T> data, int stride)
        where T : struct
    {
        // Do we already have a compute buffer?
        if (buffer != null)
        {
            // If no data or buffer doesn't match the given criteria, release it
            if (data.Count == 0 || buffer.count != data.Count || buffer.stride != stride)
            {
                buffer.Release();
                buffer = null;
            }
        }

        if (data.Count != 0)
        {
            // If the buffer has been released or wasn't there to
            // begin with, create it
            if (buffer == null)
            {
                buffer = new ComputeBuffer(data.Count, stride);
            }

            // Set data on the buffer
            buffer.SetData(data);
        }
    }
    
    private void SetComputeBuffer(string name, ComputeBuffer buffer, int kernelHandle)
    {
        if (buffer != null)
        {
            shader.SetBuffer(kernelHandle, name, buffer);
        }
    }
    
    private float GetNearestIntersectionDistanceForAutoFocus(Ray ray)
    {
        // This is a distance that allows things in the mid-distance to still get sub-pixel jitter, which
        // allows better anti-aliasing. Beyond this distance the focus changes are even more of a sub-pixel
        // and barely noticeable. We increase the jitter a bit if there is more super-sampling (passes) to get
        // more anti-aliasing.
        float nearestDistance = 12 - Math.Min(8.0f, numberOfPasses * 1.75f);

        foreach (var sphere in _spheres)
        {
            var hitDistance = sphere.Intersect(ray.origin, ray.direction);

            if (hitDistance >= 0.0f && hitDistance < nearestDistance)
            {
                nearestDistance = hitDistance;
            }
        }

        foreach (var sphere in _lights)
        {
            var hitDistance = sphere.Intersect(ray.origin, ray.direction);

            if (hitDistance >= 0.0f && hitDistance < nearestDistance)
            {
                nearestDistance = hitDistance;
            }
        }
        
        // Ground plane
        {
            var hitDistance = -ray.origin.y / ray.direction.y;

            if (hitDistance > 0 && hitDistance < nearestDistance)
            {
                nearestDistance = hitDistance;
            }
        }

        return nearestDistance;
    }
    
    private void SetShaderParameters(int kernelHandle)
    {
        shader.SetTexture(kernelHandle, "_SkyboxTexture", skyboxTexture);
        shader.SetTexture(kernelHandle, "_CheckerboardTexture", checkerboardTexture);
        
        shader.SetMatrix("_CameraToWorld", renderTextureCamera.cameraToWorldMatrix);
        shader.SetMatrix("_CameraInverseProjection", renderTextureCamera.projectionMatrix.inverse);

        _ambientLightColorAsVector = new Vector4(_ambientLightColor.r, _ambientLightColor.g, _ambientLightColor.b, 1.0f);
        shader.SetVector("_AmbientLight", _ambientLightColorAsVector);
        
        _skyboxLightColorAsVector = new Vector4(_skyboxLightColor.r / 255f, _skyboxLightColor.g / 255f, _skyboxLightColor.b / 255f, 1.0f);
        shader.SetVector("_SkyboxLight", _skyboxLightColorAsVector);
        
        shader.SetVector("_PixelOffset", new Vector2(UnityEngine.Random.value,UnityEngine.Random.value));

        if (randomNoise)
        {
            shader.SetFloat("_Seed", UnityEngine.Random.value);
        }
        else
        {
            shader.SetFloat("_Seed", (float) (new System.Random(0).NextDouble()));
        }

        shader.SetInt("_NumberOfPasses", numberOfPasses);
        shader.SetInt("_ShadowQuality", shadowQuality);
        shader.SetFloat("_ShadowRandomness", shadowRandomness);
        shader.SetFloat("_FocalDistance", cameraFocalDistance);
        shader.SetFloat("_GroundSmoothness", groundSmoothness);

        SetComputeBuffer("_Spheres", _sphereBuffer, kernelHandle);
        SetComputeBuffer("_Lights", _lightBuffer, kernelHandle);
    }
}
