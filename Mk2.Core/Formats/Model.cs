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
        public void Triangulate(List<ModelVertex> vertices, List<int> indices)
        {
            foreach (var strip in Strips)
            {
                int firstVertex = vertices.Count;
                vertices.AddRange(strip.Vertices);
                for (int index = 0; index + 2 < strip.Vertices.Count; index++)
                {
                    if ((index & 1) == 0) { indices.Add(firstVertex + index); indices.Add(firstVertex + index + 1); indices.Add(firstVertex + index + 2); }
                    else { indices.Add(firstVertex + index + 1); indices.Add(firstVertex + index); indices.Add(firstVertex + index + 2); }
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
            var reader = new Bin(mdl);
            if (mdl.Length < 0x70 || reader.CStr(0, 4) != "MDL3")
            {
                // GameCube files are big endian.
                if (mdl.Length >= 4 && mdl[0] == '3' && mdl[1] == 'L' && mdl[2] == 'D' && mdl[3] == 'M')
                    throw new NotSupportedException("Big-endian (GameCube) MDL3 is not supported yet");
                throw new InvalidDataException("Not an MDL3 file");
            }
            var model = new Model { Name = name };
            int compCount = reader.U16(4), matCount = reader.U16(6), nodeCount = reader.U16(8), refCount = reader.U16(0xA);
            int anlCount = reader.U16(0x10);
            model.DeclaredVertexCount = reader.S32(0x14);
            model.DeclaredTriangleCount = reader.S32(0x18);
            model.DeclaredStripCount = reader.U16(0x1E);
            model.BoundsMin = new Vector3(reader.F32(0x30), reader.F32(0x34), reader.F32(0x38));
            model.Radius = reader.F32(0x3C);
            model.BoundsSize = new Vector3(reader.F32(0x40), reader.F32(0x44), reader.F32(0x48));
            int compOff = reader.S32(0x50), matOff = reader.S32(0x54), refOff = reader.S32(0x58), nodeOff = reader.S32(0x5C);
            int anlOff = reader.S32(0x64), lutOff = reader.S32(0x68);

            for (int index = 0; index < matCount; index++) model.MaterialNames.Add(reader.CStr(reader.S32(matOff + index * 4)));

            for (int componentIndex = 0; componentIndex < compCount; componentIndex++)
            {
                int offset = compOff + componentIndex * 0x40;
                model.Components.Add(new ModelComponent
                {
                    BoundsMin = new Vector3(reader.F32(offset), reader.F32(offset + 4), reader.F32(offset + 8)),
                    Radius = reader.F32(offset + 12),
                    BoundsSize = new Vector3(reader.F32(offset + 16), reader.F32(offset + 20), reader.F32(offset + 24)),
                    Name = reader.CStr(reader.S32(offset + 0x30)),
                    Extra = reader.CStr(reader.S32(offset + 0x34)),
                    BoneCount = reader.U8(offset + 0x38),
                    RigidBone = reader.U16(offset + 0x3A) == 0xFFFF ? -1 : reader.U16(offset + 0x3A),
                });
            }

            try
            {
                for (int index = 0; index < refCount && refOff > 0; index++)
                {
                    int offset = reader.S32(refOff + index * 4);
                    if (offset <= 0 || offset + 0x14 > mdl.Length) continue;
                    model.RefPoints.Add(new RefPoint
                    {
                        Position = new Vector3(reader.F32(offset), reader.F32(offset + 4), reader.F32(offset + 8)),
                        Name = reader.CStr(reader.S32(offset + 0x10)),
                    });
                }
                for (int index = 0; index < nodeCount && nodeOff > 0 && nodeOff + index * 16 + 12 <= mdl.Length; index++)
                    model.AnimNodePositions.Add(new Vector3(reader.F32(nodeOff + index * 16), reader.F32(nodeOff + index * 16 + 4), reader.F32(nodeOff + index * 16 + 8)));
                for (int index = 0; index < anlCount && anlOff > 0; index++)
                {
                    int offset = anlOff + index * 0x80;
                    int length = Math.Max(0, Math.Min(0x80, mdl.Length - offset));
                    model.AnimNodeLists.Add(mdl.AsSpan(offset, length).ToArray());
                }
            }
            catch (Exception exception) { model.Warnings.Add("ref/anim tables: " + exception.Message); }

            var guts = new Bin(mdg);
            if (mdg.Length < 4 || guts.CStr(0, 4) != "MDG3") throw new InvalidDataException("Not an MDG3 file");

            for (int materialIndex = 0; materialIndex < matCount; materialIndex++)
            {
                for (int componentIndex = 0; componentIndex < compCount; componentIndex++)
                {
                    int offset = reader.S32(lutOff + (materialIndex * compCount + componentIndex) * 4);
                    int guard = 0;
                    while (offset > 0 && offset < mdg.Length && guard++ < 10000)
                    {
                        var mesh = new ModelMesh { MaterialIndex = materialIndex, ComponentIndex = componentIndex, MdgOffset = offset };
                        int next;
                        try { next = ReadMesh(guts, offset, mesh); }
                        catch (Exception exception)
                        {
                            model.Warnings.Add($"mesh @0x{offset:X}: {exception.Message}");
                            break;
                        }
                        RemapBones(model, mesh);
                        model.Meshes.Add(mesh);
                        model.Components[componentIndex].Meshes.Add(mesh);
                        offset = next;
                    }
                }
            }
            return model;
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

        private static void RemapBones(Model model, ModelMesh mesh)
        {
            if (!mesh.Skinned) return;
            if (mesh.AnimNodeList == 0xFFFF)
            {
                // Skinned strips without a node list (weapons, helmets) follow the component's rigid bone.
                if (model.Components[mesh.ComponentIndex].RigidBone < 0) return;
                foreach (var strip in mesh.Strips)
                    foreach (var vertex in strip.Vertices) vertex.Bone0 = vertex.Bone1 = NoBone;
                return;
            }
            if (mesh.AnimNodeList >= model.AnimNodeLists.Count)
            {
                model.Warnings.Add($"mesh @0x{mesh.MdgOffset:X}: anim node list {mesh.AnimNodeList} out of range");
                return;
            }
            var list = model.AnimNodeLists[mesh.AnimNodeList];
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

        private static int ReadMesh(Bin guts, int offset, ModelMesh mesh)
        {
            int stripCount = guts.U16(offset + 6);
            mesh.AnimNodeList = guts.U16(offset + 8);
            int next = guts.S32(offset + 12);

            int position = offset + 0x10;
            uint tag = guts.U32(position);
            int end = position + 16 + (int)(tag & 0xFFFF) * 16;
            position += 8;

            var unpacked = new Dictionary<int, (int cmd, int num, int at)>();
            int guard = 0;
            while (mesh.Strips.Count < stripCount)
            {
                if (++guard > 1_000_000) throw new InvalidDataException("VIF parse runaway");
                if (position >= end)
                {
                    position = Bin.Align(position, 16);
                    if (position + 16 > guts.Length) throw new InvalidDataException("DMA chain ran off end of file");
                    tag = guts.U32(position);
                    int id = (int)(tag >> 28) & 7;
                    end = position + 16 + (int)(tag & 0xFFFF) * 16;
                    position += 8;
                    if (id == 7 && (tag & 0xFFFF) == 0) throw new InvalidDataException("DMA end before all strips");
                    continue;
                }
                uint code = guts.U32(position); position += 4;
                int command = (int)(code >> 24) & 0x7F;
                int count = (int)(code >> 16) & 0xFF;
                int immediate = (int)(code & 0xFFFF);
                if ((command & 0x60) == 0x60)
                {
                    int elementCount = ((command >> 2) & 3) + 1, elementSize = command & 3;
                    if (count == 0) count = 256;
                    int elementBytes = elementSize == 3 ? 2 : elementCount * (4 >> elementSize);
                    unpacked[immediate & 0x3FF] = (command, count, position);
                    position += Bin.Align(count * elementBytes, 4);
                }
                else switch (command)
                {
                    case 0x14: case 0x15: case 0x17:   // MSCAL / MSCALF / MSCNT -> strip complete
                        mesh.Strips.Add(BuildStrip(guts, unpacked, mesh));
                        unpacked.Clear();
                        break;
                    case 0x20: position += 4; break;                        // STMASK
                    case 0x30: case 0x31: position += 16; break;            // STROW / STCOL
                    case 0x4A: position += Bin.Align(count * 8, 4) ; break;   // MPG (not expected)
                    case 0x50: case 0x51: position += (immediate == 0 ? 65536 : immediate) * 16; break; // DIRECT/DIRECTHL
                    case 0x00: case 0x01: case 0x02: case 0x03: case 0x04: case 0x05:
                    case 0x06: case 0x07: case 0x10: case 0x11: case 0x13:
                        break;
                    default:
                        throw new InvalidDataException($"Unknown VIF command 0x{command:X2} at 0x{position - 4:X}");
                }
            }
            return next;
        }

        private static ModelStrip BuildStrip(Bin guts, Dictionary<int, (int cmd, int num, int at)> unpacked, ModelMesh mesh)
        {
            var strip = new ModelStrip();
            if (!unpacked.TryGetValue(2, out var pos)) return strip;
            if (unpacked.TryGetValue(0, out var header) && header.cmd == 0x6C) strip.HeaderFlags = guts.U32(header.at + 12);
            int vertexCount = pos.num;
            for (int index = 0; index < vertexCount; index++)
            {
                var vertex = new ModelVertex();
                if (pos.cmd == 0x68 || pos.cmd == 0x6C)
                {
                    int stride = pos.cmd == 0x6C ? 16 : 12;
                    int offset = pos.at + index * stride;
                    vertex.Position = new Vector3(guts.F32(offset), guts.F32(offset + 4), guts.F32(offset + 8));
                }
                strip.Vertices.Add(vertex);
            }
            if (unpacked.TryGetValue(3, out var normals))
            {
                for (int index = 0; index < Math.Min(vertexCount, normals.num); index++)
                {
                    var vertex = strip.Vertices[index];
                    if (normals.cmd == 0x6A)        // V3-8
                    {
                        int offset = normals.at + index * 3;
                        vertex.Normal = new Vector3(guts.S8(offset), guts.S8(offset + 1), guts.S8(offset + 2)) / 127f;
                    }
                    else if (normals.cmd == 0x6E)   // V4-8 : normal + bone0<<1
                    {
                        int offset = normals.at + index * 4;
                        vertex.Normal = new Vector3(guts.S8(offset), guts.S8(offset + 1), guts.S8(offset + 2)) / 127f;
                        vertex.Node0 = vertex.Bone0 = (byte)(guts.U8(offset + 3) >> 1);
                        strip.Skinned = true;
                        mesh.Skinned = true;
                    }
                }
            }
            if (unpacked.TryGetValue(4, out var uv))
            {
                for (int index = 0; index < Math.Min(vertexCount, uv.num); index++)
                {
                    var vertex = strip.Vertices[index];
                    if (uv.cmd == 0x65)         // V2-16
                    {
                        int offset = uv.at + index * 4;
                        vertex.UV = new Vector2(guts.S16(offset), guts.S16(offset + 2)) / 4096f;
                    }
                    else if (uv.cmd == 0x6D)    // V4-16 : uv, weight, bone1<<2
                    {
                        int offset = uv.at + index * 8;
                        vertex.UV = new Vector2(guts.S16(offset), guts.S16(offset + 2)) / 4096f;
                        vertex.Weight0 = Math.Clamp(guts.S16(offset + 4) / 4096f, 0f, 1f);
                        vertex.Node1 = vertex.Bone1 = (byte)(guts.U16(offset + 6) >> 2);
                    }
                }
            }
            if (unpacked.TryGetValue(5, out var colors) && colors.cmd == 0x6E)
            {
                for (int index = 0; index < Math.Min(vertexCount, colors.num); index++)
                {
                    int offset = colors.at + index * 4;
                    strip.Vertices[index].Color = new Vector4(guts.U8(offset), guts.U8(offset + 1), guts.U8(offset + 2), guts.U8(offset + 3)) / 128f;
                }
            }
            return strip;
        }

        /// <summary>Total triangle count across all strips.</summary>
        public int TriangleCount => Meshes.Sum(mesh => mesh.Strips.Sum(strip => Math.Max(0, strip.Vertices.Count - 2)));
        public int VertexCount => Meshes.Sum(mesh => mesh.Strips.Sum(strip => strip.Vertices.Count));
    }
}
