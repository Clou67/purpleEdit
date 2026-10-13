using System.IO.Compression;

namespace Mk2.Export
{
    /// <summary>Minimal RGBA8 PNG encoder (no external dependencies).</summary>
    public static class Png
    {
        public static byte[] Encode(byte[] rgba, int w, int h)
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, w); WriteBE(ihdr, 4, h);
            ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
            Chunk(ms, "IHDR", ihdr);

            using var raw = new MemoryStream();
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, true))
            {
                for (int y = 0; y < h; y++)
                {
                    z.WriteByte(0);
                    z.Write(rgba, y * w * 4, w * 4);
                }
            }
            Chunk(ms, "IDAT", raw.ToArray());
            Chunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        public static void Save(string path, byte[] rgba, int w, int h) => File.WriteAllBytes(path, Encode(rgba, w, h));

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len);
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(t); s.Write(data);
            uint crc = Crc(t, 0xFFFFFFFF);
            crc = Crc(data, crc) ^ 0xFFFFFFFF;
            var c = new byte[4]; WriteBE(c, 0, (int)crc); s.Write(c);
        }

        private static void WriteBE(byte[] b, int o, int v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        private static uint[] _table;
        private static uint Crc(byte[] data, uint crc)
        {
            if (_table == null)
            {
                _table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    _table[n] = c;
                }
            }
            foreach (var b in data) crc = _table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
