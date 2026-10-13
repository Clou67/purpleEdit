using System.Globalization;
using System.Text;
using Mk2.IO;

namespace Mk2.Formats
{
    /// <summary>
    /// One line of a compiled Krome INI (".bni"): optional section name ("name X"),
    /// optional field key with string values, optional mask, and nested child lines.
    /// </summary>
    public sealed class BniNode
    {
        public string Section;              // non-null when this line starts a section ("name X")
        public string Key;                  // field key, may be null for bare section lines
        public List<string> Values = new();
        public string Mask;
        public List<BniNode> Children = new();
        public BniNode Parent;

        public bool IsSection => Section != null;
        public string Value => Values.Count > 0 ? Values[0] : null;
        public string JoinedValues => string.Join(" ", Values);

        public BniNode Child(string key) =>
            Children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

        public IEnumerable<BniNode> ChildrenNamed(string key) =>
            Children.Where(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

        public string Get(string key) => Child(key)?.Value;

        public float GetFloat(string key, int index = 0, float def = 0)
        {
            var c = Child(key);
            if (c == null || c.Values.Count <= index) return def;
            return Bni.ParseFloat(c.Values[index], def);
        }

        public bool TryGetVec3(string key, out float x, out float y, out float z)
        {
            x = y = z = 0;
            var c = Child(key);
            if (c == null || c.Values.Count < 3) return false;
            x = Bni.ParseFloat(c.Values[0]);
            y = Bni.ParseFloat(c.Values[1]);
            z = Bni.ParseFloat(c.Values[2]);
            return true;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            if (Section != null) sb.Append("name ").Append(Section).Append(' ');
            if (Key != null)
            {
                sb.Append(Key);
                foreach (var v in Values) sb.Append(' ').Append(v);
                if (Mask != null) sb.Append(" \\").Append(Mask).Append('\\');
            }
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// A logical section: either a "name X" line with indented children (lc3 style)
    /// or a "name X" line followed by sibling fields up to the next section (model.bni style).
    /// </summary>
    public sealed class BniSection
    {
        public string Name;
        public BniNode Header;
        public List<BniNode> Fields = new();
        public int Index;

        public BniNode Field(string key) =>
            Fields.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        public IEnumerable<BniNode> FieldsNamed(string key) =>
            Fields.Where(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        public string Get(string key) => Field(key)?.Value;
    }

    /// <summary>
    /// Compiled Krome INI file (".ini.bni", ".lc3.bni", ".model.bni", ".add.bni", ...).
    ///
    /// Header (0x44 bytes):
    ///   0x00 char[32] source path
    ///   0x20 int  0x64 (endianness marker / version)
    ///   0x24 int  lineCount
    ///   0x28 int  dataLength
    ///   0x2C int  stringTableOffset (relative to 0x44)
    ///   0x30 int  shortTableOffset  (relative to 0x44)
    ///   0x34 int  hashTableOffset
    ///   0x38 int  hashDivisor
    ///   0x3C int  binarySearchTableOffset
    ///   0x40 int  sectionCount
    /// Lines (0x10 each @0x44): u16 valueCount (0xFFFF = end of block), u16 sectionName,
    ///   u16 fieldName, u16 firstValueIndex, u16 childLineIndex, u16 mask, pad[4].
    ///   String refs are in 4-byte units into the string table; values go through the short table.
    /// </summary>
    public sealed class BniFile
    {
        public string SourcePath;
        public string Name;
        public List<BniNode> Roots = new();
        public bool BigEndian;

        private List<BniSection> _sections;

        public static BniFile Parse(byte[] data, string name = null)
        {
            bool le = data[0x20] == 0x64;
            var b = new Bin(data, !le);
            var f = new BniFile { BigEndian = !le, Name = name };
            f.SourcePath = b.CStr(0, 0x20);
            int lineCount = b.S32(0x24);
            int st = 0x44 + b.S32(0x2C);
            int sh = 0x44 + b.S32(0x30);

            string Str(int idx) => b.CStr(st + idx * 4);

            var visited = new bool[lineCount];
            List<BniNode> Walk(int start, BniNode parent, int depth)
            {
                var list = new List<BniNode>();
                if (depth > 64) return list;
                for (int i = start; i < lineCount; i++)
                {
                    int o = 0x44 + i * 0x10;
                    ushort cnt = b.U16(o);
                    if (cnt == 0xFFFF) break;
                    if (visited[i]) break;
                    visited[i] = true;
                    ushort sec = b.U16(o + 2), fld = b.U16(o + 4), first = b.U16(o + 6),
                           child = b.U16(o + 8), mask = b.U16(o + 10);
                    var n = new BniNode { Parent = parent };
                    if (sec != 0xFFFF) n.Section = Str(sec);
                    if (fld != 0xFFFF)
                    {
                        n.Key = Str(fld);
                        for (int k = 0; k < cnt; k++)
                            n.Values.Add(Str(b.U16(sh + (first + k) * 2)));
                        if (mask != 0xFFFF) n.Mask = Str(mask);
                    }
                    if (child != 0xFFFF && child < lineCount)
                        n.Children = Walk(child, n, depth + 1);
                    list.Add(n);
                }
                return list;
            }
            f.Roots = Walk(0, null, 0);
            return f;
        }

        public static BniFile Load(string path) => Parse(File.ReadAllBytes(path), Path.GetFileName(path));

        /// <summary>All logical sections at the top level.</summary>
        public List<BniSection> Sections
        {
            get
            {
                if (_sections != null) return _sections;
                _sections = new List<BniSection>();
                BniSection cur = null;
                foreach (var n in Roots)
                {
                    if (n.IsSection)
                    {
                        cur = new BniSection { Name = n.Section, Header = n, Index = _sections.Count };
                        if (n.Key != null) cur.Fields.Add(new BniNode { Key = n.Key, Values = n.Values, Mask = n.Mask });
                        cur.Fields.AddRange(n.Children);
                        _sections.Add(cur);
                    }
                    else if (cur != null && cur.Header.Children.Count == 0)
                    {
                        cur.Fields.Add(n);
                    }
                }
                return _sections;
            }
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(SourcePath);
            void W(BniNode n, int d)
            {
                if (n.IsSection && d == 0) sb.AppendLine();
                sb.Append(' ', d * 2).AppendLine(n.ToString());
                foreach (var c in n.Children) W(c, d + 1);
            }
            foreach (var r in Roots) W(r, 0);
            return sb.ToString();
        }
    }

    public static class Bni
    {
        public static float ParseFloat(string s, float def = 0)
        {
            if (string.IsNullOrEmpty(s)) return def;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
        }

        public static int ParseInt(string s, int def = 0)
        {
            if (string.IsNullOrEmpty(s)) return def;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
        }
    }
}
