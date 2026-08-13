using System.Collections.Generic;
using System.IO;
using PathTracing.Shapes;
using UnityEngine;

namespace PathTracing.AccelerationStructures
{
    public static class MeshBvhBakeSerializer
    {
        public const int Magic = 0x48564252;

        public static void Write(string path, RayTracingBvhBakeAsset asset, MeshBvhTemplateCache templates,
            int leafTriangleCount, int stackSize, float boundsPadding)
        {
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            writer.Write(Magic);
            writer.Write(asset.formatVersion);
            writer.Write(asset.meshes.Count);
            for (int meshIndex = 0; meshIndex < asset.meshes.Count; meshIndex++)
            {
                RayTracingBvhBakeAsset.MeshEntry entry = asset.meshes[meshIndex];
                MeshBvhTemplate template = templates.GetOrBuild(entry.mesh, entry.interpolateNormals, leafTriangleCount,
                    stackSize, boundsPadding, out _);
                writer.Write(template.triangles.Count);
                writer.Write(template.nodes.Count);
                for (int i = 0; i < template.triangles.Count; i++) WriteTriangle(writer, template.triangles[i]);
                for (int i = 0; i < template.nodes.Count; i++) WriteNode(writer, template.nodes[i]);
            }
        }

        public static List<MeshBvhTemplate> Read(BinaryReader reader, int formatVersion, int expectedMeshCount)
        {
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != formatVersion) return null;
            int meshCount = reader.ReadInt32();
            if (meshCount != expectedMeshCount) return null;
            var templates = new List<MeshBvhTemplate>(meshCount);
            for (int meshIndex = 0; meshIndex < meshCount; meshIndex++)
            {
                int triangleCount = reader.ReadInt32();
                int nodeCount = reader.ReadInt32();
                var template = new MeshBvhTemplate();
                for (int i = 0; i < triangleCount; i++) template.triangles.Add(ReadTriangle(reader));
                for (int i = 0; i < nodeCount; i++) template.nodes.Add(ReadNode(reader));
                templates.Add(template);
            }
            return templates;
        }

        private static Triangle ReadTriangle(BinaryReader reader) => new()
        {
            vertex0 = ReadVector3(reader), vertex1 = ReadVector3(reader), vertex2 = ReadVector3(reader),
            normal = ReadVector3(reader), normal0 = ReadVector3(reader), normal1 = ReadVector3(reader), normal2 = ReadVector3(reader),
            tangent0 = ReadVector4(reader), tangent1 = ReadVector4(reader), tangent2 = ReadVector4(reader),
            uv0 = ReadVector2(reader), uv1 = ReadVector2(reader), uv2 = ReadVector2(reader), interpolateNormals = reader.ReadInt32()
        };

        private static BvhNode ReadNode(BinaryReader reader) => new()
        {
            boundsMin = ReadVector3(reader), leftChildIndex = reader.ReadInt32(), boundsMax = ReadVector3(reader),
            rightChildIndex = reader.ReadInt32(), triangleStart = reader.ReadInt32(), triangleCount = reader.ReadInt32()
        };

        private static void WriteTriangle(BinaryWriter writer, Triangle triangle)
        {
            WriteVector3(writer, triangle.vertex0); WriteVector3(writer, triangle.vertex1); WriteVector3(writer, triangle.vertex2);
            WriteVector3(writer, triangle.normal); WriteVector3(writer, triangle.normal0); WriteVector3(writer, triangle.normal1); WriteVector3(writer, triangle.normal2);
            WriteVector4(writer, triangle.tangent0); WriteVector4(writer, triangle.tangent1); WriteVector4(writer, triangle.tangent2);
            WriteVector2(writer, triangle.uv0); WriteVector2(writer, triangle.uv1); WriteVector2(writer, triangle.uv2); writer.Write(triangle.interpolateNormals);
        }

        private static void WriteNode(BinaryWriter writer, BvhNode node)
        {
            WriteVector3(writer, node.boundsMin); writer.Write(node.leftChildIndex); WriteVector3(writer, node.boundsMax);
            writer.Write(node.rightChildIndex); writer.Write(node.triangleStart); writer.Write(node.triangleCount);
        }

        private static Vector2 ReadVector2(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle());
        private static Vector3 ReadVector3(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        private static Vector4 ReadVector4(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        private static void WriteVector2(BinaryWriter writer, Vector2 value) { writer.Write(value.x); writer.Write(value.y); }
        private static void WriteVector3(BinaryWriter writer, Vector3 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
        private static void WriteVector4(BinaryWriter writer, Vector4 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); writer.Write(value.w); }
    }
}
