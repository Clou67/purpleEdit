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
    ///        +0x10 u32 name offset   +0x14 u16 key count   +0x16 u8 parent (0xFF = root)
    ///        +0x17 u8 transform pool: the bone's ANG indices count from pool × (ANG transform count in the info block)
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
            var reader = new Bin(anm);
            if (anm.Length < 0x20 || reader.CStr(0, 4) != "ANM3")
            {
                if (anm.Length >= 4 && anm[0] == '3' && anm[1] == 'M' && anm[2] == 'N' && anm[3] == 'A')
                    throw new NotSupportedException("Big-endian (GameCube) ANM3 is not supported yet");
                throw new InvalidDataException("Not an ANM3 file");
            }
            var animation = new Animation { Name = name };
            int boneCount = reader.U8(4);
            animation.HeaderByte5 = reader.U8(5);
            animation.FrameCount = reader.U16(6);

            var guts = ang != null && ang.Length >= 0x10 ? new Bin(ang) : null;
            int angData = 0x10, angCount = 0, poolSize = 0;
            int info = reader.S32(0x14);
            if (info > 0 && info + 6 <= anm.Length)
            {
                angData = reader.S32(info);
                angCount = reader.U16(info + 4);
                poolSize = angCount;
            }
            if (guts != null)
            {
                if (angData <= 0 || angData > ang.Length) angData = 0x10;
                int fit = (ang.Length - angData) / 8;
                angCount = fit;
            }
            else animation.Warnings.Add("ANG file missing: positions, scales and indexed rotations fall back to the bind pose");
            animation.TransformCount = angCount;

            short[] ReadTransform(int index)
            {
                if (guts == null || index < 0 || index >= angCount) return null;
                int offset = angData + index * 8;
                return new[] { guts.S16(offset), guts.S16(offset + 2), guts.S16(offset + 4), guts.S16(offset + 6) };
            }
            Quaternion Rot(int index)
            {
                var transform = ReadTransform(index);
                if (transform == null) return Quaternion.Identity;
                var rotation = new Quaternion(transform[0] / 16384f, transform[1] / 16384f, transform[2] / 16384f, transform[3] / 16384f);
                return rotation.LengthSquared() > 1e-12f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
            }
            Vector3 Vec(int index, Vector3 fallback)
            {
                var transform = ReadTransform(index);
                return transform == null ? fallback : DecompressVector(transform[0], transform[1], transform[2], (ushort)transform[3]);
            }

            for (int boneIndex = 0; boneIndex < boneCount; boneIndex++)
            {
                int offset = 0x20 + boneIndex * 0x20;
                if (offset + 0x20 > anm.Length) { animation.Warnings.Add($"bone table truncated at bone {boneIndex}"); break; }
                var bone = new AnimBone
                {
                    Index = boneIndex,
                    BindPosition = new Vector3(reader.F32(offset), reader.F32(offset + 4), reader.F32(offset + 8)),
                    Name = reader.CStr(reader.S32(offset + 0x10)),
                    KeyType = (AnimKeyType)reader.U16(offset + 0x1E),
                };
                int keyCount = reader.U16(offset + 0x14);
                int parent = reader.U8(offset + 0x16);
                bone.Parent = parent == 0xFF || parent >= boneCount ? -1 : parent;
                int poolStart = reader.U8(offset + 0x17) * poolSize;
                int keyOff = reader.S32(offset + 0x18);
                if (string.IsNullOrEmpty(bone.Name)) bone.Name = "bone" + boneIndex;

                int stride = bone.KeyType == AnimKeyType.RotationIndexed ? 4 : 8;
                if ((int)bone.KeyType > 2) animation.Warnings.Add($"{bone.Name}: unknown key type {(int)bone.KeyType}, read as type 0");
                for (int keyIndex = 0; keyIndex < keyCount; keyIndex++)
                {
                    int keyOffset = keyOff + keyIndex * stride;
                    if (keyOff <= 0 || keyOffset + stride > anm.Length) { animation.Warnings.Add($"{bone.Name}: keys run off the end of the file"); break; }
                    var key = new AnimKey { Frame = reader.U16(keyOffset), Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One };
                    switch (bone.KeyType)
                    {
                        case AnimKeyType.RotationIndexed:
                            key.Rotation = Rot(poolStart + reader.U16(keyOffset + 2));
                            break;
                        case AnimKeyType.RotationInline:
                        {
                            float quaternionX = reader.S16(keyOffset + 2) / 16384f, quaternionY = reader.S16(keyOffset + 4) / 16384f, quaternionZ = reader.S16(keyOffset + 6) / 16384f;
                            float wSquared = 1f - quaternionX * quaternionX - quaternionY * quaternionY - quaternionZ * quaternionZ;
                            key.Rotation = Quaternion.Normalize(new Quaternion(quaternionX, quaternionY, quaternionZ, wSquared > 0 ? MathF.Sqrt(wSquared) : 0f));
                            break;
                        }
                        default:
                            key.Position = Vec(poolStart + reader.U16(keyOffset + 2), Vector3.Zero);
                            key.Rotation = Rot(poolStart + reader.U16(keyOffset + 4));
                            key.Scale = Vec(poolStart + reader.U16(keyOffset + 6), Vector3.One);
                            break;
                    }
                    bone.Keys.Add(key);
                }
                animation.Bones.Add(bone);
            }

            foreach (var bone in animation.Bones)
            {
                var parentBind = bone.Parent >= 0 ? animation.Bones[bone.Parent].BindPosition : Vector3.Zero;
                bone.LocalOffset = bone.BindPosition - parentBind;
                if (bone.Parent >= 0) animation.Bones[bone.Parent].Children.Add(bone.Index);
                bone.IsAnimated = bone.Keys.Count > 1 && bone.Keys.Any(key =>
                    key.Position.LengthSquared() > 1e-6f || MathF.Abs(MathF.Abs(key.Rotation.W) - 1f) > 1e-5f
                    || (key.Scale - Vector3.One).LengthSquared() > 1e-6f);
            }
            animation.BuildEvalOrder();
            return animation;
        }

        private void BuildEvalOrder()
        {
            var order = new List<int>(Bones.Count);
            var state = new byte[Bones.Count];   // 0 = new, 1 = visiting, 2 = done
            void Visit(int boneIndex)
            {
                if (state[boneIndex] == 2) return;
                if (state[boneIndex] == 1) { Bones[boneIndex].Parent = -1; Warnings.Add($"{Bones[boneIndex].Name}: parent loop, treated as root"); return; }
                state[boneIndex] = 1;
                if (Bones[boneIndex].Parent >= 0) Visit(Bones[boneIndex].Parent);
                state[boneIndex] = 2;
                order.Add(boneIndex);
            }
            for (int boneIndex = 0; boneIndex < Bones.Count; boneIndex++) Visit(boneIndex);
            EvalOrder = order.ToArray();
        }

        /// <summary>
        /// Krome's Decompress_Vector (from the GameCube ELF): each axis is a 16-bit signed mantissa, and
        /// w packs three 5-bit exponents (x in bits 10–14, y in 5–9, z in 0–4). Exponent 0 means 0.0.
        /// </summary>
        public static Vector3 DecompressVector(short packedX, short packedY, short packedZ, ushort exponents)
        {
            static float Axis(short mantissa, int exponent)
            {
                if (exponent == 0) return 0f;
                int bits = ((mantissa & unchecked((int)0xFFFF8000)) << 8) | (mantissa & 0x7FFF);
                bits = (bits << 8) | ((exponent + 0x70) << 23);
                return BitConverter.Int32BitsToSingle(bits);
            }
            return new Vector3(Axis(packedX, (exponents >> 10) & 0x1F), Axis(packedY, (exponents >> 5) & 0x1F), Axis(packedZ, exponents & 0x1F));
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
            int low = 0, high = keys.Count - 1;
            if (frame <= keys[0].Frame) high = 0;
            else
            {
                while (low < high)
                {
                    int mid = (low + high + 1) >> 1;
                    if (keys[mid].Frame <= frame) low = mid; else high = mid - 1;
                }
            }
            var previous = keys[high];
            var next = high + 1 < keys.Count ? keys[high + 1] : previous;
            float blend = next.Frame > previous.Frame ? Math.Clamp((frame - previous.Frame) / (next.Frame - previous.Frame), 0f, 1f) : 0f;
            translation = bone.LocalOffset + Vector3.Lerp(previous.Position, next.Position, blend);
            scale = Vector3.Lerp(previous.Scale, next.Scale, blend);
            var nextRotation = next.Rotation;
            if (Quaternion.Dot(previous.Rotation, nextRotation) < 0) nextRotation = -nextRotation;
            rotation = Quaternion.Normalize(Quaternion.Lerp(previous.Rotation, nextRotation, blend));
        }

        /// <summary>Model-space bone matrices (row vectors) at a frame. <paramref name="frame"/> &lt; 0 gives the bind (reference) pose.</summary>
        public void ComputePose(float frame, Matrix4x4[] globals)
        {
            foreach (int boneIndex in EvalOrder)
            {
                var bone = Bones[boneIndex];
                Matrix4x4 local;
                if (frame < 0) local = Matrix4x4.CreateTranslation(bone.LocalOffset);
                else
                {
                    SampleLocal(bone, frame, out var translation, out var rotation, out var scale);
                    local = Matrix4x4.CreateScale(scale)
                          * Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(rotation))
                          * Matrix4x4.CreateTranslation(translation);
                }
                globals[boneIndex] = bone.Parent >= 0 ? local * globals[bone.Parent] : local;
            }
        }

        /// <summary>Converts model-space bone matrices into skinning matrices (bind pose → posed).</summary>
        public void SkinMatrices(Matrix4x4[] globals, Matrix4x4[] skin)
        {
            for (int boneIndex = 0; boneIndex < Bones.Count; boneIndex++)
                skin[boneIndex] = Matrix4x4.CreateTranslation(-Bones[boneIndex].BindPosition) * globals[boneIndex];
        }

        /// <summary>"z_A002_Head" → "A002": the actor code cutscene animations use to name their target's bones.</summary>
        public string ActorCode()
        {
            foreach (var bone in Bones)
            {
                var name = bone.Name;
                if (name.Length > 7 && name[1] == '_' && char.IsLetter(name[2]) && char.IsDigit(name[3]) && char.IsDigit(name[4]) && char.IsDigit(name[5]) && name[6] == '_')
                    return name.Substring(2, 4);
            }
            return null;
        }
    }
}
