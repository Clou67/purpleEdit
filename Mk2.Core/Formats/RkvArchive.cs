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
        private readonly Dictionary<string, Entry> byName = new(StringComparer.OrdinalIgnoreCase);
        private byte[] contents;          // when loaded fully in memory
        private readonly object readLock = new();

        public static bool IsRkv(byte[] head) =>
            head.Length >= 4 && head[0] == 'R' && head[1] == 'K' && head[2] == 'V' && head[3] == '2';

        public static RkvArchive Open(string path, bool loadIntoMemory = true)
        {
            var archive = new RkvArchive { SourcePath = path };
            if (loadIntoMemory)
            {
                archive.contents = File.ReadAllBytes(path);
                archive.ParseDirectory(archive.contents);
            }
            else
            {
                using var stream = File.OpenRead(path);
                var head = new byte[0x80];
                stream.ReadExactly(head);
                var header = new Bin(head);
                int count = header.S32(4);
                bool bigEndian = count < 0 || count > 1_000_000;
                header = new Bin(head, bigEndian);
                count = header.S32(4);
                int nameSize = header.S32(8);
                int infoOff = header.S32(0x14);
                int dirLen = infoOff + count * 0x14 + nameSize;
                var directory = new byte[dirLen];
                stream.Position = 0;
                stream.ReadExactly(directory);
                archive.ParseDirectory(directory);
            }
            return archive;
        }

        public static RkvArchive FromBytes(byte[] data, string name)
        {
            var archive = new RkvArchive { SourcePath = name, contents = data };
            archive.ParseDirectory(data);
            return archive;
        }

        private void ParseDirectory(byte[] directory)
        {
            if (!IsRkv(directory)) throw new InvalidDataException("Not an RKV2 archive");
            var reader = new Bin(directory);
            int count = reader.S32(4);
            BigEndian = count < 0 || count > 1_000_000;
            reader = new Bin(directory, BigEndian);
            count = reader.S32(4);
            int infoOff = reader.S32(0x14);
            int nameBase = infoOff + count * 0x14;
            for (int index = 0; index < count; index++)
            {
                int offset = infoOff + index * 0x14;
                var entry = new Entry
                {
                    Name = reader.CStr(nameBase + reader.S32(offset)),
                    Size = reader.S32(offset + 8),
                    Offset = reader.S32(offset + 12),
                    Crc = reader.U32(offset + 16),
                };
                Entries.Add(entry);
                byName.TryAdd(entry.Name, entry);
            }
        }

        public bool Contains(string name) => byName.ContainsKey(name);
        public Entry Find(string name) => byName.TryGetValue(name, out var entry) ? entry : null;

        public byte[] Read(Entry entry)
        {
            var buffer = new byte[entry.Size];
            if (contents != null)
            {
                Buffer.BlockCopy(contents, entry.Offset, buffer, 0, entry.Size);
                return buffer;
            }
            lock (readLock)
            {
                using var stream = File.OpenRead(SourcePath);
                stream.Position = entry.Offset;
                stream.ReadExactly(buffer);
            }
            return buffer;
        }

        public byte[] Read(string name)
        {
            var entry = Find(name);
            return entry == null ? null : Read(entry);
        }

        public void ExtractAll(string outDir, bool groupByExtension = false)
        {
            Directory.CreateDirectory(outDir);
            foreach (var entry in Entries)
            {
                string directory = outDir;
                if (groupByExtension)
                {
                    string extension = Path.GetExtension(entry.Name).TrimStart('.').ToUpperInvariant();
                    if (extension.Length > 0) directory = Path.Combine(outDir, extension);
                }
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, entry.Name), Read(entry));
            }
        }
    }
}
