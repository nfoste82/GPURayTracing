using System.Collections.Generic;
using PathTracing.Shapes;

namespace PathTracing.AccelerationStructures
{
    public sealed class MeshBvhTemplate
    {
        public readonly List<Triangle> triangles = new ();
        public readonly List<BvhNode> nodes = new ();
    }
}