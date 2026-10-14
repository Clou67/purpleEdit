using Mk2.IO;

namespace Mk2.Formats
{
    public enum BlendMode { Opaque = 0, Additive = 1, Subtractive = 2 }

    /// <summary>
    /// Material instance (".min", 0x1EC bytes on PS2).
    /// Field meanings are inferred from comparing every material in the game; the ones
    /// marked (?) are educated guesses.
    ///   0x000 char[32] material name
    ///   0x020 u32  name hash
    ///   0x024 u32  blend mode (0 opaque, 1 additive, 2 subtractive) (?)
    ///   0x02C f32  alpha-test reference (0.5 typical for cut-out foliage) (?)
    ///   0x030 f32[4] colour multiplier (?)
    ///   0x080 char[32] collision surface name ("Wall", "Mud", ...) for CM_* materials
    ///   0x0A0 char[32] grass type ("grass")
    ///   0x0C0 char[40] grass/overlay material
    ///   0x0E8 char[32] texture name override (empty = texture has the material's name)
    ///   0x140 f32  uv scroll U, 0x144 f32 uv scroll V
    ///   0x1DC u32  ?, 0x1E0 u32 flags:
    ///        0x0001 collision material, 0x0060 grass, 0x0100 double-sided ("_NC"/"_DS"),
    ///        0x2000 alpha test, 0x8000 texture override present, 0x80000 uv scroll,
    ///        0x40000000 glow/HDR pass (?), 0x80000000 environment map (?)
    /// </summary>
    public sealed class Material
    {
        public string Name;
        public string TextureName;
        public string GrassType;
        public string GrassMaterial;
        public string CollisionSurface;
        public uint Hash;
        public uint Flags;
        public BlendMode Blend;
        public float AlphaRef;
        public float[] Color = { 1, 1, 1, 1 };
        public float ScrollU, ScrollV;
        public bool DoubleSided => (Flags & 0x100) != 0;
        public bool AlphaTest => (Flags & 0x2000) != 0 || AlphaRef > 0;
        public bool IsCollisionMaterial => (Flags & 1) != 0;
        public bool Scrolls => (Flags & 0x80000) != 0 && (ScrollU != 0 || ScrollV != 0);
        public bool Glow => (Flags & 0x40000000) != 0;

        public static Material Parse(byte[] d, string fallbackName = null)
        {
            var b = new Bin(d);
            var m = new Material();
            m.Name = b.FixedStr(0, 32);
            if (string.IsNullOrEmpty(m.Name)) m.Name = fallbackName;
            if (d.Length < 0x1E4)
            {
                m.TextureName = m.Name;
                return m;
            }
            m.Hash = b.U32(0x20);
            uint blend = b.U32(0x24);
            m.Blend = blend <= 2 ? (BlendMode)blend : BlendMode.Opaque;
            m.AlphaRef = b.F32(0x2C);
            if (float.IsNaN(m.AlphaRef) || m.AlphaRef < 0 || m.AlphaRef > 1) m.AlphaRef = 0;
            var col = new float[] { b.F32(0x30), b.F32(0x34), b.F32(0x38), b.F32(0x3C) };
            if (col.Any(c => c != 0) && col.All(c => c >= 0 && c <= 4)) m.Color = col;
            m.CollisionSurface = b.FixedStr(0x80, 32);
            m.GrassType = b.FixedStr(0xA0, 32);
            m.GrassMaterial = b.FixedStr(0xC0, 40);
            m.TextureName = b.FixedStr(0xE8, 32);
            if (string.IsNullOrEmpty(m.TextureName)) m.TextureName = m.Name;
            m.ScrollU = b.F32(0x140);
            m.ScrollV = b.F32(0x144);
            m.Flags = b.U32(0x1E0);
            return m;
        }

        public static Material Default(string name) => new Material { Name = name, TextureName = name };
    }
}
