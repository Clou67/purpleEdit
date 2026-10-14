using Mk2.IO;

namespace Mk2.Formats
{
    /// <summary>
    /// Krome MK2 PS2 texture (".tex").
    ///
    /// Header (0x20 bytes, LE):
    ///   0x00 char[4] "TEX\0"
    ///   0x04 u16 width, 0x06 u16 height
    ///   0x08 u32 paletteOffset (relative to 0x20) / pixel data size
    ///   0x0C u32 pixel data size
    ///   0x10 u8  format: 0x00 RGBA32, 0x01 RGB24, 0x13 8bpp CLUT, 0x14 4bpp CLUT
    ///   0x11 u8  (8 for CLUT formats)
    ///   0x12 u8  log2(width), 0x13 u8 log2(height)
    ///   0x18 u32 flags (1 observed on some textures)
    /// Pixel data starts at 0x20, linear (not swizzled). CLUT follows pixel data.
    ///   8bpp: 256 RGBA entries in PS2 CSM1 order (entries 8..15 / 16..23 of each 32 block swapped).
    ///   4bpp: 16 colours stored as an 8x2 block inside a 16-wide CLUT (entries 0-7, then 16-23).
    /// Alpha is PS2 style: 0x80 = fully opaque.
    /// </summary>
    public sealed class Texture
    {
        public string Name;
        public int Width, Height;
        public int Format;
        public uint Flags;
        /// <summary>RGBA8, top row first.</summary>
        public byte[] Rgba;
        public bool HasAlpha;       // any pixel with alpha < 255
        public bool HasCutout;      // only 0/255 alpha

        public static Texture Decode(byte[] d, string name = null)
        {
            if (d.Length < 0x20 || d[0] != 'T' || d[1] != 'E' || d[2] != 'X')
                throw new InvalidDataException("Not a TEX file");
            var b = new Bin(d);
            var t = new Texture
            {
                Name = name,
                Width = b.U16(4),
                Height = b.U16(6),
                Format = d[0x10],
                Flags = b.U32(0x18),
            };
            int w = t.Width, h = t.Height, n = w * h;
            var px = new byte[n * 4];
            int p = 0x20;
            switch (t.Format)
            {
                case 0x00: // RGBA32
                    for (int i = 0; i < n; i++)
                    {
                        px[i * 4 + 0] = d[p + i * 4 + 0];
                        px[i * 4 + 1] = d[p + i * 4 + 1];
                        px[i * 4 + 2] = d[p + i * 4 + 2];
                        px[i * 4 + 3] = Ps2Alpha(d[p + i * 4 + 3]);
                    }
                    break;
                case 0x01: // RGB24
                    for (int i = 0; i < n; i++)
                    {
                        px[i * 4 + 0] = d[p + i * 3 + 0];
                        px[i * 4 + 1] = d[p + i * 3 + 1];
                        px[i * 4 + 2] = d[p + i * 3 + 2];
                        px[i * 4 + 3] = 255;
                    }
                    break;
                case 0x13: // 8bpp
                {
                    int pal = p + n;
                    var clut = new byte[256 * 4];
                    for (int i = 0; i < 256; i++)
                    {
                        int j = (i & 0xE7) | ((i & 0x08) << 1) | ((i & 0x10) >> 1);
                        int s = pal + j * 4;
                        if (s + 3 >= d.Length) continue;
                        clut[i * 4 + 0] = d[s]; clut[i * 4 + 1] = d[s + 1];
                        clut[i * 4 + 2] = d[s + 2]; clut[i * 4 + 3] = Ps2Alpha(d[s + 3]);
                    }
                    for (int i = 0; i < n; i++)
                        Buffer.BlockCopy(clut, d[p + i] * 4, px, i * 4, 4);
                    break;
                }
                case 0x14: // 4bpp
                {
                    int pal = p + n / 2;
                    var clut = new byte[16 * 4];
                    for (int i = 0; i < 16; i++)
                    {
                        int j = (i & 7) + (i >> 3) * 16;
                        int s = pal + j * 4;
                        if (s + 3 >= d.Length) continue;
                        clut[i * 4 + 0] = d[s]; clut[i * 4 + 1] = d[s + 1];
                        clut[i * 4 + 2] = d[s + 2]; clut[i * 4 + 3] = Ps2Alpha(d[s + 3]);
                    }
                    for (int i = 0; i < n; i++)
                    {
                        int v = d[p + (i >> 1)];
                        int idx = (i & 1) == 0 ? v & 15 : v >> 4;
                        Buffer.BlockCopy(clut, idx * 4, px, i * 4, 4);
                    }
                    break;
                }
                default:
                    throw new NotSupportedException($"TEX format 0x{t.Format:X2}");
            }
            t.Rgba = px;
            bool any = false, partial = false;
            for (int i = 3; i < px.Length; i += 4)
            {
                if (px[i] != 255) { any = true; if (px[i] != 0) partial = true; }
            }
            t.HasAlpha = any;
            t.HasCutout = any && !partial;
            return t;
        }

        private static byte Ps2Alpha(byte a) => a >= 0x80 ? (byte)255 : (byte)(a * 2);

        /// <summary>A small magenta/black checker used when a texture is missing.</summary>
        public static Texture Missing()
        {
            var t = new Texture { Name = "<missing>", Width = 8, Height = 8, Rgba = new byte[8 * 8 * 4] };
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    bool c = ((x >> 2) ^ (y >> 2)) != 0;
                    int i = (y * 8 + x) * 4;
                    t.Rgba[i] = c ? (byte)255 : (byte)40; t.Rgba[i + 1] = 0;
                    t.Rgba[i + 2] = c ? (byte)255 : (byte)40; t.Rgba[i + 3] = 255;
                }
            return t;
        }
    }
}
