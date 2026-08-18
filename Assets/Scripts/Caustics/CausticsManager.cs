using System.Collections.Generic;
using PathTracing.Lighting;
using PathTracing.Shapes;
using UnityEngine;
using UnityEngine.Rendering;
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
        private readonly int[] _gridDimensions = new int[3];

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
        private ComputeBuffer _dummyPhotonBuffer;
        private ComputeBuffer _dummyMetadataBuffer;
        private ComputeBuffer _dummyGridHeadBuffer;
        private ComputeBuffer _dummyPhotonNextBuffer;
        private ComputeBuffer _dummyTargetPairBuffer;
        private ComputeBuffer _dummyTargetTriangleBuffer;

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

        public void BuildSamplingDistribution(IReadOnlyList<Light> lights, List<Sphere> spheres,
            List<MeshInfo> meshes, List<Triangle> triangles, Water water)
        {
            TargetPairs.Clear();
            TargetTriangles.Clear();

            var meshTriangleRanges = new Dictionary<int, Vector2Int>();
            for (var meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
            {
                var mesh = meshes[meshIndex];
                if (!CausticsLogic.IsCausticRefractor(mesh, triangles)) continue;

                var triangleStart = TargetTriangles.Count;
                var totalArea = 0.0f;
                for (var triangleOffset = 0; triangleOffset < mesh.triangleCount; triangleOffset++)
                {
                    var triangle = triangles[mesh.triangleStart + triangleOffset];
                    totalArea += 0.5f * Vector3.Cross(
                        triangle.vertex1 - triangle.vertex0,
                        triangle.vertex2 - triangle.vertex0).magnitude;
                    TargetTriangles.Add(new CausticTargetTriangle
                    {
                        triangleIndex = mesh.triangleStart + triangleOffset,
                        cumulativeProbability = totalArea
                    });
                }

                if (totalArea <= 1e-8f)
                {
                    TargetTriangles.RemoveRange(triangleStart, TargetTriangles.Count - triangleStart);
                    continue;
                }

                var lastTriangleIndex = TargetTriangles.Count - 1;
                var previousCdf = 0.0f;
                for (var triangleIndex = triangleStart; triangleIndex < TargetTriangles.Count; triangleIndex++)
                {
                    var target = TargetTriangles[triangleIndex];
                    var normalizedCdf = target.cumulativeProbability / totalArea;
                    target.selectionProbability = normalizedCdf - previousCdf;
                    target.cumulativeProbability = triangleIndex == lastTriangleIndex ? 1.0f : normalizedCdf;
                    previousCdf = normalizedCdf;
                    TargetTriangles[triangleIndex] = target;
                }
                meshTriangleRanges.Add(meshIndex, new Vector2Int(
                    triangleStart, TargetTriangles.Count - triangleStart));
            }

            var pairWeights = new List<float>();
            var maximumWeight = 0.0f;
            for (var lightIndex = 0; lightIndex < lights.Count; lightIndex++)
            {
                var light = lights[lightIndex];
                if (!CausticsLogic.IsCausticLight(light)) continue;

                for (var sphereIndex = 0; sphereIndex < spheres.Count; sphereIndex++)
                {
                    var sphere = spheres[sphereIndex];
                    if (CausticsLogic.IsCausticRefractor(sphere))
                    {
                        AddTargetPair(lightIndex, 0, sphereIndex, 0, 0,
                            CausticsLogic.GetCausticPairWeight(light, sphere.position, sphere.radius),
                            pairWeights, ref maximumWeight);
                    }
                }

                foreach (var meshRange in meshTriangleRanges)
                {
                    var mesh = meshes[meshRange.Key];
                    AddTargetPair(lightIndex, 1, meshRange.Key, meshRange.Value.x, meshRange.Value.y,
                        CausticsLogic.GetCausticPairWeight(light, (mesh.boundsMin + mesh.boundsMax) * 0.5f,
                            (mesh.boundsMax - mesh.boundsMin).magnitude * 0.5f),
                        pairWeights, ref maximumWeight);
                }

                if (CausticsLogic.IsCausticRefractor(water))
                {
                    var waterSize = water.Size;
                    AddTargetPair(lightIndex, 2, -1, 0, 0,
                        CausticsLogic.GetCausticPairWeight(light, water.TopCenter,
                            new Vector3(waterSize.x, 0.0f, waterSize.y).magnitude * 0.5f),
                        pairWeights, ref maximumWeight);
                }
            }

            if (TargetPairs.Count == 0) return;
            var totalWeight = 0.0f;
            var minimumWeight = Mathf.Max(1e-8f, maximumWeight * 1e-4f);
            for (var i = 0; i < pairWeights.Count; i++)
            {
                pairWeights[i] = Mathf.Max(minimumWeight, pairWeights[i]);
                totalWeight += pairWeights[i];
            }

            var cumulativeProbability = 0.0f;
            for (var i = 0; i < TargetPairs.Count; i++)
            {
                var pair = TargetPairs[i];
                pair.selectionProbability = pairWeights[i] / totalWeight;
                cumulativeProbability += pair.selectionProbability;
                pair.cumulativeProbability = i == TargetPairs.Count - 1 ? 1.0f : cumulativeProbability;
                TargetPairs[i] = pair;
            }
        }

        private void AddTargetPair(int lightIndex, int refractorType, int refractorIndex,
            int triangleStart, int triangleCount, float weight, List<float> weights, ref float maximumWeight)
        {
            TargetPairs.Add(new CausticTargetPair
            {
                lightIndex = lightIndex,
                refractorType = refractorType,
                refractorIndex = refractorIndex,
                triangleStart = triangleStart,
                triangleCount = triangleCount
            });
            weights.Add(weight);
            maximumWeight = Mathf.Max(maximumWeight, weight);
        }

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
            _gridDimensions[0] = GridDimensions.x;
            _gridDimensions[1] = GridDimensions.y;
            _gridDimensions[2] = GridDimensions.z;
            
            EnsureDummyResources();
            shader.SetInt(CausticPhotonCapacity, Mathf.Max(1, PhotonCount));
            shader.SetInt(CausticPhotonAttemptCount, Mathf.Max(1, PhotonCount));
            shader.SetInt(CausticMaxBounces, Mathf.Clamp(maxBounces, 1, 16));
            shader.SetInt(CausticSeed, Seed);
            shader.SetInt(CausticFrameIndex, FrameIndex);
            shader.SetFloat(CausticGatherRadius, Mathf.Max(0.001f, GatherRadius));
            shader.SetFloat(CausticIntensity, Mathf.Max(0.0f, Intensity));
            shader.SetVector(CausticGridMin, GridMin);
            shader.SetFloat(CausticGridCellSize, GridCellSize);
            shader.SetInts(CausticGridDimensions, _gridDimensions);
            shader.SetInt(CausticGridCellCount, GridCellCount);
            shader.SetInt(NumCausticTargetPairs, TargetPairs.Count);
            SetBuffer(shader, kernelHandle, CausticPhotons, PhotonBuffer ?? _dummyPhotonBuffer);
            SetBuffer(shader, kernelHandle, CausticPhotonMetadata, PhotonMetadataBuffer ?? _dummyMetadataBuffer);
            SetBuffer(shader, kernelHandle, CausticGridCellHeads, GridCellHeadBuffer ?? _dummyGridHeadBuffer);
            SetBuffer(shader, kernelHandle, CausticPhotonNext, PhotonNextBuffer ?? _dummyPhotonNextBuffer);
            SetBuffer(shader, kernelHandle, CausticTargetPairs, TargetPairBuffer ?? _dummyTargetPairBuffer);
            SetBuffer(shader, kernelHandle, CausticTargetTriangles, TargetTriangleBuffer ?? _dummyTargetTriangleBuffer);
        }

        internal void BindBuffers(ComputeShader shader, int kernelHandle)
        {
            EnsureDummyResources();
            SetBuffer(shader, kernelHandle, CausticPhotons, PhotonBuffer ?? _dummyPhotonBuffer);
            SetBuffer(shader, kernelHandle, CausticPhotonMetadata, PhotonMetadataBuffer ?? _dummyMetadataBuffer);
            SetBuffer(shader, kernelHandle, CausticGridCellHeads, GridCellHeadBuffer ?? _dummyGridHeadBuffer);
            SetBuffer(shader, kernelHandle, CausticPhotonNext, PhotonNextBuffer ?? _dummyPhotonNextBuffer);
            SetBuffer(shader, kernelHandle, CausticTargetPairs, TargetPairBuffer ?? _dummyTargetPairBuffer);
            SetBuffer(shader, kernelHandle, CausticTargetTriangles, TargetTriangleBuffer ?? _dummyTargetTriangleBuffer);
        }

        private void EnsureDummyResources()
        {
            _dummyPhotonBuffer ??= new ComputeBuffer(1, PhotonStride);
            _dummyMetadataBuffer ??= new ComputeBuffer(MetadataCount, sizeof(uint));
            _dummyGridHeadBuffer ??= new ComputeBuffer(1, sizeof(int));
            _dummyPhotonNextBuffer ??= new ComputeBuffer(1, sizeof(int));
            _dummyTargetPairBuffer ??= new ComputeBuffer(1, TargetPairStride);
            _dummyTargetTriangleBuffer ??= new ComputeBuffer(1, TargetTriangleStride);
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

        internal void ConfigureGrid(Vector3 boundsMin, Vector3 boundsMax, float gatherRadius, int maximumCellCount)
        {
            float padding = Mathf.Max(0.01f, gatherRadius);
            boundsMin -= Vector3.one * padding;
            boundsMax += Vector3.one * padding;
            Vector3 size = Vector3.Max(boundsMax - boundsMin, Vector3.one * padding);
            float cellSize = padding;
            Vector3Int dimensions = CalculateGridDimensions(size, cellSize);
            while ((long)dimensions.x * dimensions.y * dimensions.z > maximumCellCount)
            {
                cellSize *= 1.25f;
                dimensions = CalculateGridDimensions(size, cellSize);
            }

            GridMin = boundsMin;
            GridCellSize = cellSize;
            GridDimensions = dimensions;
            long cellCount = (long)dimensions.x * dimensions.y * dimensions.z;
            GridCellCountValue = Mathf.Max(1, (int)Mathf.Min(int.MaxValue, cellCount));
        }

        internal void UploadSamplingDistribution()
        {
            TargetPairBuffer.SetData(TargetPairs.Count > 0 ? TargetPairs : new List<CausticTargetPair> { default });
            TargetTriangleBuffer.SetData(TargetTriangles.Count > 0 ? TargetTriangles : new List<CausticTargetTriangle> { default });
        }

        internal void RequestMetadataReadback()
        {
            if (MetadataReadbackInFlight || PhotonMetadataBuffer == null)
            {
                return;
            }

            MetadataReadbackInFlight = true;
            var generation = MetadataReadbackGeneration;
            AsyncGPUReadback.Request(PhotonMetadataBuffer,
                request => CompleteMetadataReadback(request, generation));
        }

        private void CompleteMetadataReadback(AsyncGPUReadbackRequest request, int generation)
        {
            if (generation != MetadataReadbackGeneration)
            {
                return;
            }

            MetadataReadbackInFlight = false;
            if (request.hasError)
            {
                Debug.LogWarning("Caustic metadata GPU readback failed.");
                return;
            }

            var metadata = request.GetData<uint>();
            if (metadata.Length >= MetadataCount)
            {
                GridOutOfBoundsCountValue = (int)metadata[4];
                GridPhotonCountValue = (int)metadata[5];
            }
        }

        private static Vector3Int CalculateGridDimensions(Vector3 size, float cellSize)
        {
            return new Vector3Int(
                Mathf.Max(1, Mathf.CeilToInt(size.x / cellSize)),
                Mathf.Max(1, Mathf.CeilToInt(size.y / cellSize)),
                Mathf.Max(1, Mathf.CeilToInt(size.z / cellSize)));
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
            _dummyPhotonBuffer?.Release();
            _dummyMetadataBuffer?.Release();
            _dummyGridHeadBuffer?.Release();
            _dummyPhotonNextBuffer?.Release();
            _dummyTargetPairBuffer?.Release();
            _dummyTargetTriangleBuffer?.Release();
            PhotonBuffer = null;
            PhotonMetadataBuffer = null;
            GridCellHeadBuffer = null;
            PhotonNextBuffer = null;
            TargetPairBuffer = null;
            TargetTriangleBuffer = null;
            _dummyPhotonBuffer = null;
            _dummyMetadataBuffer = null;
            _dummyGridHeadBuffer = null;
            _dummyPhotonNextBuffer = null;
            _dummyTargetPairBuffer = null;
            _dummyTargetTriangleBuffer = null;
            GridCellCountValue = 0;
            GridPhotonCountValue = 0;
            GridOutOfBoundsCountValue = 0;
            HasPhotonStateHash = false;
            FrameIndex = 0;
        }
    }
}
