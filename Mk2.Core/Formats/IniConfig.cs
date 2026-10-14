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
            Children.FirstOrDefault(node => string.Equals(node.Key, key, StringComparison.OrdinalIgnoreCase));

        public IEnumerable<BniNode> ChildrenNamed(string key) =>
            Children.Where(node => string.Equals(node.Key, key, StringComparison.OrdinalIgnoreCase));

        public string Get(string key) => Child(key)?.Value;

        public float GetFloat(string key, int index = 0, float fallback = 0)
        {
            var node = Child(key);
            if (node == null || node.Values.Count <= index) return fallback;
            return Bni.ParseFloat(node.Values[index], fallback);
        }

        public bool TryGetVec3(string key, out float xValue, out float yValue, out float zValue)
        {
            xValue = yValue = zValue = 0;
            var node = Child(key);
            if (node == null || node.Values.Count < 3) return false;
            xValue = Bni.ParseFloat(node.Values[0]);
            yValue = Bni.ParseFloat(node.Values[1]);
            zValue = Bni.ParseFloat(node.Values[2]);
            return true;
        }

        public override string ToString()
        {
            var text = new StringBuilder();
            if (Section != null) text.Append("name ").Append(Section).Append(' ');
            if (Key != null)
            {
                text.Append(Key);
                foreach (var value in Values) text.Append(' ').Append(value);
                if (Mask != null) text.Append(" \\").Append(Mask).Append('\\');
            }
            return text.ToString().TrimEnd();
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
            Fields.FirstOrDefault(node => string.Equals(node.Key, key, StringComparison.OrdinalIgnoreCase));
        public IEnumerable<BniNode> FieldsNamed(string key) =>
            Fields.Where(node => string.Equals(node.Key, key, StringComparison.OrdinalIgnoreCase));
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

        private List<BniSection> sections;

        public static BniFile Parse(byte[] data, string name = null)
        {
            bool littleEndian = data[0x20] == 0x64;
            var reader = new Bin(data, !littleEndian);
            var bniFile = new BniFile { BigEndian = !littleEndian, Name = name };
            bniFile.SourcePath = reader.CStr(0, 0x20);
            int lineCount = reader.S32(0x24);
            int stringTable = 0x44 + reader.S32(0x2C);
            int shortTable = 0x44 + reader.S32(0x30);

            string Str(int stringIndex) => reader.CStr(stringTable + stringIndex * 4);

            var visited = new bool[lineCount];
            List<BniNode> Walk(int start, BniNode parent, int depth)
            {
                var list = new List<BniNode>();
                if (depth > 64) return list;
                for (int index = start; index < lineCount; index++)
                {
                    int offset = 0x44 + index * 0x10;
                    ushort valueCount = reader.U16(offset);
                    if (valueCount == 0xFFFF) break;
                    if (visited[index]) break;
                    visited[index] = true;
                    ushort sectionName = reader.U16(offset + 2), fieldName = reader.U16(offset + 4), first = reader.U16(offset + 6),
                           child = reader.U16(offset + 8), mask = reader.U16(offset + 10);
                    var node = new BniNode { Parent = parent };
                    if (sectionName != 0xFFFF) node.Section = Str(sectionName);
                    if (fieldName != 0xFFFF)
                    {
                        node.Key = Str(fieldName);
                        for (int valueIndex = 0; valueIndex < valueCount; valueIndex++)
                            node.Values.Add(Str(reader.U16(shortTable + (first + valueIndex) * 2)));
                        if (mask != 0xFFFF) node.Mask = Str(mask);
                    }
                    if (child != 0xFFFF && child < lineCount)
                        node.Children = Walk(child, node, depth + 1);
                    list.Add(node);
                }
                return list;
            }
            bniFile.Roots = Walk(0, null, 0);
            return bniFile;
        }

        public static BniFile Load(string path) => Parse(File.ReadAllBytes(path), Path.GetFileName(path));

        /// <summary>All logical sections at the top level.</summary>
        public List<BniSection> Sections
        {
            get
            {
                if (sections != null) return sections;
                sections = new List<BniSection>();
                BniSection current = null;
                foreach (var node in Roots)
                {
                    if (node.IsSection)
                    {
                        current = new BniSection { Name = node.Section, Header = node, Index = sections.Count };
                        if (node.Key != null) current.Fields.Add(new BniNode { Key = node.Key, Values = node.Values, Mask = node.Mask });
                        current.Fields.AddRange(node.Children);
                        sections.Add(current);
                    }
                    else if (current != null && current.Header.Children.Count == 0)
                    {
                        current.Fields.Add(node);
                    }
                }
                return sections;
            }
        }

        public string ToText()
        {
            var text = new StringBuilder();
            text.AppendLine(SourcePath);
            void WriteNode(BniNode node, int depth)
            {
                if (node.IsSection && depth == 0) text.AppendLine();
                text.Append(' ', depth * 2).AppendLine(node.ToString());
                foreach (var child in node.Children) WriteNode(child, depth + 1);
            }
            foreach (var root in Roots) WriteNode(root, 0);
            return text.ToString();
        }
    }

    public static class Bni
    {
        public static float ParseFloat(string text, float fallback = 0)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        }

        public static int ParseInt(string text, int fallback = 0)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        }
    }
}
