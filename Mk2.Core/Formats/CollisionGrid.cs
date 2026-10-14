using System.Numerics;
using Mk2.IO;

namespace Mk2.Formats
{
    public struct CollisionTri
    {
        public int First, Second, Third;
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

        public static Collision Load(byte[] data, string name = null)
        {
            var reader = new Bin(data);
            int version = reader.S32(0);
            if (version < 0 || version > 0xFFFF) reader = new Bin(data, true);
            var collision = new Collision { Name = name };
            collision.CellsX = reader.S32(4); collision.CellsY = reader.S32(8); collision.CellsZ = reader.S32(12);
            collision.CellSize = new Vector3(reader.F32(0x10), reader.F32(0x14), reader.F32(0x18));
            collision.BoundsMin = new Vector3(reader.F32(0x20), reader.F32(0x24), reader.F32(0x28));
            collision.BoundsMax = new Vector3(reader.F32(0x30), reader.F32(0x34), reader.F32(0x38));
            int matCount = reader.S32(0x40);
            int vtxSize = reader.S32(0x44);
            int cellSize = reader.S32(0xC8);
            int triSize = reader.S32(0xCC);
            int cells = collision.CellsX * collision.CellsY * collision.CellsZ;
            if (cells <= 0 || cells > 1_000_000) throw new InvalidDataException("Bad collision grid size");

            int cellOff = 0xD0;
            int triOff = cellOff + cellSize;
            int vtxOff = triOff + triSize;
            int matOff = vtxOff + vtxSize;
            int vtxCount = vtxSize / 16;
            int triCount = 0;
            for (int index = 0; index < cells; index++)
                triCount = Math.Max(triCount, reader.U16(cellOff + index * 4) + reader.U16(cellOff + index * 4 + 2));
            triCount = Math.Min(triCount, triSize / 14);

            for (int index = 0; index < vtxCount; index++)
            {
                int offset = vtxOff + index * 16;
                collision.Vertices.Add(new Vector3(reader.F32(offset), reader.F32(offset + 4), reader.F32(offset + 8)));
                collision.VertexColors.Add(reader.U32(offset + 12));
            }
            var seen = new HashSet<(int, int, int)>();
            for (int index = 0; index < triCount; index++)
            {
                int offset = triOff + index * 14;
                var triangle = new CollisionTri
                {
                    Extra = data.AsSpan(offset, 6).ToArray(),
                    First = reader.U16(offset + 6), Second = reader.U16(offset + 8), Third = reader.U16(offset + 10),
                    Flags = reader.U16(offset + 12),
                };
                if (triangle.First >= vtxCount || triangle.Second >= vtxCount || triangle.Third >= vtxCount) continue;
                var key = Sort(triangle.First, triangle.Second, triangle.Third);
                if (!seen.Add(key)) continue;
                collision.Triangles.Add(triangle);
            }
            for (int index = 0; index < matCount && matOff + index * 32 < data.Length; index++)
                collision.Materials.Add(reader.FixedStr(matOff + index * 32, 32));
            return collision;
        }

        private static (int, int, int) Sort(int first, int second, int third)
        {
            if (first > second) (first, second) = (second, first);
            if (second > third) (second, third) = (third, second);
            if (first > second) (first, second) = (second, first);
            return (first, second, third);
        }

        public string MaterialName(CollisionTri triangle) =>
            triangle.Material < Materials.Count ? Materials[triangle.Material] : $"#{triangle.Material}";
    }
}
