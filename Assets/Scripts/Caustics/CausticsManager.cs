using System.Collections.Generic;
using UnityEngine;
using Light = PathTracing.Lighting.Light;

namespace PathTracing.Caustics
{
    /// <summary>
    /// Owns the GPU and CPU state used by the photon-mapped caustics path.
    /// GameManager remains the owner of the scene data and GPU scene buffers.
    /// </summary>
    [System.Serializable]
    public sealed class CausticsManager
    {
        private static readonly int CausticPhotonCapacity = Shader.PropertyToID("_CausticPhotonCapacity");
        private static readonly int CausticPhotonAttemptCount = Shader.PropertyToID("_CausticPhotonAttemptCount");
        private static readonly int CausticMaxBounces = Shader.PropertyToID("_CausticMaxBounces");
        private static readonly int CausticSeed = Shader.PropertyToID("_CausticSeed");
        private static readonly int CausticFrameIndex = Shader.PropertyToID("_CausticFrameIndex");
        private static readonly int CausticGatherRadius = Shader.PropertyToID("_CausticGatherRadius");
        private static readonly int CausticIntensity = Shader.PropertyToID("_CausticIntensity");
        private static readonly int CausticGridMin = Shader.PropertyToID("_CausticGridMin");
        private static readonly int CausticGridCellSize = Shader.PropertyToID("_CausticGridCellSize");
        private static readonly int CausticGridDimensions = Shader.PropertyToID("_CausticGridDimensions");
        private static readonly int CausticGridCellCount = Shader.PropertyToID("_CausticGridCellCount");
        private static readonly int NumCausticTargetPairs = Shader.PropertyToID("_NumCausticTargetPairs");
        private static readonly int CausticPhotons = Shader.PropertyToID("_CausticPhotons");
        private static readonly int CausticPhotonMetadata = Shader.PropertyToID("_CausticPhotonMetadata");
        private static readonly int CausticGridCellHeads = Shader.PropertyToID("_CausticGridCellHeads");
        private static readonly int CausticPhotonNext = Shader.PropertyToID("_CausticPhotonNext");
        private static readonly int CausticTargetPairs = Shader.PropertyToID("_CausticTargetPairs");
        private static readonly int CausticTargetTriangles = Shader.PropertyToID("_CausticTargetTriangles");

        [SerializeField, Range(64, 2097252)]
        [Tooltip("Photon attempts traced for each rendered frame. Independent batches are averaged by final-color frame accumulation.")]
        private int _photonCount = 65536;

        [SerializeField, Range(0.01f, 2.0f)]
        private float _gatherRadius = 0.025f;

        [SerializeField, HideInInspector]
        private int _seed = 1;

        [SerializeField, Range(0.0f, 10.0f)]
        private float _intensity = 4.0f;

        public int PhotonCount { get => _photonCount; set => _photonCount = value; }
        public float GatherRadius { get => _gatherRadius; set => _gatherRadius = value; }
        public int Seed { get => _seed; set => _seed = value; }
        public float Intensity { get => _intensity; set => _intensity = value; }

        internal readonly List<CausticTargetPair> TargetPairs = new ();
        internal readonly List<CausticTargetTriangle> TargetTriangles = new ();

        internal Vector3 GridMin;
        internal Vector3Int GridDimensions;
        internal float GridCellSize;
        internal int GridCellCountValue;
        internal int GridOutOfBoundsCountValue;
        internal int GridPhotonCountValue;
        internal int PhotonStateHash;
        internal bool HasPhotonStateHash;
        internal int FrameIndex;
        internal bool PreviousEnabled;
        internal int DispatchCountValue;
        internal bool MetadataReadbackInFlight;
        internal int MetadataReadbackGeneration;

        internal ComputeBuffer PhotonBuffer;
        internal ComputeBuffer PhotonMetadataBuffer;
        internal ComputeBuffer GridCellHeadBuffer;
        internal ComputeBuffer PhotonNextBuffer;
        internal ComputeBuffer TargetPairBuffer;
        internal ComputeBuffer TargetTriangleBuffer;

        internal const int PhotonStride = 36;
        internal const int MetadataCount = 6;
        internal const int TargetPairStride = 32;
        internal const int TargetTriangleStride = 12;

        public bool HasResources => PhotonBuffer != null && PhotonMetadataBuffer != null
            && GridCellHeadBuffer != null && PhotonNextBuffer != null
            && TargetPairBuffer != null && TargetTriangleBuffer != null;

        public int DispatchCount => DispatchCountValue;
        public int GridCellCount => GridCellCountValue;
        public int GridPhotonCount => GridPhotonCountValue;
        public int GridOutOfBoundsCount => GridOutOfBoundsCountValue;
        public int TargetPairCount => TargetPairs.Count;

        internal int CalculatePhotonStateHash(int hash)
        {
            unchecked
            {
                hash = GameManager.AddHash(hash, 5); // Progressive, low-discrepancy photon-map algorithm version.
                hash = GameManager.AddHash(hash, PhotonCount);
                hash = GameManager.AddHash(hash, GatherRadius);
                hash = GameManager.AddHash(hash, Seed);
                return hash;
            }
        }

        internal int AddAccumulationStateHash(int hash, bool enabled)
        {
            if (!enabled)
            {
                return hash;
            }

            hash = GameManager.AddHash(hash, PhotonCount);
            hash = GameManager.AddHash(hash, GatherRadius);
            hash = GameManager.AddHash(hash, Seed);
            hash = GameManager.AddHash(hash, Intensity);
            return GameManager.AddHash(hash, PhotonStateHash);
        }

        internal void SetShaderParameters(ComputeShader shader, int kernelHandle, int maxBounces)
        {
            shader.SetInt(CausticPhotonCapacity, Mathf.Max(1, PhotonCount));
            shader.SetInt(CausticPhotonAttemptCount, Mathf.Max(1, PhotonCount));
            shader.SetInt(CausticMaxBounces, Mathf.Clamp(maxBounces, 1, 16));
            shader.SetInt(CausticSeed, Seed);
            shader.SetInt(CausticFrameIndex, FrameIndex);
            shader.SetFloat(CausticGatherRadius, Mathf.Max(0.001f, GatherRadius));
            shader.SetFloat(CausticIntensity, Mathf.Max(0.0f, Intensity));
            shader.SetVector(CausticGridMin, GridMin);
            shader.SetFloat(CausticGridCellSize, GridCellSize);
            shader.SetInts(CausticGridDimensions, GridDimensions.x, GridDimensions.y, GridDimensions.z);
            shader.SetInt(CausticGridCellCount, GridCellCount);
            shader.SetInt(NumCausticTargetPairs, TargetPairs.Count);
            SetBuffer(shader, kernelHandle, CausticPhotons, PhotonBuffer);
            SetBuffer(shader, kernelHandle, CausticPhotonMetadata, PhotonMetadataBuffer);
            SetBuffer(shader, kernelHandle, CausticGridCellHeads, GridCellHeadBuffer);
            SetBuffer(shader, kernelHandle, CausticPhotonNext, PhotonNextBuffer);
            SetBuffer(shader, kernelHandle, CausticTargetPairs, TargetPairBuffer);
            SetBuffer(shader, kernelHandle, CausticTargetTriangles, TargetTriangleBuffer);
        }

        private static void SetBuffer(ComputeShader shader, int kernelHandle, int nameId, ComputeBuffer buffer)
        {
            if (buffer != null)
            {
                shader.SetBuffer(kernelHandle, nameId, buffer);
            }
        }

        internal void EnsureResources(int photonCount)
        {
            int photonCapacity = Mathf.Max(1, photonCount);
            int targetPairCapacity = Mathf.Max(1, TargetPairs.Count);
            int targetTriangleCapacity = Mathf.Max(1, TargetTriangles.Count);
            if (PhotonBuffer != null && PhotonBuffer.count == photonCapacity
                && PhotonMetadataBuffer != null
                && PhotonNextBuffer != null && PhotonNextBuffer.count == photonCapacity
                && GridCellHeadBuffer != null && GridCellHeadBuffer.count == GridCellCount
                && TargetPairBuffer != null && TargetPairBuffer.count == targetPairCapacity
                && TargetTriangleBuffer != null && TargetTriangleBuffer.count == targetTriangleCapacity)
            {
                return;
            }

            // ReleaseResources clears diagnostics, including the layout calculated by GameManager.
            // Preserve the current grid capacity before releasing the previous buffers.
            int gridCellCount = Mathf.Max(1, GridCellCount);
            ReleaseResources();
            GridCellCountValue = gridCellCount;
            PhotonBuffer = new ComputeBuffer(photonCapacity, PhotonStride);
            PhotonMetadataBuffer = new ComputeBuffer(MetadataCount, sizeof(uint));
            PhotonNextBuffer = new ComputeBuffer(photonCapacity, sizeof(int));
            GridCellHeadBuffer = new ComputeBuffer(gridCellCount, sizeof(int));
            TargetPairBuffer = CreateComputeBuffer(TargetPairs, TargetPairStride);
            TargetTriangleBuffer = CreateComputeBuffer(TargetTriangles, TargetTriangleStride);
            HasPhotonStateHash = false;
        }

        private static ComputeBuffer CreateComputeBuffer<T>(List<T> data, int stride) where T : struct
        {
            return new ComputeBuffer(Mathf.Max(1, data.Count), stride);
        }

        internal void ReleaseResources()
        {
            MetadataReadbackGeneration++;
            MetadataReadbackInFlight = false;
            PhotonBuffer?.Release();
            PhotonMetadataBuffer?.Release();
            GridCellHeadBuffer?.Release();
            PhotonNextBuffer?.Release();
            TargetPairBuffer?.Release();
            TargetTriangleBuffer?.Release();
            PhotonBuffer = null;
            PhotonMetadataBuffer = null;
            GridCellHeadBuffer = null;
            PhotonNextBuffer = null;
            TargetPairBuffer = null;
            TargetTriangleBuffer = null;
            GridCellCountValue = 0;
            GridPhotonCountValue = 0;
            GridOutOfBoundsCountValue = 0;
            HasPhotonStateHash = false;
            FrameIndex = 0;
        }
    }
}
