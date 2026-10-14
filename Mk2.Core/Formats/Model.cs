using System.Numerics;
using Mk2.IO;

namespace Mk2.Formats
{
    public sealed class ModelVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 UV;
        public Vector4 Color = Vector4.One;   // 0..1, PS2 colours are scaled so 0x80 = 1.0
        /// <summary>Skeleton bone indices (already remapped through the mesh's anim node list). 0xFF = no valid bone.</summary>
        public byte Bone0, Bone1;
        /// <summary>The node indices as stored in the file, before remapping.</summary>
        public byte Node0, Node1;
        public float Weight0 = 1f;
    }

    /// <summary>One triangle strip as sent to VU1 (one MSCAL batch).</summary>
    public sealed class ModelStrip
    {
        public List<ModelVertex> Vertices = new();
        public uint HeaderFlags;     // 4th word of the per-strip header qword (usually 0xFFFFFFFF)
        public bool Skinned;
    }

    public sealed class ModelMesh
    {
        public int MaterialIndex;
        public int ComponentIndex;
        public int MdgOffset;
        public int AnimNodeList = 0xFFFF;
        public List<ModelStrip> Strips = new();
        public bool Skinned;

        /// <summary>Expands strips to a triangle list (indices into Vertices).</summary>
        public void Triangulate(List<ModelVertex> verts, List<int> indices)
        {
            foreach (var s in Strips)
            {
                int b = verts.Count;
                verts.AddRange(s.Vertices);
                for (int i = 0; i + 2 < s.Vertices.Count; i++)
                {
                    if ((i & 1) == 0) { indices.Add(b + i); indices.Add(b + i + 1); indices.Add(b + i + 2); }
                    else { indices.Add(b + i + 1); indices.Add(b + i); indices.Add(b + i + 2); }
                }
            }
        }
    }

    public sealed class ModelComponent
    {
        public string Name;
        public string Extra;
        public Vector3 BoundsMin;
        public float Radius;
        public Vector3 BoundsSize;
        public int BoneCount;          // u8 at +0x38 (?)
        /// <summary>Bone that moves this whole component when its meshes aren't skinned (u16 at +0x3A, -1 = none).</summary>
        public int RigidBone = -1;
        public List<ModelMesh> Meshes = new();

        /// <summary>Krome convention: c_ = collision proxy, f_ = shatter fragment.</summary>
        public bool IsCollision => Name != null && Name.StartsWith("c_", StringComparison.OrdinalIgnoreCase);
        public bool IsFragment => Name != null && Name.StartsWith("f_", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class RefPoint
    {
        public string Name;
        public Vector3 Position;
    }

    /// <summary>
    /// MDL3 header + MDG3 "guts" pair (PS2).
    ///
    /// MDL3 header:
    ///   0x00 "MDL3"
    ///   0x04 u16 componentCount   0x06 u16 materialCount   0x08 u16 animNodeCount
    ///   0x0A u16 refPointCount    0x0E u16 meshCount        0x10 u16 animNodeListCount
    ///   0x14 u32 vertexCount      0x18 u32 triangleCount    0x1E u16 stripCount
    ///   0x30 vec4 boundsMin (w = radius), 0x40 vec4 boundsSize
    ///   0x50 u32 componentTable (0x40 per component)
    ///   0x54 u32 materialNameTable (u32 string offsets)
    ///   0x58 u32 refPointTable (u32 offsets to 0x70-byte ref points: vec4 pos, u32 name, ...)
    ///   0x5C u32 animNodeTable (vec4 bind position per node)
    ///   0x64 u32 animNodeLists (128-byte bone remap tables: u8 count, then count bone indices)
    ///   0x68 u32 meshLookupTable [material][component] -> MDG offset of first mesh (0 = none)
    /// Component: vec4 boundsMin, vec4 boundsSize, ..., 0x30 u32 name, 0x34 u32 extra, 0x38 u8 ?, 0x3A u16 rigid bone (0xFFFF = none)
    ///
    /// MDG3 mesh header (0x10): u16 ?, u16 ?, u16 ?, u16 stripCount, u16 animNodeList, u16 ?, u32 nextMesh
    /// followed by a PS2 DMA chain of VIF packets. Each strip is:
    ///   UNPACK V4-32 @0 (2 qw: [vertexCount, ?, 0xCDCDCDCD, flags], bounding sphere)
    ///   STCYCL 4/1
    ///   UNPACK V3-32 @2 positions
    ///   UNPACK V3-8  @3 normals          | skinned: V4-8  @3 normal + bone0*2
    ///   UNPACK V2-16 @4 uv (4096 = 1.0)  | skinned: V4-16 @4 uv, weight0*4096, bone1*4
    ///   UNPACK V4-8  @5 colour (0x80 = 1.0)
    ///   MSCAL
    /// </summary>
    public sealed class Model
    {
        public string Name;
        public List<string> MaterialNames = new();
        public List<ModelComponent> Components = new();
        public List<ModelMesh> Meshes = new();
        public List<RefPoint> RefPoints = new();
        public List<Vector3> AnimNodePositions = new();
        public List<byte[]> AnimNodeLists = new();
        public Vector3 BoundsMin, BoundsSize;
        public float Radius;
        public int DeclaredVertexCount, DeclaredTriangleCount, DeclaredStripCount;
        public List<string> Warnings = new();

        public Vector3 BoundsMax => BoundsMin + BoundsSize;

        public static Model Load(byte[] mdl, byte[] mdg, string name = null)
        {
            var b = new Bin(mdl);
            if (mdl.Length < 0x70 || b.CStr(0, 4) != "MDL3")
            {
                // GameCube files are big endian.
                if (mdl.Length >= 4 && mdl[0] == '3' && mdl[1] == 'L' && mdl[2] == 'D' && mdl[3] == 'M')
                    throw new NotSupportedException("Big-endian (GameCube) MDL3 is not supported yet");
                throw new InvalidDataException("Not an MDL3 file");
            }
            var m = new Model { Name = name };
            int compCount = b.U16(4), matCount = b.U16(6), nodeCount = b.U16(8), refCount = b.U16(0xA);
            int anlCount = b.U16(0x10);
            m.DeclaredVertexCount = b.S32(0x14);
            m.DeclaredTriangleCount = b.S32(0x18);
            m.DeclaredStripCount = b.U16(0x1E);
            m.BoundsMin = new Vector3(b.F32(0x30), b.F32(0x34), b.F32(0x38));
            m.Radius = b.F32(0x3C);
            m.BoundsSize = new Vector3(b.F32(0x40), b.F32(0x44), b.F32(0x48));
            int compOff = b.S32(0x50), matOff = b.S32(0x54), refOff = b.S32(0x58), nodeOff = b.S32(0x5C);
            int anlOff = b.S32(0x64), lutOff = b.S32(0x68);

            for (int i = 0; i < matCount; i++) m.MaterialNames.Add(b.CStr(b.S32(matOff + i * 4)));

            for (int c = 0; c < compCount; c++)
            {
                int o = compOff + c * 0x40;
                m.Components.Add(new ModelComponent
                {
                    BoundsMin = new Vector3(b.F32(o), b.F32(o + 4), b.F32(o + 8)),
                    Radius = b.F32(o + 12),
                    BoundsSize = new Vector3(b.F32(o + 16), b.F32(o + 20), b.F32(o + 24)),
                    Name = b.CStr(b.S32(o + 0x30)),
                    Extra = b.CStr(b.S32(o + 0x34)),
                    BoneCount = b.U8(o + 0x38),
                    RigidBone = b.U16(o + 0x3A) == 0xFFFF ? -1 : b.U16(o + 0x3A),
                });
            }

            try
            {
                for (int i = 0; i < refCount && refOff > 0; i++)
                {
                    int o = b.S32(refOff + i * 4);
                    if (o <= 0 || o + 0x14 > mdl.Length) continue;
                    m.RefPoints.Add(new RefPoint
                    {
                        Position = new Vector3(b.F32(o), b.F32(o + 4), b.F32(o + 8)),
                        Name = b.CStr(b.S32(o + 0x10)),
                    });
                }
                for (int i = 0; i < nodeCount && nodeOff > 0 && nodeOff + i * 16 + 12 <= mdl.Length; i++)
                    m.AnimNodePositions.Add(new Vector3(b.F32(nodeOff + i * 16), b.F32(nodeOff + i * 16 + 4), b.F32(nodeOff + i * 16 + 8)));
                for (int i = 0; i < anlCount && anlOff > 0; i++)
                {
                    int o = anlOff + i * 0x80;
                    int len = Math.Max(0, Math.Min(0x80, mdl.Length - o));
                    m.AnimNodeLists.Add(mdl.AsSpan(o, len).ToArray());
                }
            }
            catch (Exception ex) { m.Warnings.Add("ref/anim tables: " + ex.Message); }

            var g = new Bin(mdg);
            if (mdg.Length < 4 || g.CStr(0, 4) != "MDG3") throw new InvalidDataException("Not an MDG3 file");

            for (int t = 0; t < matCount; t++)
            {
                for (int c = 0; c < compCount; c++)
                {
                    int off = b.S32(lutOff + (t * compCount + c) * 4);
                    int guard = 0;
                    while (off > 0 && off < mdg.Length && guard++ < 10000)
                    {
                        var mesh = new ModelMesh { MaterialIndex = t, ComponentIndex = c, MdgOffset = off };
                        int next;
                        try { next = ReadMesh(g, off, mesh); }
                        catch (Exception ex)
                        {
                            m.Warnings.Add($"mesh @0x{off:X}: {ex.Message}");
                            break;
                        }
                        RemapBones(m, mesh);
                        m.Meshes.Add(mesh);
                        m.Components[c].Meshes.Add(mesh);
                        off = next;
                    }
                }
            }
            return m;
        }

        /// <summary>
        /// Skinned vertices store a node index into the mesh's anim node list. Each list is 0x80 bytes:
        /// u8 count, then count bone indices. Converts Bone0/Bone1 into skeleton bone indices.
        /// </summary>
        public const byte NoBone = 0xFF;

        /// <summary>The bones listed in a mesh's anim node list, or an empty list.</summary>
        public IReadOnlyList<int> NodeListBones(ModelMesh mesh)
        {
            if (mesh.AnimNodeList == 0xFFFF || mesh.AnimNodeList >= AnimNodeLists.Count) return Array.Empty<int>();
            var list = AnimNodeLists[mesh.AnimNodeList];
            int listLength = list.Length > 0 ? Math.Min(list[0], list.Length - 1) : 0;
            var bones = new int[listLength];
            for (int index = 0; index < listLength; index++) bones[index] = list[index + 1];
            return bones;
        }

        private static void RemapBones(Model m, ModelMesh mesh)
        {
            if (!mesh.Skinned || mesh.AnimNodeList == 0xFFFF) return;
            if (mesh.AnimNodeList >= m.AnimNodeLists.Count)
            {
                m.Warnings.Add($"mesh @0x{mesh.MdgOffset:X}: anim node list {mesh.AnimNodeList} out of range");
                return;
            }
            var list = m.AnimNodeLists[mesh.AnimNodeList];
            int listLength = list.Length > 0 ? Math.Min(list[0], list.Length - 1) : 0;
            byte Map(byte node) => node < listLength ? list[node + 1] : NoBone;
            foreach (var strip in mesh.Strips)
            {
                if (!strip.Skinned) continue;
                foreach (var vertex in strip.Vertices)
                {
                    vertex.Bone0 = Map(vertex.Node0);
                    vertex.Bone1 = Map(vertex.Node1);
                }
            }
        }

        private static int ReadMesh(Bin g, int off, ModelMesh mesh)
        {
            int stripCount = g.U16(off + 6);
            mesh.AnimNodeList = g.U16(off + 8);
            int next = g.S32(off + 12);

            int p = off + 0x10;
            uint tag = g.U32(p);
            int end = p + 16 + (int)(tag & 0xFFFF) * 16;
            p += 8;

            var unpacked = new Dictionary<int, (int cmd, int num, int at)>();
            int guard = 0;
            while (mesh.Strips.Count < stripCount)
            {
                if (++guard > 1_000_000) throw new InvalidDataException("VIF parse runaway");
                if (p >= end)
                {
                    p = Bin.Align(p, 16);
                    if (p + 16 > g.Length) throw new InvalidDataException("DMA chain ran off end of file");
                    tag = g.U32(p);
                    int id = (int)(tag >> 28) & 7;
                    end = p + 16 + (int)(tag & 0xFFFF) * 16;
                    p += 8;
                    if (id == 7 && (tag & 0xFFFF) == 0) throw new InvalidDataException("DMA end before all strips");
                    continue;
                }
                uint code = g.U32(p); p += 4;
                int cmd = (int)(code >> 24) & 0x7F;
                int num = (int)(code >> 16) & 0xFF;
                int imm = (int)(code & 0xFFFF);
                if ((cmd & 0x60) == 0x60)
                {
                    int vn = ((cmd >> 2) & 3) + 1, vl = cmd & 3;
                    if (num == 0) num = 256;
                    int esz = vl == 3 ? 2 : vn * (4 >> vl);
                    unpacked[imm & 0x3FF] = (cmd, num, p);
                    p += Bin.Align(num * esz, 4);
                }
                else switch (cmd)
                {
                    case 0x14: case 0x15: case 0x17:   // MSCAL / MSCALF / MSCNT -> strip complete
                        mesh.Strips.Add(BuildStrip(g, unpacked, mesh));
                        unpacked.Clear();
                        break;
                    case 0x20: p += 4; break;                        // STMASK
                    case 0x30: case 0x31: p += 16; break;            // STROW / STCOL
                    case 0x4A: p += Bin.Align(num * 8, 4) ; break;   // MPG (not expected)
                    case 0x50: case 0x51: p += (imm == 0 ? 65536 : imm) * 16; break; // DIRECT/DIRECTHL
                    case 0x00: case 0x01: case 0x02: case 0x03: case 0x04: case 0x05:
                    case 0x06: case 0x07: case 0x10: case 0x11: case 0x13:
                        break;
                    default:
                        throw new InvalidDataException($"Unknown VIF command 0x{cmd:X2} at 0x{p - 4:X}");
                }
            }
            return next;
        }

        private static ModelStrip BuildStrip(Bin g, Dictionary<int, (int cmd, int num, int at)> u, ModelMesh mesh)
        {
            var s = new ModelStrip();
            if (!u.TryGetValue(2, out var pos)) return s;
            if (u.TryGetValue(0, out var hdr) && hdr.cmd == 0x6C) s.HeaderFlags = g.U32(hdr.at + 12);
            int n = pos.num;
            for (int i = 0; i < n; i++)
            {
                var v = new ModelVertex();
                if (pos.cmd == 0x68 || pos.cmd == 0x6C)
                {
                    int stride = pos.cmd == 0x6C ? 16 : 12;
                    int o = pos.at + i * stride;
                    v.Position = new Vector3(g.F32(o), g.F32(o + 4), g.F32(o + 8));
                }
                s.Vertices.Add(v);
            }
            if (u.TryGetValue(3, out var nrm))
            {
                for (int i = 0; i < Math.Min(n, nrm.num); i++)
                {
                    var v = s.Vertices[i];
                    if (nrm.cmd == 0x6A)        // V3-8
                    {
                        int o = nrm.at + i * 3;
                        v.Normal = new Vector3(g.S8(o), g.S8(o + 1), g.S8(o + 2)) / 127f;
                    }
                    else if (nrm.cmd == 0x6E)   // V4-8 : normal + bone0<<1
                    {
                        int o = nrm.at + i * 4;
                        v.Normal = new Vector3(g.S8(o), g.S8(o + 1), g.S8(o + 2)) / 127f;
                        v.Node0 = v.Bone0 = (byte)(g.U8(o + 3) >> 1);
                        s.Skinned = true;
                        mesh.Skinned = true;
                    }
                }
            }
            if (u.TryGetValue(4, out var uv))
            {
                for (int i = 0; i < Math.Min(n, uv.num); i++)
                {
                    var v = s.Vertices[i];
                    if (uv.cmd == 0x65)         // V2-16
                    {
                        int o = uv.at + i * 4;
                        v.UV = new Vector2(g.S16(o), g.S16(o + 2)) / 4096f;
                    }
                    else if (uv.cmd == 0x6D)    // V4-16 : uv, weight, bone1<<2
                    {
                        int o = uv.at + i * 8;
                        v.UV = new Vector2(g.S16(o), g.S16(o + 2)) / 4096f;
                        v.Weight0 = Math.Clamp(g.S16(o + 4) / 4096f, 0f, 1f);
                        v.Node1 = v.Bone1 = (byte)(g.U16(o + 6) >> 2);
                    }
                }
            }
            if (u.TryGetValue(5, out var col) && col.cmd == 0x6E)
            {
                for (int i = 0; i < Math.Min(n, col.num); i++)
                {
                    int o = col.at + i * 4;
                    s.Vertices[i].Color = new Vector4(g.U8(o), g.U8(o + 1), g.U8(o + 2), g.U8(o + 3)) / 128f;
                }
            }
            return s;
        }

        /// <summary>Total triangle count across all strips.</summary>
        public int TriangleCount => Meshes.Sum(m => m.Strips.Sum(s => Math.Max(0, s.Vertices.Count - 2)));
        public int VertexCount => Meshes.Sum(m => m.Strips.Sum(s => s.Vertices.Count));
    }
}
