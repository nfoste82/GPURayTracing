namespace PathTracing.Caustics
{
    public struct CausticTargetPair
    {
        public int lightIndex;
        public int refractorType;
        public int refractorIndex;
        public int triangleStart;
        public int triangleCount;
        public float cumulativeProbability;
        public float selectionProbability;
        public float padding;
    }
}