using Mk2.IO;

namespace Mk2.Formats
{
    /// <summary>
    /// RKV2 archive (used for DATA_*.RKV and the per-chunk .BPK packs).
    ///
    /// Header (0x80 bytes, little endian on PS2):
    ///   0x00 char[4] "RKV2"
    ///   0x04 int     entryCount
    ///   0x08 int     nameTableSize
    ///   0x0C int     addendumCount   (full-path aliases; 0 in BPKs)
    ///   0x10 int     unknown (0x10 observed)
    ///   0x14 int     entryTableOffset
    ///   0x18 int     addendumTableSize
    /// Entry (0x14 bytes): nameOffset, groupingRef, size, dataOffset, crc32
    /// Name table immediately follows the entry table (offsets relative to its start).
    /// </summary>
    public sealed class RkvArchive
    {
        public sealed class Entry
        {
            public string Name;
            public int Size;
            public int Offset;
            public uint Crc;
            public string Extension => Path.GetExtension(Name).ToLowerInvariant();
            public override string ToString() => $"{Name} ({Size} bytes)";
        }

        public string SourcePath { get; private set; }
        public bool BigEndian { get; private set; }
        public List<Entry> Entries { get; } = new();
        private readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);
        private byte[] _data;          // when loaded fully in memory
        private readonly object _lock = new();

        public static bool IsRkv(byte[] head) =>
            head.Length >= 4 && head[0] == 'R' && head[1] == 'K' && head[2] == 'V' && head[3] == '2';

        public static RkvArchive Open(string path, bool loadIntoMemory = true)
        {
            var a = new RkvArchive { SourcePath = path };
            if (loadIntoMemory)
            {
                a._data = File.ReadAllBytes(path);
                a.ParseDirectory(a._data);
            }
            else
            {
                using var fs = File.OpenRead(path);
                var head = new byte[0x80];
                fs.ReadExactly(head);
                var tmp = new Bin(head);
                int count = tmp.S32(4);
                bool be = count < 0 || count > 1_000_000;
                tmp = new Bin(head, be);
                count = tmp.S32(4);
                int nameSize = tmp.S32(8);
                int infoOff = tmp.S32(0x14);
                int dirLen = infoOff + count * 0x14 + nameSize;
                var dir = new byte[dirLen];
                fs.Position = 0;
                fs.ReadExactly(dir);
                a.ParseDirectory(dir);
            }
            return a;
        }

        public static RkvArchive FromBytes(byte[] data, string name)
        {
            var a = new RkvArchive { SourcePath = name, _data = data };
            a.ParseDirectory(data);
            return a;
        }

        private void ParseDirectory(byte[] d)
        {
            if (!IsRkv(d)) throw new InvalidDataException("Not an RKV2 archive");
            var b = new Bin(d);
            int count = b.S32(4);
            BigEndian = count < 0 || count > 1_000_000;
            b = new Bin(d, BigEndian);
            count = b.S32(4);
            int infoOff = b.S32(0x14);
            int nameBase = infoOff + count * 0x14;
            for (int i = 0; i < count; i++)
            {
                int o = infoOff + i * 0x14;
                var e = new Entry
                {
                    Name = b.CStr(nameBase + b.S32(o)),
                    Size = b.S32(o + 8),
                    Offset = b.S32(o + 12),
                    Crc = b.U32(o + 16),
                };
                Entries.Add(e);
                _byName.TryAdd(e.Name, e);
            }
        }

        public bool Contains(string name) => _byName.ContainsKey(name);
        public Entry Find(string name) => _byName.TryGetValue(name, out var e) ? e : null;

        public byte[] Read(Entry e)
        {
            var buf = new byte[e.Size];
            if (_data != null)
            {
                Buffer.BlockCopy(_data, e.Offset, buf, 0, e.Size);
                return buf;
            }
            lock (_lock)
            {
                using var fs = File.OpenRead(SourcePath);
                fs.Position = e.Offset;
                fs.ReadExactly(buf);
            }
            return buf;
        }

        public byte[] Read(string name)
        {
            var e = Find(name);
            return e == null ? null : Read(e);
        }

        public void ExtractAll(string outDir, bool groupByExtension = false)
        {
            Directory.CreateDirectory(outDir);
            foreach (var e in Entries)
            {
                string dir = outDir;
                if (groupByExtension)
                {
                    string ext = Path.GetExtension(e.Name).TrimStart('.').ToUpperInvariant();
                    if (ext.Length > 0) dir = Path.Combine(outDir, ext);
                }
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, e.Name), Read(e));
            }
        }
    }
}
