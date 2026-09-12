// Parsers for the binary assets the plugin bakes. Layouts are documented in
// protocol/PROTOCOL.md and produced by plugin/mesh_bake.cpp. Values are left
// in Daz units/space here; DazSpace converts when the Unity mesh is built.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DazVrBridge
{
    public sealed class DzmMesh
    {
        public struct Group { public int Start; public int Count; public int Material; }

        public float[] Positions;   // 3 per vertex, cm, figure/node local
        public float[] Uvs;         // 2 per vertex, or null
        public int[] Triangles;     // 3 per triangle, Daz winding
        public List<Group> Groups = new List<Group>();

        public int VertexCount => Positions.Length / 3;
        public int TriangleCount => Triangles.Length / 3;

        public static DzmMesh Parse(byte[] bytes)
        {
            using (var r = new BinaryReader(new MemoryStream(bytes), Encoding.ASCII))
            {
                var magic = Encoding.ASCII.GetString(r.ReadBytes(4));
                if (magic != "DZM1") throw new InvalidDataException($"not a DZM1 mesh chunk ({magic})");

                var vertCount = (int)r.ReadUInt32();
                var triCount = (int)r.ReadUInt32();
                var flags = r.ReadUInt32();
                var groupCount = (int)r.ReadUInt32();

                var m = new DzmMesh { Positions = new float[vertCount * 3] };
                for (var i = 0; i < m.Positions.Length; i++) m.Positions[i] = r.ReadSingle();

                if ((flags & 1) != 0)
                {
                    m.Uvs = new float[vertCount * 2];
                    for (var i = 0; i < m.Uvs.Length; i++) m.Uvs[i] = r.ReadSingle();
                }

                m.Triangles = new int[triCount * 3];
                for (var i = 0; i < m.Triangles.Length; i++) m.Triangles[i] = (int)r.ReadUInt32();

                for (var g = 0; g < groupCount; g++)
                {
                    var start = (int)r.ReadUInt32();
                    var count = (int)r.ReadUInt32();
                    var mat = (int)r.ReadUInt16();
                    r.ReadUInt16(); // pad
                    m.Groups.Add(new Group { Start = start, Count = count, Material = mat });
                }
                return m;
            }
        }
    }

    public sealed class DzsSkin
    {
        public int Influences;      // per vertex, 4 or 8
        public ushort[] Bones;      // Influences per vertex, index into the figure's bone list
        public float[] Weights;     // Influences per vertex, normalized

        public int VertexCount => Weights.Length / Influences;

        public static DzsSkin Parse(byte[] bytes)
        {
            using (var r = new BinaryReader(new MemoryStream(bytes), Encoding.ASCII))
            {
                var magic = Encoding.ASCII.GetString(r.ReadBytes(4));
                if (magic != "DZS1") throw new InvalidDataException($"not a DZS1 skin chunk ({magic})");

                var vertCount = (int)r.ReadUInt32();
                var influences = r.ReadByte();
                r.ReadBytes(3); // pad

                var s = new DzsSkin
                {
                    Influences = influences,
                    Bones = new ushort[vertCount * influences],
                    Weights = new float[vertCount * influences],
                };
                for (var v = 0; v < vertCount; v++)
                {
                    for (var i = 0; i < influences; i++) s.Bones[v * influences + i] = r.ReadUInt16();
                    for (var i = 0; i < influences; i++) s.Weights[v * influences + i] = r.ReadSingle();
                }
                return s;
            }
        }
    }
}
