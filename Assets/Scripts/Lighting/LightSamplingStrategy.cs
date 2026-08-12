namespace PathTracing.Lighting
{
    public enum LightSamplingStrategy
    {
        // Sample every light at each shading point. Most accurate per frame, cost scales with light count.
        AllLights = 0,

        // Pick one light at random per shading point, weighted by light count. O(1) lights per hit, noisier per frame.
        UniformRandom = 1,

        // Pick lights weighted by a cheap power/distance estimate, then divide by selection probability.
        // Unbiased like UniformRandom but concentrates samples on lights that matter, so much less noise per sample.
        ImportanceSampled = 2
    }
}