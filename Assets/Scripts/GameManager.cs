using System;
using System.Collections.Generic;
using System.Linq;
using DefaultNamespace;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    public ComputeShader shader;
    public Camera renderTextureCamera;
    
    [Range(1, 32)]
    public int numberOfPasses = 1;

    [Range(0, 5)]
    public int shadowQuality = 2;

    [Range(0, 4)]
    public int numRayBounces = 2;

    [Range(0f, 0.2f)] 
    public float shadowRandomness = 0.06f;

    [Range(0.1f, 100f)] 
    public float cameraFocalDistance = 100f;

    public float shiftAmount = 0.1f;

    public bool cameraAutoFocus = true;
    
    public bool randomNoise = false;
    
    public Color _ambientLightColor;
    public Color32 _skyboxLightColor = new Color32(123, 107, 101, 255);
    
    public Texture skyboxTexture;
    public Texture checkerboardTexture;

    private Vector4 _ambientLightColorAsVector;
    private Vector4 _skyboxLightColorAsVector;
    
    private RenderTexture _outputTexture;
    private Vector2Int _textureSize;
    
    private List<Sphere> _spheres;
    private List<GameObject> _sphereObjects;
    private ComputeBuffer _sphereBuffer;

    private List<Sphere> _lights;
    private List<GameObject> _lightObjects;
    private ComputeBuffer _lightBuffer;
    
    private static bool _meshObjectsNeedRebuilding = false;
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
        
        SetupSpheres();
    }

    private void Update()
    {
        HandleInputForCamera(renderTextureCamera);
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
        var autoFocusDistance = (cameraAutoFocus) ? GetNearestIntersectionDistance(new Ray(renderTextureCamera.transform.position, renderTextureCamera.transform.forward)) : cameraFocalDistance;

        if (cameraAutoFocus && autoFocusDistance < 1.0f)
        {
            var modifier = Mathf.Lerp(1.75f, 1.0f, autoFocusDistance);
            autoFocusDistance *= modifier;

            autoFocusDistance = Mathf.Max(autoFocusDistance, 0.1f);
        }
        
        cameraFocalDistance = autoFocusDistance;
        
        UpdateSpheres();
        
        var kernelHandle = shader.FindKernel("CSMain");
        
        SetShaderParameters(kernelHandle);
        UpdateTextureFromCompute(kernelHandle);
        
        renderTextureCamera.targetTexture = null;
        Graphics.Blit(_outputTexture, null as RenderTexture);
    }

    private void SetupSpheres()
    {
        _spheres = new List<Sphere>();
        _sphereObjects = new List<GameObject>();
        
        _lights = new List<Sphere>();
        _lightObjects = new List<GameObject>();

        var sphereTransforms = GameObject.Find("Spheres").GetComponentsInChildren<Transform>();

        foreach (var sphereObj in sphereTransforms)
        {
            var material = sphereObj.GetComponent<RayMaterial>();

            if (material == null)
            {
                continue;
            }
            
            var sphere = new Sphere
            {
                position = sphereObj.transform.position,
                color = material.Color.ToVector3(),
                smoothness = material.Smoothness,
                radius = sphereObj.GetComponent<SphereCollider>().radius,
                opacity = material.Opacity,
                refraction = material.RefractionIndex,
            };
            _spheres.Add(sphere);
            _sphereObjects.Add(sphereObj.gameObject);
        }

        var lightTransforms = GameObject.Find("Lights").GetComponentsInChildren<Transform>();

        foreach (var lightObj in lightTransforms)
        {
            var light = lightObj.GetComponent<RayLight>();

            if (light == null)
            {
                continue;
            }
            
            var sphere = new Sphere
            {
                position = lightObj.transform.position,
                radius = lightObj.GetComponent<SphereCollider>().radius,
                emission = light.Color.ToVector3()
            };
            _lights.Add(sphere);
            _lightObjects.Add(lightObj.gameObject);
        }

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

    public static void RegisterObject(RayTracingObject obj)
    {
        _rayTracingObjects.Add(obj);
        //_meshObjectsNeedRebuilding = true;
    }
    public static void UnregisterObject(RayTracingObject obj)
    {
        _rayTracingObjects.Remove(obj);
        //_meshObjectsNeedRebuilding = true;
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
    
    private float GetNearestIntersectionDistance(Ray ray)
    {
        float nearestDistance = 10000.0f;
            
        Collider nearestCollider = null;

        foreach (var sphere in _spheres)
        {
            var hitDistance = sphere.Intersect(ray.origin, ray.direction);

            if (hitDistance >= 0.0f && hitDistance < nearestDistance)
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
        shader.SetInt("_NumBounces", numRayBounces);
        shader.SetFloat("_ShadowRandomness", shadowRandomness);
        shader.SetFloat("_FocalDistance", cameraFocalDistance);
        shader.SetFloat("_ShiftAmount", shiftAmount);

        SetComputeBuffer("_Spheres", _sphereBuffer, kernelHandle);
        SetComputeBuffer("_Lights", _lightBuffer, kernelHandle);
    }
}
