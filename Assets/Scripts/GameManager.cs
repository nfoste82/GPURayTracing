using System;
using System.Collections.Generic;
using DefaultNamespace;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.Rendering;
#endif

public class GameManager : MonoBehaviour
{
    public ComputeShader shader;
    public Camera renderTextureCamera;
    
    [Header("Quality settings (Higher quality -> Slower)")]
    [Range(1, 32)]
    public int numberOfPasses = 1;

    [Range(1, 16)]
    public int numBounces = 3;

    [Range(0, 5)]
    public int shadowQuality = 2;

    [Range(0f, 1.5f)]
    public float shadowRandomness = 0.3f;

    public enum DebugRenderMode
    {
        FinalColor = 0,
        Normals = 1,
        Albedo = 2,
        Emission = 3,
        DirectLight = 4,
        Throughput = 5,
        BounceCount = 6,
        HitDistance = 7
    }

    [Header("Debug render modes")]
    public DebugRenderMode debugRenderMode = DebugRenderMode.FinalColor;

    [Header("Misc settings")]
    public bool cameraAutoFocus = true;
    
    [Range(0.1f, 100f)]
    public float cameraFocalDistance = 100f;

    [Range(0.0f, 1.0f)]
    public float groundSmoothness = 0.98f;

    [Tooltip("Higher values make direct light fall off faster with distance.")]
    [Range(0.001f, 1.0f)]
    public float lightFalloffScale = 0.16f;

    private float previousFocalDistance = 100f;
    private float timeSincePreviousFocusDistance = 1f;

    public bool randomNoise = false;

    public Color32 _skyboxLightColor = new Color32(123, 107, 101, 255);

    public Texture skyboxTexture;

    [Header("Scene preview")]
    public bool syncUnitySkyboxToRayTracedSkybox = true;

    [Range(0.0f, 8.0f)]
    public float unitySkyboxExposure = 1.0f;

    [Range(0.0f, 360.0f)]
    public float unitySkyboxRotation = 0.0f;

    private Vector4 _skyboxLightColorAsVector;
    private Material _unitySkyboxMaterial;

    private RenderTexture _outputTexture;
    private Vector2Int _textureSize;

    private List<Sphere> _spheres = new List<Sphere>();
    private readonly List<RayTracedSphere> _sphereObjects = new List<RayTracedSphere>();
    private ComputeBuffer _sphereBuffer;

    private List<Sphere> _lights = new List<Sphere>();
    private readonly List<RayTracedLight> _lightObjects = new List<RayTracedLight>();
    private ComputeBuffer _lightBuffer;

    private List<Triangle> _triangles = new List<Triangle>();
    private readonly List<MeshInfo> _meshInfos = new List<MeshInfo>();
    private readonly List<BvhNode> _bvhNodes = new List<BvhNode>();
    private readonly List<RayTracedMesh> _meshObjects = new List<RayTracedMesh>();
    private ComputeBuffer _triangleBuffer;
    private ComputeBuffer _meshBuffer;
    private ComputeBuffer _bvhNodeBuffer;
    
    [Header("Render single frame")] 
    public bool _singleFrame = false;

    private bool _running = true;
    private bool _previousSingleFrame;

    private static bool _buffersNeedRebuilding = false;
    private static readonly List<RayTracingObject> _rayTracingObjects = new List<RayTracingObject>();

    private const int SphereStride = 56;
    private const int TriangleStride = 80;
    private const int MeshInfoStride = 48;
    private const int BvhNodeStride = 48;
    private const int BvhLeafTriangleCount = 4;
    private const int BvhStackSize = 64;
    private const float BvhBoundsPadding = 0.0001f;
    private const float GroundPreviewSize = 40.0f;

    private struct Sphere
    {
        public Vector3 position;
        public Vector3 color;
        public Vector3 emission;
        public float radius;
        public float smoothness;
        public float opacity;
        public float refraction;
        public int materialType;
        
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

    private struct RayTracedSphere
    {
        public RayTracingObject obj;
        public Transform transform;
        public RayMaterial material;
        public SphereCollider collider;
    }

    private struct RayTracedLight
    {
        public RayTracingObject obj;
        public Transform transform;
        public RayLight light;
        public SphereCollider collider;
    }

    private struct Triangle
    {
        public Vector3 vertex0;
        public Vector3 vertex1;
        public Vector3 vertex2;
        public Vector3 normal;
        public Vector3 color;
        public float smoothness;
        public float opacity;
        public float refraction;
        public int materialType;
        public int meshIndex;

        public float Intersect(Vector3 origin, Vector3 direction)
        {
            var edge1 = vertex1 - vertex0;
            var edge2 = vertex2 - vertex0;
            var p = Vector3.Cross(direction, edge2);
            var determinant = Vector3.Dot(edge1, p);

            if (Mathf.Abs(determinant) < 0.000001f)
            {
                return -1.0f;
            }

            var inverseDeterminant = 1.0f / determinant;
            var t = origin - vertex0;
            var u = Vector3.Dot(t, p) * inverseDeterminant;

            if (u < 0.0f || u > 1.0f)
            {
                return -1.0f;
            }

            var q = Vector3.Cross(t, edge1);
            var v = Vector3.Dot(direction, q) * inverseDeterminant;

            if (v < 0.0f || u + v > 1.0f)
            {
                return -1.0f;
            }

            var hitDistance = Vector3.Dot(edge2, q) * inverseDeterminant;
            return hitDistance > 0.001f ? hitDistance : -1.0f;
        }
    }

    private struct MeshInfo
    {
        public Vector3 boundsMin;
        public int rootNodeIndex;
        public Vector3 boundsMax;
        public int triangleStart;
        public int triangleCount;
        public int meshIndex;
        public int padding0;
        public int padding1;
    }

    private struct BvhNode
    {
        public Vector3 boundsMin;
        public int leftChildIndex;
        public Vector3 boundsMax;
        public int rightChildIndex;
        public int triangleStart;
        public int triangleCount;
        public int padding0;
        public int padding1;
    }

    private struct RayTracedMesh
    {
        public RayTracingObject obj;
        public Transform transform;
        public RayMaterial material;
        public Mesh mesh;
        public Matrix4x4 previousLocalToWorld;
        public Vector3 previousColor;
        public float previousSmoothness;
        public float previousOpacity;
        public float previousRefraction;
        public int previousMaterialType;
    }

    private void Start()
    {
        SyncUnitySkyboxPreview();
        CreateOutputTexture(Screen.width, Screen.height);
        RebuildBuffers();
    }

    private void OnValidate()
    {
        SyncUnitySkyboxPreview();
    }

    private void OnDrawGizmos()
    {
        DrawGroundPreview();
    }

    private void OnDrawGizmosSelected()
    {
        DrawGroundPreview();
    }

    private static void DrawGroundPreview()
    {
#if UNITY_EDITOR
        var vertices = new[]
        {
            new Vector3(-GroundPreviewSize * 0.5f, 0.0f, -GroundPreviewSize * 0.5f),
            new Vector3(-GroundPreviewSize * 0.5f, 0.0f, GroundPreviewSize * 0.5f),
            new Vector3(GroundPreviewSize * 0.5f, 0.0f, GroundPreviewSize * 0.5f),
            new Vector3(GroundPreviewSize * 0.5f, 0.0f, -GroundPreviewSize * 0.5f)
        };

        var previousZTest = Handles.zTest;
        Handles.zTest = CompareFunction.LessEqual;
        Handles.DrawSolidRectangleWithOutline(vertices, new Color(0.8f, 0.8f, 0.8f, 1.0f), new Color(0.8f, 0.8f, 0.8f, 1.0f));

        float halfSize = GroundPreviewSize * 0.5f;
        const float gridSpacing = 2.0f;
        Handles.color = new Color(0.55f, 0.55f, 0.55f, 1.0f);
        for (float offset = -halfSize; offset <= halfSize; offset += gridSpacing)
        {
            Handles.DrawLine(new Vector3(-halfSize, 0.001f, offset), new Vector3(halfSize, 0.001f, offset));
            Handles.DrawLine(new Vector3(offset, 0.001f, -halfSize), new Vector3(offset, 0.001f, halfSize));
        }
        Handles.zTest = previousZTest;
#else
        var center = new Vector3(0.0f, -0.001f, 0.0f);
        var size = new Vector3(GroundPreviewSize, 0.02f, GroundPreviewSize);
        float halfSize = GroundPreviewSize * 0.5f;
        const float gridSpacing = 2.0f;

        Gizmos.color = new Color(0.8f, 0.8f, 0.8f, 1.0f);
        Gizmos.DrawWireCube(center, size);

        for (float offset = -halfSize; offset <= halfSize; offset += gridSpacing)
        {
            Gizmos.DrawLine(new Vector3(-halfSize, 0.0f, offset), new Vector3(halfSize, 0.0f, offset));
            Gizmos.DrawLine(new Vector3(offset, 0.0f, -halfSize), new Vector3(offset, 0.0f, halfSize));
        }
#endif
    }

    private void SyncUnitySkyboxPreview()
    {
        if (!syncUnitySkyboxToRayTracedSkybox || skyboxTexture == null)
        {
            return;
        }

        var skyboxShader = Shader.Find("Skybox/Panoramic");
        if (skyboxShader == null)
        {
            return;
        }

        if (_unitySkyboxMaterial == null || _unitySkyboxMaterial.shader != skyboxShader)
        {
            _unitySkyboxMaterial = new Material(skyboxShader)
            {
                name = "Ray Traced Skybox Preview",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        _unitySkyboxMaterial.SetTexture("_MainTex", skyboxTexture);
        _unitySkyboxMaterial.SetColor("_Tint", _skyboxLightColor);
        _unitySkyboxMaterial.SetFloat("_Exposure", unitySkyboxExposure);
        _unitySkyboxMaterial.SetFloat("_Rotation", unitySkyboxRotation);
        RenderSettings.skybox = _unitySkyboxMaterial;
    }

    private void CreateOutputTexture(int width, int height)
    {
        _outputTexture?.Release();
        _textureSize = new Vector2Int(width, height);
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

        if (_singleFrame != _previousSingleFrame)
        {
            SetSingleFrameMode(_singleFrame);
        }

        HandleInputForCamera(renderTextureCamera);

        if (Input.GetKeyDown(KeyCode.T))
        {
            SetSingleFrameMode(!_singleFrame);
        }

        if (Input.GetKeyDown(KeyCode.Space))
        {
            SetSingleFrameMode(false);
        }
    }

    private void SetSingleFrameMode(bool enabled)
    {
        _singleFrame = enabled;
        _previousSingleFrame = enabled;
        _running = true;

        if (!enabled)
        {
            EnableRealtimeSettings();
        }
    }

    private void EnableSingleFrameSettings()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 10;
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
        _outputTexture?.Release();
        _sphereBuffer?.Release();
        _lightBuffer?.Release();
        _triangleBuffer?.Release();
        _meshBuffer?.Release();
        _bvhNodeBuffer?.Release();
    }

    private void EnsureOutputTextureSize(int width, int height)
    {
        if (_outputTexture == null || width != _textureSize.x || height != _textureSize.y)
        {
            CreateOutputTexture(width, height);
        }

        renderTextureCamera.aspect = (float)_textureSize.x / _textureSize.y;
    }

    private void UpdateTextureFromCompute(int kernelHandle)
    {
        shader.SetTexture(kernelHandle, "Result", _outputTexture);
        int threadGroupsX = Mathf.CeilToInt(_textureSize.x / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(_textureSize.y / 8.0f);
        shader.Dispatch(kernelHandle, threadGroupsX, threadGroupsY, 1);
    }
    
    public void RenderImage(RenderTexture src, RenderTexture dest)
    {
        EnsureOutputTextureSize(src.width, src.height);

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
            UpdateTriangles();

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
            var sphereObject = _sphereObjects[i];

            sphere.position = sphereObject.transform.TransformPoint(sphereObject.collider.center);

            sphere.radius = GetWorldSphereRadius(sphereObject.collider, sphereObject.transform);

            var material = sphereObject.material;
            sphere.color = material.Color.ToVector3();
            sphere.refraction = material.RefractionIndex;
            sphere.opacity = material.Opacity;
            sphere.smoothness = material.Smoothness;
            sphere.materialType = (int)material.Type;
            
            _spheres[i] = sphere;
        }
        
        for (int i = 0; i < _lights.Count; ++i)
        {
            var sphere = _lights[i];
            var lightObject = _lightObjects[i];

            sphere.position = lightObject.transform.TransformPoint(lightObject.collider.center);

            sphere.radius = GetWorldSphereRadius(lightObject.collider, lightObject.transform);

            var light = lightObject.light;
            sphere.emission = light.Color.ToVector3();
            
            _lights[i] = sphere;
        }

        if (_sphereBuffer != null && _spheres.Count > 0)
        {
            _sphereBuffer.SetData(_spheres);
        }

        if (_lightBuffer != null && _lights.Count > 0)
        {
            _lightBuffer.SetData(_lights);
        }
    }

    private void UpdateTriangles()
    {
        if (_meshObjects.Count == 0)
        {
            return;
        }

        if (!UpdateMeshChangeCache())
        {
            return;
        }

        RebuildTriangleData();

        if (_triangleBuffer != null && _triangles.Count > 0)
        {
            _triangleBuffer.SetData(_triangles);
        }

        if (_meshBuffer != null && _meshInfos.Count > 0)
        {
            _meshBuffer.SetData(_meshInfos);
        }

        if (_bvhNodeBuffer != null && _bvhNodes.Count > 0)
        {
            _bvhNodeBuffer.SetData(_bvhNodes);
        }
    }

    private bool UpdateMeshChangeCache()
    {
        bool changed = false;

        for (int i = 0; i < _meshObjects.Count; i++)
        {
            var meshObject = _meshObjects[i];
            var material = meshObject.material;
            var localToWorld = meshObject.transform.localToWorldMatrix;
            var color = material.Color.ToVector3();
            var smoothness = material.Smoothness;
            var opacity = Mathf.Clamp01(material.Opacity);
            var refraction = material.RefractionIndex;
            var materialType = (int)material.Type;

            if (meshObject.previousLocalToWorld == localToWorld
                && meshObject.previousColor == color
                && Mathf.Approximately(meshObject.previousSmoothness, smoothness)
                && Mathf.Approximately(meshObject.previousOpacity, opacity)
                && Mathf.Approximately(meshObject.previousRefraction, refraction)
                && meshObject.previousMaterialType == materialType)
            {
                continue;
            }

            meshObject.previousLocalToWorld = localToWorld;
            meshObject.previousColor = color;
            meshObject.previousSmoothness = smoothness;
            meshObject.previousOpacity = opacity;
            meshObject.previousRefraction = refraction;
            meshObject.previousMaterialType = materialType;
            _meshObjects[i] = meshObject;
            changed = true;
        }

        return changed;
    }

    private void RebuildTriangleData()
    {
        _triangles.Clear();
        _meshInfos.Clear();
        _bvhNodes.Clear();

        for (int meshIndex = 0; meshIndex < _meshObjects.Count; meshIndex++)
        {
            var meshObject = _meshObjects[meshIndex];
            var mesh = meshObject.mesh;
            if (mesh == null)
            {
                continue;
            }

            var vertices = mesh.vertices;
            var indices = mesh.triangles;
            var localToWorld = meshObject.transform.localToWorldMatrix;
            var material = meshObject.material;
            var meshTriangles = new List<Triangle>(indices.Length / 3);

            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                var vertex0 = localToWorld.MultiplyPoint3x4(vertices[indices[i]]);
                var vertex1 = localToWorld.MultiplyPoint3x4(vertices[indices[i + 1]]);
                var vertex2 = localToWorld.MultiplyPoint3x4(vertices[indices[i + 2]]);
                var normal = Vector3.Cross(vertex1 - vertex0, vertex2 - vertex0).normalized;

                meshTriangles.Add(new Triangle
                {
                    vertex0 = vertex0,
                    vertex1 = vertex1,
                    vertex2 = vertex2,
                    normal = normal,
                    color = material.Color.ToVector3(),
                    smoothness = material.Smoothness,
                    opacity = Mathf.Clamp01(material.Opacity),
                    refraction = material.RefractionIndex,
                    materialType = (int)material.Type,
                    meshIndex = meshIndex
                });
            }

            if (meshTriangles.Count == 0)
            {
                continue;
            }

            var triangleStart = _triangles.Count;
            var rootNodeIndex = BuildBvhNode(meshTriangles, 0, meshTriangles.Count);
            _meshInfos.Add(new MeshInfo
            {
                boundsMin = _bvhNodes[rootNodeIndex].boundsMin,
                rootNodeIndex = rootNodeIndex,
                boundsMax = _bvhNodes[rootNodeIndex].boundsMax,
                triangleStart = triangleStart,
                triangleCount = _triangles.Count - triangleStart,
                meshIndex = meshIndex
            });
        }
    }

    private int BuildBvhNode(List<Triangle> meshTriangles, int start, int count)
    {
        var nodeIndex = _bvhNodes.Count;
        var boundsMin = GetTriangleBoundsMin(meshTriangles[start]);
        var boundsMax = GetTriangleBoundsMax(meshTriangles[start]);

        for (int i = start + 1; i < start + count; i++)
        {
            Encapsulate(meshTriangles[i], ref boundsMin, ref boundsMax);
        }

        var padding = Vector3.one * BvhBoundsPadding;
        boundsMin -= padding;
        boundsMax += padding;

        _bvhNodes.Add(new BvhNode
        {
            boundsMin = boundsMin,
            boundsMax = boundsMax,
            leftChildIndex = -1,
            rightChildIndex = -1,
            triangleStart = -1,
            triangleCount = 0
        });

        if (count <= BvhLeafTriangleCount)
        {
            var triangleStart = _triangles.Count;
            for (int i = start; i < start + count; i++)
            {
                _triangles.Add(meshTriangles[i]);
            }

            _bvhNodes[nodeIndex] = new BvhNode
            {
                boundsMin = boundsMin,
                boundsMax = boundsMax,
                leftChildIndex = -1,
                rightChildIndex = -1,
                triangleStart = triangleStart,
                triangleCount = count
            };
            return nodeIndex;
        }

        int axis = GetLongestAxis(boundsMax - boundsMin);
        meshTriangles.Sort(start, count, Comparer<Triangle>.Create((a, b) =>
            GetTriangleCentroid(a)[axis].CompareTo(GetTriangleCentroid(b)[axis])));

        int leftCount = count / 2;
        int rightCount = count - leftCount;
        int leftChildIndex = BuildBvhNode(meshTriangles, start, leftCount);
        int rightChildIndex = BuildBvhNode(meshTriangles, start + leftCount, rightCount);

        _bvhNodes[nodeIndex] = new BvhNode
        {
            boundsMin = boundsMin,
            boundsMax = boundsMax,
            leftChildIndex = leftChildIndex,
            rightChildIndex = rightChildIndex,
            triangleStart = -1,
            triangleCount = 0
        };

        return nodeIndex;
    }

    private static Vector3 GetTriangleCentroid(Triangle triangle)
    {
        return (triangle.vertex0 + triangle.vertex1 + triangle.vertex2) / 3.0f;
    }

    private static Vector3 GetTriangleBoundsMin(Triangle triangle)
    {
        return Vector3.Min(triangle.vertex0, Vector3.Min(triangle.vertex1, triangle.vertex2));
    }

    private static Vector3 GetTriangleBoundsMax(Triangle triangle)
    {
        return Vector3.Max(triangle.vertex0, Vector3.Max(triangle.vertex1, triangle.vertex2));
    }

    private static void Encapsulate(Triangle triangle, ref Vector3 boundsMin, ref Vector3 boundsMax)
    {
        boundsMin = Vector3.Min(boundsMin, GetTriangleBoundsMin(triangle));
        boundsMax = Vector3.Max(boundsMax, GetTriangleBoundsMax(triangle));
    }

    private static int GetLongestAxis(Vector3 size)
    {
        if (size.x >= size.y && size.x >= size.z)
        {
            return 0;
        }

        return size.y >= size.z ? 1 : 2;
    }

    private static bool IntersectAabb(Ray ray, Vector3 boundsMin, Vector3 boundsMax, float maxDistance)
    {
        var inverseDirection = new Vector3(
            1.0f / GetSafeDirectionComponent(ray.direction.x),
            1.0f / GetSafeDirectionComponent(ray.direction.y),
            1.0f / GetSafeDirectionComponent(ray.direction.z));

        var t0 = Vector3.Scale(boundsMin - ray.origin, inverseDirection);
        var t1 = Vector3.Scale(boundsMax - ray.origin, inverseDirection);
        var tMin3 = Vector3.Min(t0, t1);
        var tMax3 = Vector3.Max(t0, t1);
        float tMin = Mathf.Max(tMin3.x, Mathf.Max(tMin3.y, tMin3.z));
        float tMax = Mathf.Min(tMax3.x, Mathf.Min(tMax3.y, tMax3.z));

        return tMax >= Mathf.Max(0.0f, tMin) && tMin < maxDistance;
    }

    private static float GetSafeDirectionComponent(float value)
    {
        if (Mathf.Abs(value) >= 0.00000001f)
        {
            return value;
        }

        return value < 0.0f ? -0.00000001f : 0.00000001f;
    }

    private float GetNearestMeshIntersectionDistance(Ray ray, MeshInfo meshInfo, float nearestDistance)
    {
        if (meshInfo.triangleCount <= 0 || !IntersectAabb(ray, meshInfo.boundsMin, meshInfo.boundsMax, nearestDistance))
        {
            return nearestDistance;
        }

        var stack = new int[BvhStackSize];
        int stackCount = 0;
        stack[stackCount++] = meshInfo.rootNodeIndex;

        while (stackCount > 0)
        {
            var node = _bvhNodes[stack[--stackCount]];
            if (!IntersectAabb(ray, node.boundsMin, node.boundsMax, nearestDistance))
            {
                continue;
            }

            if (node.triangleCount > 0)
            {
                for (int i = 0; i < node.triangleCount; i++)
                {
                    var hitDistance = _triangles[node.triangleStart + i].Intersect(ray.origin, ray.direction);
                    if (hitDistance >= 0.0f && hitDistance < nearestDistance)
                    {
                        nearestDistance = hitDistance;
                    }
                }

                continue;
            }

            if (node.leftChildIndex >= 0 && stackCount < BvhStackSize)
            {
                stack[stackCount++] = node.leftChildIndex;
            }

            if (node.rightChildIndex >= 0 && stackCount < BvhStackSize)
            {
                stack[stackCount++] = node.rightChildIndex;
            }
        }

        return nearestDistance;
    }

    public void RebuildBuffers()
    {
        _buffersNeedRebuilding = false;
        _sphereBuffer?.Release();
        _lightBuffer?.Release();
        _triangleBuffer?.Release();
        _meshBuffer?.Release();
        _bvhNodeBuffer?.Release();
        _sphereBuffer = null;
        _lightBuffer = null;
        _triangleBuffer = null;
        _meshBuffer = null;
        _bvhNodeBuffer = null;

        RebuildTriangleData();

        shader.SetInt("_NumSpheres", _spheres.Count);
        shader.SetInt("_NumLights", _lights.Count);
        shader.SetInt("_NumTriangles", _triangles.Count);
        shader.SetInt("_NumMeshes", _meshInfos.Count);

        _sphereBuffer = CreateComputeBuffer(_spheres, SphereStride);
        _lightBuffer = CreateComputeBuffer(_lights, SphereStride);
        _triangleBuffer = CreateComputeBuffer(_triangles, TriangleStride);
        _meshBuffer = CreateComputeBuffer(_meshInfos, MeshInfoStride);
        _bvhNodeBuffer = CreateComputeBuffer(_bvhNodes, BvhNodeStride);
    }

    private static ComputeBuffer CreateComputeBuffer<T>(List<T> data, int stride) where T : struct
    {
        var buffer = new ComputeBuffer(Mathf.Max(1, data.Count), stride);
        if (data.Count > 0)
        {
            buffer.SetData(data);
        }
        else
        {
            buffer.SetData(new[] { default(T) });
        }

        return buffer;
    }

    public void RegisterObject(RayTracingObject obj)
    {
        if (_rayTracingObjects.Contains(obj))
        {
            return;
        }

        _rayTracingObjects.Add(obj);
        _buffersNeedRebuilding = true;

        var material = obj.GetComponent<RayMaterial>();
        var sphereCollider = obj.GetComponent<SphereCollider>();

        if (material != null && sphereCollider != null)
        {
            var sphere = new Sphere
            {
                position = obj.transform.TransformPoint(sphereCollider.center),
                color = material.Color.ToVector3(),
                smoothness = material.Smoothness,
                radius = GetWorldSphereRadius(sphereCollider, obj.transform),
                opacity = material.Opacity,
                refraction = material.RefractionIndex,
                materialType = (int)material.Type,
            };
            _spheres.Add(sphere);
            _sphereObjects.Add(new RayTracedSphere
            {
                obj = obj,
                transform = obj.transform,
                material = material,
                collider = sphereCollider
            });
            return;
        }

        var meshFilter = obj.GetComponent<MeshFilter>();
        if (material != null && meshFilter != null && meshFilter.sharedMesh != null)
        {
            _meshObjects.Add(new RayTracedMesh
            {
                obj = obj,
                transform = obj.transform,
                material = material,
                mesh = meshFilter.sharedMesh,
                previousLocalToWorld = obj.transform.localToWorldMatrix,
                previousColor = material.Color.ToVector3(),
                previousSmoothness = material.Smoothness,
                previousOpacity = Mathf.Clamp01(material.Opacity),
                previousRefraction = material.RefractionIndex,
                previousMaterialType = (int)material.Type
            });
            RebuildTriangleData();
            return;
        }

        var rayLight = obj.GetComponent<RayLight>();
        if (rayLight != null && sphereCollider != null)
        {
            var sphere = new Sphere
            {
                position = obj.transform.TransformPoint(sphereCollider.center),
                radius = GetWorldSphereRadius(sphereCollider, obj.transform),
                emission = rayLight.Color.ToVector3(),
                materialType = 3
            };
            _lights.Add(sphere);
            _lightObjects.Add(new RayTracedLight
            {
                obj = obj,
                transform = obj.transform,
                light = rayLight,
                collider = sphereCollider
            });
            return;
        }

        Debug.LogWarning($"RayTracingObject '{obj.name}' needs RayMaterial with SphereCollider or MeshFilter, or RayLight with SphereCollider.", obj);
    }
    
    public void UnregisterObject(RayTracingObject obj)
    {
        _rayTracingObjects.Remove(obj);
        _buffersNeedRebuilding = true;

        var sphereIndex = _sphereObjects.FindIndex(sphere => sphere.obj == obj);
        if (sphereIndex >= 0)
        {
            _sphereObjects.RemoveAt(sphereIndex);
            _spheres.RemoveAt(sphereIndex);
            return;
        }

        var lightIndex = _lightObjects.FindIndex(light => light.obj == obj);
        if (lightIndex >= 0)
        {
            _lightObjects.RemoveAt(lightIndex);
            _lights.RemoveAt(lightIndex);
            return;
        }

        var meshIndex = _meshObjects.FindIndex(mesh => mesh.obj == obj);
        if (meshIndex >= 0)
        {
            _meshObjects.RemoveAt(meshIndex);
            RebuildTriangleData();
        }
    }

    private void SetComputeBuffer(string name, ComputeBuffer buffer, int kernelHandle)
    {
        if (buffer != null)
        {
            shader.SetBuffer(kernelHandle, name, buffer);
        }
    }

    private static float GetWorldSphereRadius(SphereCollider sphereCollider, Transform sphereTransform)
    {
        var scale = sphereTransform.lossyScale;
        float largestAxisScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
        return sphereCollider.radius * largestAxisScale;
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

        foreach (var meshInfo in _meshInfos)
        {
            nearestDistance = GetNearestMeshIntersectionDistance(ray, meshInfo, nearestDistance);
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

        shader.SetMatrix("_CameraToWorld", renderTextureCamera.cameraToWorldMatrix);
        shader.SetMatrix("_CameraInverseProjection", renderTextureCamera.projectionMatrix.inverse);

        _skyboxLightColorAsVector = new Vector4(_skyboxLightColor.r / 255f, _skyboxLightColor.g / 255f, _skyboxLightColor.b / 255f, 1.0f);
        shader.SetVector("_SkyboxLight", _skyboxLightColorAsVector);

        if (randomNoise)
        {
            shader.SetInt("_Seed", UnityEngine.Random.Range(1, int.MaxValue));
        }
        else
        {
            shader.SetInt("_Seed", 1);
        }

        shader.SetInt("_NumberOfPasses", numberOfPasses);
        shader.SetInt("_NumBounces", numBounces);
        shader.SetInt("_DebugRenderMode", (int)debugRenderMode);
        shader.SetInt("_ShadowQuality", shadowQuality);
        shader.SetFloat("_ShadowRandomness", shadowRandomness);
        shader.SetFloat("_LightFalloffScale", lightFalloffScale);
        shader.SetFloat("_FocalDistance", cameraFocalDistance);
        shader.SetFloat("_GroundSmoothness", groundSmoothness);

        SetComputeBuffer("_Spheres", _sphereBuffer, kernelHandle);
        SetComputeBuffer("_Lights", _lightBuffer, kernelHandle);
        SetComputeBuffer("_Triangles", _triangleBuffer, kernelHandle);
        SetComputeBuffer("_Meshes", _meshBuffer, kernelHandle);
        SetComputeBuffer("_BvhNodes", _bvhNodeBuffer, kernelHandle);
    }
}
