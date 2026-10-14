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

        public static Texture Decode(byte[] data, string name = null)
        {
            if (data.Length < 0x20 || data[0] != 'T' || data[1] != 'E' || data[2] != 'X')
                throw new InvalidDataException("Not a TEX file");
            var reader = new Bin(data);
            var texture = new Texture
            {
                Name = name,
                Width = reader.U16(4),
                Height = reader.U16(6),
                Format = data[0x10],
                Flags = reader.U32(0x18),
            };
            int width = texture.Width, height = texture.Height, pixelCount = width * height;
            var pixels = new byte[pixelCount * 4];
            int dataOffset = 0x20;
            switch (texture.Format)
            {
                case 0x00: // RGBA32
                    for (int pixel = 0; pixel < pixelCount; pixel++)
                    {
                        pixels[pixel * 4 + 0] = data[dataOffset + pixel * 4 + 0];
                        pixels[pixel * 4 + 1] = data[dataOffset + pixel * 4 + 1];
                        pixels[pixel * 4 + 2] = data[dataOffset + pixel * 4 + 2];
                        pixels[pixel * 4 + 3] = Ps2Alpha(data[dataOffset + pixel * 4 + 3]);
                    }
                    break;
                case 0x01: // RGB24
                    for (int pixel = 0; pixel < pixelCount; pixel++)
                    {
                        pixels[pixel * 4 + 0] = data[dataOffset + pixel * 3 + 0];
                        pixels[pixel * 4 + 1] = data[dataOffset + pixel * 3 + 1];
                        pixels[pixel * 4 + 2] = data[dataOffset + pixel * 3 + 2];
                        pixels[pixel * 4 + 3] = 255;
                    }
                    break;
                case 0x13: // 8bpp
                {
                    int palette = dataOffset + pixelCount;
                    var clut = new byte[256 * 4];
                    for (int entry = 0; entry < 256; entry++)
                    {
                        int swizzled = (entry & 0xE7) | ((entry & 0x08) << 1) | ((entry & 0x10) >> 1);
                        int source = palette + swizzled * 4;
                        if (source + 3 >= data.Length) continue;
                        clut[entry * 4 + 0] = data[source]; clut[entry * 4 + 1] = data[source + 1];
                        clut[entry * 4 + 2] = data[source + 2]; clut[entry * 4 + 3] = Ps2Alpha(data[source + 3]);
                    }
                    for (int pixel = 0; pixel < pixelCount; pixel++)
                        Buffer.BlockCopy(clut, data[dataOffset + pixel] * 4, pixels, pixel * 4, 4);
                    break;
                }
                case 0x14: // 4bpp
                {
                    int palette = dataOffset + pixelCount / 2;
                    var clut = new byte[16 * 4];
                    for (int entry = 0; entry < 16; entry++)
                    {
                        int clutIndex = (entry & 7) + (entry >> 3) * 16;
                        int source = palette + clutIndex * 4;
                        if (source + 3 >= data.Length) continue;
                        clut[entry * 4 + 0] = data[source]; clut[entry * 4 + 1] = data[source + 1];
                        clut[entry * 4 + 2] = data[source + 2]; clut[entry * 4 + 3] = Ps2Alpha(data[source + 3]);
                    }
                    for (int pixel = 0; pixel < pixelCount; pixel++)
                    {
                        int packed = data[dataOffset + (pixel >> 1)];
                        int paletteIndex = (pixel & 1) == 0 ? packed & 15 : packed >> 4;
                        Buffer.BlockCopy(clut, paletteIndex * 4, pixels, pixel * 4, 4);
                    }
                    break;
                }
                default:
                    throw new NotSupportedException($"TEX format 0x{texture.Format:X2}");
            }
            texture.Rgba = pixels;
            bool any = false, partial = false;
            for (int alpha = 3; alpha < pixels.Length; alpha += 4)
            {
                if (pixels[alpha] != 255) { any = true; if (pixels[alpha] != 0) partial = true; }
            }
            texture.HasAlpha = any;
            texture.HasCutout = any && !partial;
            return texture;
        }

        private static byte Ps2Alpha(byte alpha) => alpha >= 0x80 ? (byte)255 : (byte)(alpha * 2);

        /// <summary>A small magenta/black checker used when a texture is missing.</summary>
        public static Texture Missing()
        {
            var texture = new Texture { Name = "<missing>", Width = 8, Height = 8, Rgba = new byte[8 * 8 * 4] };
            for (int row = 0; row < 8; row++)
                for (int column = 0; column < 8; column++)
                {
                    bool light = ((column >> 2) ^ (row >> 2)) != 0;
                    int offset = (row * 8 + column) * 4;
                    texture.Rgba[offset] = light ? (byte)255 : (byte)40; texture.Rgba[offset + 1] = 0;
                    texture.Rgba[offset + 2] = light ? (byte)255 : (byte)40; texture.Rgba[offset + 3] = 255;
                }
            return texture;
        }
    }
}
