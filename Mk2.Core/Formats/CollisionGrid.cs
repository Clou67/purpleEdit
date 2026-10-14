using System.Numerics;
using Mk2.IO;

namespace Mk2.Formats
{
    public struct CollisionTri
    {
        public int A, B, C;
        public ushort Flags;            // low byte = material index, high byte = surface/edge flags
        public byte[] Extra;            // 6 unknown bytes per triangle (edge data?)
        public int Material => Flags & 0xFF;
    }

    /// <summary>
    /// Collision grid (".cgr"). Level ground uses "&lt;ground&gt;_all.cgr", props use "&lt;model&gt;_NN.cgr".
    ///
    ///   0x00 u32 version (2)
    ///   0x04 u32 cellsX, 0x08 u32 cellsY, 0x0C u32 cellsZ
    ///   0x10 vec4 cellSize, 0x20 vec4 boundsMin, 0x30 vec4 boundsMax
    ///   0x40 u32 materialCount
    ///   0x44 u32 vertexDataSize (bytes)
    ///   0x48..0xC3 runtime pointers (garbage on disk)
    ///   0xC8 u32 cellTableSize (bytes, 16-aligned), 0xCC u32 triangleDataSize (bytes, 16-aligned)
    ///   0xD0 cells[X*Y*Z]: u16 firstTriangle, u16 triangleCount   (padded with 'X')
    ///   triangles: 14 bytes each: u8[6] ?, u16 a, u16 b, u16 c, u16 flags  (padded with 'X')
    ///   vertices: 16 bytes each: f32 x, y, z, u8 r, g, b, a
    ///   materials: char[32] each ("CM_Wall", ...)
    /// Triangles are stored per cell, so the same face can appear in several cells.
    /// </summary>
    public sealed class Collision
    {
        public string Name;
        public int CellsX, CellsY, CellsZ;
        public Vector3 CellSize, BoundsMin, BoundsMax;
        public List<Vector3> Vertices = new();
        public List<uint> VertexColors = new();
        public List<CollisionTri> Triangles = new();     // de-duplicated
        public List<string> Materials = new();

        public static Collision Load(byte[] d, string name = null)
        {
            var b = new Bin(d);
            int ver = b.S32(0);
            if (ver < 0 || ver > 0xFFFF) b = new Bin(d, true);
            var c = new Collision { Name = name };
            c.CellsX = b.S32(4); c.CellsY = b.S32(8); c.CellsZ = b.S32(12);
            c.CellSize = new Vector3(b.F32(0x10), b.F32(0x14), b.F32(0x18));
            c.BoundsMin = new Vector3(b.F32(0x20), b.F32(0x24), b.F32(0x28));
            c.BoundsMax = new Vector3(b.F32(0x30), b.F32(0x34), b.F32(0x38));
            int matCount = b.S32(0x40);
            int vtxSize = b.S32(0x44);
            int cellSize = b.S32(0xC8);
            int triSize = b.S32(0xCC);
            int cells = c.CellsX * c.CellsY * c.CellsZ;
            if (cells <= 0 || cells > 1_000_000) throw new InvalidDataException("Bad collision grid size");

            int cellOff = 0xD0;
            int triOff = cellOff + cellSize;
            int vtxOff = triOff + triSize;
            int matOff = vtxOff + vtxSize;
            int vtxCount = vtxSize / 16;
            int triCount = 0;
            for (int i = 0; i < cells; i++)
                triCount = Math.Max(triCount, b.U16(cellOff + i * 4) + b.U16(cellOff + i * 4 + 2));
            triCount = Math.Min(triCount, triSize / 14);

            for (int i = 0; i < vtxCount; i++)
            {
                int o = vtxOff + i * 16;
                c.Vertices.Add(new Vector3(b.F32(o), b.F32(o + 4), b.F32(o + 8)));
                c.VertexColors.Add(b.U32(o + 12));
            }
            var seen = new HashSet<(int, int, int)>();
            for (int i = 0; i < triCount; i++)
            {
                int o = triOff + i * 14;
                var t = new CollisionTri
                {
                    Extra = d.AsSpan(o, 6).ToArray(),
                    A = b.U16(o + 6), B = b.U16(o + 8), C = b.U16(o + 10),
                    Flags = b.U16(o + 12),
                };
                if (t.A >= vtxCount || t.B >= vtxCount || t.C >= vtxCount) continue;
                var key = Sort(t.A, t.B, t.C);
                if (!seen.Add(key)) continue;
                c.Triangles.Add(t);
            }
            for (int i = 0; i < matCount && matOff + i * 32 < d.Length; i++)
                c.Materials.Add(b.FixedStr(matOff + i * 32, 32));
            return c;
        }

        private static (int, int, int) Sort(int a, int b, int c)
        {
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            return (a, b, c);
        }

        public string MaterialName(CollisionTri t) =>
            t.Material < Materials.Count ? Materials[t.Material] : $"#{t.Material}";
    }
}
