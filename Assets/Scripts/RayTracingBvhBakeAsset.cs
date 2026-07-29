using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class RayTracingBvhBakeAsset : ScriptableObject
{
    [Serializable]
    public struct MeshEntry
    {
        public Mesh mesh;
        public string meshIdentity;
        public bool interpolateNormals;
        public string dependencyHash;
        public int vertexCount;
        public int indexCount;
        public int triangleCount;
        public int nodeCount;
    }

    public int formatVersion;
    public string sceneSignature;
    public string streamingAssetsRelativePath;
    public List<MeshEntry> meshes = new List<MeshEntry>();
}
