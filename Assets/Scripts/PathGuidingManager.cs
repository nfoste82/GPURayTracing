using UnityEngine;

namespace PathTracing.Sampling
{
    // Progressive first implementation: a world-space grid of normal-relative directional bins.
    // The guide is deliberately opt-in until its estimator and performance are validated.
    public sealed class PathGuidingManager
    {
        public const int GridResolution = 16;
        public const int DirectionBinCount = 32;

        private static readonly int EnabledId = Shader.PropertyToID("_PathGuideEnabled");
        private static readonly int MixtureWeightId = Shader.PropertyToID("_PathGuideMixtureWeight");
        private static readonly int MinSamplesId = Shader.PropertyToID("_PathGuideMinSamples");
        private static readonly int GridMinId = Shader.PropertyToID("_PathGuideGridMin");
        private static readonly int GridMaxId = Shader.PropertyToID("_PathGuideGridMax");
        private static readonly int GridResolutionId = Shader.PropertyToID("_PathGuideGridResolution");
        private static readonly int DirectionBinCountId = Shader.PropertyToID("_PathGuideDirectionBinCount");
        private static readonly int TrainingId = Shader.PropertyToID("_PathGuideTraining");
        private static readonly int CdfId = Shader.PropertyToID("_PathGuideCdf");
        private static readonly int ObservationCountsId = Shader.PropertyToID("_PathGuideObservationCounts");

        private ComputeBuffer _training;
        private ComputeBuffer _cdf;
        private ComputeBuffer _observationCounts;
        private ComputeBuffer _inertTraining;
        private ComputeBuffer _inertCdf;
        private ComputeBuffer _inertObservationCounts;
        private ComputeShader _rebuildShader;
        private Vector3 _gridMin = new(-10.0f, -1.0f, -10.0f);
        private Vector3 _gridMax = new(10.0f, 10.0f, 10.0f);
        private int _rebuildKernel = -1;

        public bool Enabled { get; set; }
        public float MixtureWeight { get; set; } = 0.5f;
        public int MinimumSamples { get; set; } = 32;

        private static int CellCount => GridResolution * GridResolution * GridResolution;
        private static int TrainingCount => CellCount * DirectionBinCount;

        public void ConfigureBounds(Vector3 min, Vector3 max)
        {
            _gridMin = Vector3.Min(min, max - Vector3.one * 0.01f);
            _gridMax = Vector3.Max(max, _gridMin + Vector3.one * 0.02f);
        }

        public void EnsureResources(ComputeShader rebuildShader)
        {
            if (_training != null) return;

            _training = new ComputeBuffer(TrainingCount, sizeof(uint));
            _cdf = new ComputeBuffer(TrainingCount, sizeof(float));
            _observationCounts = new ComputeBuffer(CellCount, sizeof(uint));
            _rebuildKernel = rebuildShader.FindKernel("RebuildPathGuide");
            _rebuildShader = rebuildShader;
            _training.SetData(new uint[TrainingCount]);
            _cdf.SetData(CreateUniformCdf());
            _observationCounts.SetData(new uint[CellCount]);
        }

        public void Rebuild(ComputeShader rebuildShader)
        {
            if (_training == null) return;
            rebuildShader.SetInt(GridResolutionId, GridResolution);
            rebuildShader.SetInt(DirectionBinCountId, DirectionBinCount);
            rebuildShader.SetBuffer(_rebuildKernel, TrainingId, _training);
            rebuildShader.SetBuffer(_rebuildKernel, CdfId, _cdf);
            rebuildShader.SetBuffer(_rebuildKernel, ObservationCountsId, _observationCounts);
            ComputeShaderDispatch(rebuildShader, _rebuildKernel, CellCount);
        }

        public void Bind(ComputeShader shader, int kernel, ComputeShader rebuildShader)
        {
            shader.SetInt(EnabledId, Enabled ? 1 : 0);
            shader.SetFloat(MixtureWeightId, Mathf.Clamp01(MixtureWeight));
            shader.SetInt(MinSamplesId, Mathf.Max(1, MinimumSamples));
            shader.SetVector(GridMinId, _gridMin);
            shader.SetVector(GridMaxId, _gridMax);
            shader.SetInt(GridResolutionId, GridResolution);
            shader.SetInt(DirectionBinCountId, DirectionBinCount);
            EnsureInertResources();
            shader.SetBuffer(kernel, TrainingId, _training ?? _inertTraining);
            shader.SetBuffer(kernel, CdfId, _cdf ?? _inertCdf);
            shader.SetBuffer(kernel, ObservationCountsId, _observationCounts ?? _inertObservationCounts);
        }

        public void Invalidate()
        {
            if (_training == null) return;
            _training.SetData(new uint[TrainingCount]);
            _cdf.SetData(CreateUniformCdf());
            _observationCounts.SetData(new uint[CellCount]);
        }

        public int AddStateHash(int hash)
        {
            unchecked
            {
                hash = hash * 31 + (Enabled ? 1 : 0);
                hash = hash * 31 + Mathf.RoundToInt(MixtureWeight * 10000.0f);
                hash = hash * 31 + MinimumSamples;
                return hash;
            }
        }

        public void ReleaseGuideResources()
        {
            _training?.Release();
            _cdf?.Release();
            _observationCounts?.Release();
            _training = null;
            _cdf = null;
            _observationCounts = null;
            _rebuildKernel = -1;
            _rebuildShader = null;
        }

        public void ReleaseResources()
        {
            ReleaseGuideResources();
            _inertTraining?.Release();
            _inertCdf?.Release();
            _inertObservationCounts?.Release();
            _inertTraining = null;
            _inertCdf = null;
            _inertObservationCounts = null;
        }

        private void EnsureInertResources()
        {
            if (_inertTraining != null) return;

            _inertTraining = new ComputeBuffer(1, sizeof(uint));
            _inertCdf = new ComputeBuffer(1, sizeof(float));
            _inertObservationCounts = new ComputeBuffer(1, sizeof(uint));
        }

        private static float[] CreateUniformCdf()
        {
            var cdf = new float[TrainingCount];
            for (var cell = 0; cell < CellCount; cell++)
            {
                var baseIndex = cell * DirectionBinCount;
                for (var bin = 0; bin < DirectionBinCount; bin++)
                {
                    cdf[baseIndex + bin] = (bin + 1) / (float)DirectionBinCount;
                }
            }

            return cdf;
        }

        private static void ComputeShaderDispatch(ComputeShader shader, int kernel, int elementCount)
        {
            uint x;
            uint y;
            uint z;
            shader.GetKernelThreadGroupSizes(kernel, out x, out y, out z);
            shader.Dispatch(kernel, Mathf.CeilToInt(elementCount / (float)x), 1, 1);
        }
    }
}
