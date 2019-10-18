﻿using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    public ComputeShader shader;
    public Camera renderTextureCamera;
    public Texture skyboxTexture;
    
    private RenderTexture _outputTexture;
    private Vector2Int _textureSize;

    private List<Sphere> _spheres;
    private ComputeBuffer _sphereBuffer;
    
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
        //public Vector3 albedo;
        //public Vector3 specular;
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

    private void OnDestroy()
    {
        _sphereBuffer?.Release();
    }

//    private void Update()
//    {
//        
//    }

    private void UpdateTextureFromCompute(int kernelHandle)
    {
        shader.SetTexture(kernelHandle, "Result", _outputTexture);
        int threadGroupsX = Mathf.CeilToInt(_textureSize.x / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(_textureSize.y / 8.0f);
        shader.Dispatch(kernelHandle, threadGroupsX, threadGroupsY, 1);
    }
    
    private void OnRenderImage(RenderTexture src, RenderTexture dest)
    {
        MoveSpheres();
        
        var kernelHandle = shader.FindKernel("CSMain");
        
        SetShaderParameters(kernelHandle);
        UpdateTextureFromCompute(kernelHandle);
        
        renderTextureCamera.targetTexture = null;
        Graphics.Blit(_outputTexture, null as RenderTexture);
    }

    private void SetupSpheres()
    {
        _spheres = new List<Sphere>();

        var sphere = new Sphere
        {
            position = Vector3.zero,
            emission = new Vector3(0.0f, 0.0f, 0.0f),
            color = new Vector3(1.0f, 0f, 0f),
            radius = 0.5f,
            smoothness = 0.8f
        };
        _spheres.Add(sphere);
        
        var sphere2 = new Sphere
        {
            position = new Vector3(-2.0f, 2.0f, 0.0f),
            emission = new Vector3(0.0f, 0.0f, 0.0f),
            color = new Vector3(0f, 0f, 1f),
            radius = 0.3f,
            smoothness = 0.6f
        };
        _spheres.Add(sphere2);

        _sphereBuffer?.Release();

        if (_spheres.Count > 0)
        {
            shader.SetInt("_NumSpheres", _spheres.Count);
            
            _sphereBuffer = new ComputeBuffer(_spheres.Count, 44);
            _sphereBuffer.SetData(_spheres);
        }
    }

    private void MoveSpheres()
    {
        for (int i = 0; i < _spheres.Count; ++i)
        {
            var sphere = _spheres[i];
            
            sphere.position.y += Mathf.Sin(Time.time + i) * Time.deltaTime;
            _spheres[i] = sphere;
        }

        _sphereBuffer.SetData(_spheres);
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
    
    private void SetShaderParameters(int kernelHandle)
    {
        shader.SetTexture(kernelHandle, "_SkyboxTexture", skyboxTexture);
        shader.SetMatrix("_CameraToWorld", renderTextureCamera.cameraToWorldMatrix);
        shader.SetMatrix("_CameraInverseProjection", renderTextureCamera.projectionMatrix.inverse);
        //shader.SetVector("_PixelOffset", new Vector2(Random.value, Random.value));
        //shader.SetFloat("_Seed", Random.value);

        SetComputeBuffer("_Spheres", _sphereBuffer, kernelHandle);
    }
}