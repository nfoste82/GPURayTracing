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

        public bool HasResources => PhotonBuffer != null && PhotonMetadataBuffer != null
            && GridCellHeadBuffer != null && PhotonNextBuffer != null
            && TargetPairBuffer != null && TargetTriangleBuffer != null;

        public int DispatchCount => DispatchCountValue;
        public int GridCellCount => GridCellCountValue;
        public int GridPhotonCount => GridPhotonCountValue;
        public int GridOutOfBoundsCount => GridOutOfBoundsCountValue;
        public int TargetPairCount => TargetPairs.Count;

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
