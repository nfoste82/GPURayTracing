using System.Collections.Generic;
using UnityEngine;

namespace PathTracing.Caustics
{
    /// <summary>
    /// Owns the GPU and CPU state used by the photon-mapped caustics path.
    /// GameManager remains the owner of the serialized settings and scene buffers.
    /// </summary>
    [System.Serializable]
    public sealed class CausticsManager
    {
        [SerializeField, Range(64, 4194200)]
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

        internal readonly List<CausticTargetPair> TargetPairs = new List<CausticTargetPair>();
        internal readonly List<CausticTargetTriangle> TargetTriangles = new List<CausticTargetTriangle>();

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
