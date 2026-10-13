using System.Buffers.Binary;
using System.Text;

namespace Mk2.IO
{
    /// <summary>
    /// Endian-aware helpers for reading from a byte array at absolute offsets.
    /// PS2 builds are little endian; GameCube builds are big endian.
    /// </summary>
    public sealed class Bin
    {
        public readonly byte[] Data;
        public readonly bool BigEndian;

        public Bin(byte[] data, bool bigEndian = false)
        {
            Data = data;
            BigEndian = bigEndian;
        }

        public int Length => Data.Length;

        public byte U8(int o) => Data[o];
        public sbyte S8(int o) => (sbyte)Data[o];

        public ushort U16(int o) => BigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(o))
            : BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(o));

        public short S16(int o) => BigEndian
            ? BinaryPrimitives.ReadInt16BigEndian(Data.AsSpan(o))
            : BinaryPrimitives.ReadInt16LittleEndian(Data.AsSpan(o));

        public uint U32(int o) => BigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(o))
            : BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(o));

        public int S32(int o) => (int)U32(o);

        public float F32(int o) => BitConverter.Int32BitsToSingle(S32(o));

        public string CStr(int o, int max = int.MaxValue)
        {
            if (o < 0 || o >= Data.Length) return string.Empty;
            int end = o;
            int lim = (int)Math.Min((long)Data.Length, (long)o + max);
            while (end < lim && Data[end] != 0) end++;
            return Encoding.Latin1.GetString(Data, o, end - o);
        }

        public string FixedStr(int o, int len) => CStr(o, len);

        public static int Align(int v, int a) => (v + a - 1) & ~(a - 1);
    }
}
