using System.Numerics;
using Mk2.IO;

namespace Mk2.Formats
{
    /// <summary>How a bone stores its keyframes (u16 at bone descriptor +0x1E).</summary>
    public enum AnimKeyType
    {
        /// <summary>8 bytes: u16 frame, u16 position, u16 rotation, u16 scale (indices into the ANG).</summary>
        Full = 0,
        /// <summary>4 bytes: u16 frame, u16 rotation index into the ANG.</summary>
        RotationIndexed = 1,
        /// <summary>8 bytes: u16 frame, s16 qx, qy, qz (÷16384), w = sqrt(1 − x² − y² − z²).</summary>
        RotationInline = 2,
    }

    public struct AnimKey
    {
        public int Frame;
        /// <summary>Offset added to the bone's bind-pose local offset (zero for rotation-only keys).</summary>
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
    }

    public sealed class AnimBone
    {
        public int Index;
        public string Name;
        /// <summary>Parent bone index, -1 for a root (stored as 0xFF).</summary>
        public int Parent = -1;
        /// <summary>Bind-pose position in model space. Matches the MDL3 anim node table.</summary>
        public Vector3 BindPosition;
        /// <summary>BindPosition relative to the parent's BindPosition.</summary>
        public Vector3 LocalOffset;
        public AnimKeyType KeyType;
        public List<AnimKey> Keys = new();
        public List<int> Children = new();

        /// <summary>True when the keys actually move the bone away from its bind pose at some point.</summary>
        public bool IsAnimated;

        public override string ToString() => Name;
    }

    /// <summary>
    /// ANM3 (header) + ANG3 (transform "guts") pair: a skeleton plus one long timeline of keyframes.
    /// Individual clips are frame ranges listed in the model's .bbi (see <see cref="AnimSet"/>).
    ///
    /// ANM3:
    ///   0x00 "ANM3"
    ///   0x04 u8  bone count          0x05 u8 ? (1 or 2)          0x06 u16 frame count
    ///   0x14 u32 offset of the ANG info block: u32 ANG data offset (0x10), u16 ANG transform count, u16 8 (stride)
    ///   0x20 bones, 0x20 bytes each:
    ///        +0x00 vec3 bind position (model space)  +0x0C runtime pointer
    ///        +0x10 u32 name offset   +0x14 u16 key count   +0x16 u16 parent (0xFF = root)
    ///        +0x18 u32 key offset    +0x1C u16 0           +0x1E u16 key type (<see cref="AnimKeyType"/>)
    /// ANG3: 0x10 header, then 8-byte transforms: s16 x, y, z, w.
    ///   rotation = quaternion (x, y, z, w) ÷ 16384
    ///   position / scale = Decompress_Vector (5-bit exponent per axis packed in w).
    ///
    /// Pose maths (checked against the game's walk/run cycles): local = S · R · T(localOffset + key.Position)
    /// with row vectors, R = CreateFromQuaternion(conjugate(q)) — the game's matrices are transposed — and
    /// global = local · parentGlobal. Vertices are stored in bind pose, so skin = T(−BindPosition) · global.
    /// </summary>
    public sealed class Animation
    {
        public string Name;
        public int FrameCount;
        public int HeaderByte5;
        public int TransformCount;
        public List<AnimBone> Bones = new();
        public List<string> Warnings = new();

        /// <summary>Bones in an order where every parent comes before its children.</summary>
        public int[] EvalOrder { get; private set; } = Array.Empty<int>();

        public static Animation Load(byte[] anm, byte[] ang, string name = null)
        {
            var b = new Bin(anm);
            if (anm.Length < 0x20 || b.CStr(0, 4) != "ANM3")
            {
                if (anm.Length >= 4 && anm[0] == '3' && anm[1] == 'M' && anm[2] == 'N' && anm[3] == 'A')
                    throw new NotSupportedException("Big-endian (GameCube) ANM3 is not supported yet");
                throw new InvalidDataException("Not an ANM3 file");
            }
            var a = new Animation { Name = name };
            int boneCount = b.U8(4);
            a.HeaderByte5 = b.U8(5);
            a.FrameCount = b.U16(6);

            var g = ang != null && ang.Length >= 0x10 ? new Bin(ang) : null;
            int angData = 0x10, angCount = 0;
            int info = b.S32(0x14);
            if (info > 0 && info + 6 <= anm.Length)
            {
                angData = b.S32(info);
                angCount = b.U16(info + 4);
            }
            if (g != null)
            {
                if (angData <= 0 || angData > ang.Length) angData = 0x10;
                int fit = (ang.Length - angData) / 8;
                if (angCount == 0 || angCount > fit) angCount = fit;
            }
            else a.Warnings.Add("ANG file missing: positions, scales and indexed rotations fall back to the bind pose");
            a.TransformCount = angCount;

            short[] T(int i)
            {
                if (g == null || i < 0 || i >= angCount) return null;
                int o = angData + i * 8;
                return new[] { g.S16(o), g.S16(o + 2), g.S16(o + 4), g.S16(o + 6) };
            }
            Quaternion Rot(int i)
            {
                var s = T(i);
                if (s == null) return Quaternion.Identity;
                var q = new Quaternion(s[0] / 16384f, s[1] / 16384f, s[2] / 16384f, s[3] / 16384f);
                return q.LengthSquared() > 1e-12f ? Quaternion.Normalize(q) : Quaternion.Identity;
            }
            Vector3 Vec(int i, Vector3 fallback)
            {
                var s = T(i);
                return s == null ? fallback : DecompressVector(s[0], s[1], s[2], (ushort)s[3]);
            }

            for (int i = 0; i < boneCount; i++)
            {
                int o = 0x20 + i * 0x20;
                if (o + 0x20 > anm.Length) { a.Warnings.Add($"bone table truncated at bone {i}"); break; }
                var bone = new AnimBone
                {
                    Index = i,
                    BindPosition = new Vector3(b.F32(o), b.F32(o + 4), b.F32(o + 8)),
                    Name = b.CStr(b.S32(o + 0x10)),
                    KeyType = (AnimKeyType)b.U16(o + 0x1E),
                };
                int keyCount = b.U16(o + 0x14);
                int parent = b.U16(o + 0x16);
                bone.Parent = parent == 0xFF || parent == 0xFFFF || parent >= boneCount ? -1 : parent;
                int keyOff = b.S32(o + 0x18);
                if (string.IsNullOrEmpty(bone.Name)) bone.Name = "bone" + i;

                int stride = bone.KeyType == AnimKeyType.RotationIndexed ? 4 : 8;
                if ((int)bone.KeyType > 2) a.Warnings.Add($"{bone.Name}: unknown key type {(int)bone.KeyType}, read as type 0");
                for (int k = 0; k < keyCount; k++)
                {
                    int ko = keyOff + k * stride;
                    if (keyOff <= 0 || ko + stride > anm.Length) { a.Warnings.Add($"{bone.Name}: keys run off the end of the file"); break; }
                    var key = new AnimKey { Frame = b.U16(ko), Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One };
                    switch (bone.KeyType)
                    {
                        case AnimKeyType.RotationIndexed:
                            key.Rotation = Rot(b.U16(ko + 2));
                            break;
                        case AnimKeyType.RotationInline:
                        {
                            float x = b.S16(ko + 2) / 16384f, y = b.S16(ko + 4) / 16384f, z = b.S16(ko + 6) / 16384f;
                            float w2 = 1f - x * x - y * y - z * z;
                            key.Rotation = Quaternion.Normalize(new Quaternion(x, y, z, w2 > 0 ? MathF.Sqrt(w2) : 0f));
                            break;
                        }
                        default:
                            key.Position = Vec(b.U16(ko + 2), Vector3.Zero);
                            key.Rotation = Rot(b.U16(ko + 4));
                            key.Scale = Vec(b.U16(ko + 6), Vector3.One);
                            break;
                    }
                    bone.Keys.Add(key);
                }
                a.Bones.Add(bone);
            }

            foreach (var bone in a.Bones)
            {
                var p = bone.Parent >= 0 ? a.Bones[bone.Parent].BindPosition : Vector3.Zero;
                bone.LocalOffset = bone.BindPosition - p;
                if (bone.Parent >= 0) a.Bones[bone.Parent].Children.Add(bone.Index);
                bone.IsAnimated = bone.Keys.Count > 1 && bone.Keys.Any(k =>
                    k.Position.LengthSquared() > 1e-6f || MathF.Abs(MathF.Abs(k.Rotation.W) - 1f) > 1e-5f
                    || (k.Scale - Vector3.One).LengthSquared() > 1e-6f);
            }
            a.BuildEvalOrder();
            return a;
        }

        private void BuildEvalOrder()
        {
            var order = new List<int>(Bones.Count);
            var state = new byte[Bones.Count];   // 0 = new, 1 = visiting, 2 = done
            void Visit(int i)
            {
                if (state[i] == 2) return;
                if (state[i] == 1) { Bones[i].Parent = -1; Warnings.Add($"{Bones[i].Name}: parent loop, treated as root"); return; }
                state[i] = 1;
                if (Bones[i].Parent >= 0) Visit(Bones[i].Parent);
                state[i] = 2;
                order.Add(i);
            }
            for (int i = 0; i < Bones.Count; i++) Visit(i);
            EvalOrder = order.ToArray();
        }

        /// <summary>
        /// Krome's Decompress_Vector (from the GameCube ELF): each axis is a 16-bit signed mantissa, and
        /// w packs three 5-bit exponents (x in bits 10–14, y in 5–9, z in 0–4). Exponent 0 means 0.0.
        /// </summary>
        public static Vector3 DecompressVector(short x, short y, short z, ushort w)
        {
            static float Axis(short v, int e)
            {
                if (e == 0) return 0f;
                int bits = ((v & unchecked((int)0xFFFF8000)) << 8) | (v & 0x7FFF);
                bits = (bits << 8) | ((e + 0x70) << 23);
                return BitConverter.Int32BitsToSingle(bits);
            }
            return new Vector3(Axis(x, (w >> 10) & 0x1F), Axis(y, (w >> 5) & 0x1F), Axis(z, w & 0x1F));
        }

        /// <summary>Interpolated local transform of one bone at a (fractional) frame.</summary>
        public void SampleLocal(AnimBone bone, float frame, out Vector3 translation, out Quaternion rotation, out Vector3 scale)
        {
            var keys = bone.Keys;
            if (keys.Count == 0)
            {
                translation = bone.LocalOffset; rotation = Quaternion.Identity; scale = Vector3.One;
                return;
            }
            // Last key with Frame <= frame (binary search; keys are sorted by frame).
            int lo = 0, hi = keys.Count - 1;
            if (frame <= keys[0].Frame) hi = 0;
            else
            {
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) >> 1;
                    if (keys[mid].Frame <= frame) lo = mid; else hi = mid - 1;
                }
            }
            var k0 = keys[hi];
            var k1 = hi + 1 < keys.Count ? keys[hi + 1] : k0;
            float t = k1.Frame > k0.Frame ? Math.Clamp((frame - k0.Frame) / (k1.Frame - k0.Frame), 0f, 1f) : 0f;
            translation = bone.LocalOffset + Vector3.Lerp(k0.Position, k1.Position, t);
            scale = Vector3.Lerp(k0.Scale, k1.Scale, t);
            var q1 = k1.Rotation;
            if (Quaternion.Dot(k0.Rotation, q1) < 0) q1 = -q1;
            rotation = Quaternion.Normalize(Quaternion.Lerp(k0.Rotation, q1, t));
        }

        /// <summary>Model-space bone matrices (row vectors) at a frame. <paramref name="frame"/> &lt; 0 gives the bind pose.</summary>
        public void ComputePose(float frame, Matrix4x4[] globals)
        {
            foreach (int i in EvalOrder)
            {
                var bone = Bones[i];
                Matrix4x4 local;
                if (frame < 0) local = Matrix4x4.CreateTranslation(bone.LocalOffset);
                else
                {
                    SampleLocal(bone, frame, out var t, out var r, out var s);
                    local = Matrix4x4.CreateScale(s)
                          * Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(r))
                          * Matrix4x4.CreateTranslation(t);
                }
                globals[i] = bone.Parent >= 0 ? local * globals[bone.Parent] : local;
            }
        }

        /// <summary>Converts model-space bone matrices into skinning matrices (bind pose → posed).</summary>
        public void SkinMatrices(Matrix4x4[] globals, Matrix4x4[] skin)
        {
            for (int i = 0; i < Bones.Count; i++)
                skin[i] = Matrix4x4.CreateTranslation(-Bones[i].BindPosition) * globals[i];
        }

        /// <summary>"z_A002_Head" → "A002": the actor code cutscene animations use to name their target's bones.</summary>
        public string ActorCode()
        {
            foreach (var bone in Bones)
            {
                var n = bone.Name;
                if (n.Length > 7 && n[1] == '_' && char.IsLetter(n[2]) && char.IsDigit(n[3]) && char.IsDigit(n[4]) && char.IsDigit(n[5]) && n[6] == '_')
                    return n.Substring(2, 4);
            }
            return null;
        }
    }
}
