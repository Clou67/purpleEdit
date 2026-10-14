using System.IO.Compression;

namespace Mk2.Export
{
    /// <summary>Minimal RGBA8 PNG encoder (no external dependencies).</summary>
    public static class Png
    {
        public static byte[] Encode(byte[] rgba, int width, int height)
        {
            using var output = new MemoryStream();
            output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, width); WriteBE(ihdr, 4, height);
            ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
            Chunk(output, "IHDR", ihdr);

            using var raw = new MemoryStream();
            using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, true))
            {
                for (int row = 0; row < height; row++)
                {
                    zlib.WriteByte(0);
                    zlib.Write(rgba, row * width * 4, width * 4);
                }
            }
            Chunk(output, "IDAT", raw.ToArray());
            Chunk(output, "IEND", Array.Empty<byte>());
            return output.ToArray();
        }

        public static void Save(string path, byte[] rgba, int width, int height) => File.WriteAllBytes(path, Encode(rgba, width, height));

        private static void Chunk(Stream stream, string type, byte[] data)
        {
            var lengthBytes = new byte[4]; WriteBE(lengthBytes, 0, data.Length); stream.Write(lengthBytes);
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes); stream.Write(data);
            uint crc = Crc(typeBytes, 0xFFFFFFFF);
            crc = Crc(data, crc) ^ 0xFFFFFFFF;
            var crcBytes = new byte[4]; WriteBE(crcBytes, 0, (int)crc); stream.Write(crcBytes);
        }

        private static void WriteBE(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24); buffer[offset + 1] = (byte)(value >> 16); buffer[offset + 2] = (byte)(value >> 8); buffer[offset + 3] = (byte)value;
        }

        private static uint[] crcTable;
        private static uint Crc(byte[] data, uint crc)
        {
            if (crcTable == null)
            {
                crcTable = new uint[256];
                for (uint entry = 0; entry < 256; entry++)
                {
                    uint value = entry;
                    for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
                    crcTable[entry] = value;
                }
            }
            foreach (var value in data) crc = crcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
