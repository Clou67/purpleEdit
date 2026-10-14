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

        public byte U8(int offset) => Data[offset];
        public sbyte S8(int offset) => (sbyte)Data[offset];

        public ushort U16(int offset) => BigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(offset))
            : BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(offset));

        public short S16(int offset) => BigEndian
            ? BinaryPrimitives.ReadInt16BigEndian(Data.AsSpan(offset))
            : BinaryPrimitives.ReadInt16LittleEndian(Data.AsSpan(offset));

        public uint U32(int offset) => BigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(offset))
            : BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(offset));

        public int S32(int offset) => (int)U32(offset);

        public float F32(int offset) => BitConverter.Int32BitsToSingle(S32(offset));

        public string CStr(int offset, int max = int.MaxValue)
        {
            if (offset < 0 || offset >= Data.Length) return string.Empty;
            int end = offset;
            int limit = (int)Math.Min((long)Data.Length, (long)offset + max);
            while (end < limit && Data[end] != 0) end++;
            return Encoding.Latin1.GetString(Data, offset, end - offset);
        }

        public string FixedStr(int offset, int length) => CStr(offset, length);

        public static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
    }
}
