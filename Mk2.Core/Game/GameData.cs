using Mk2.Formats;

namespace Mk2.Game
{
    /// <summary>
    /// Indexes every asset reachable from a root folder (or a single archive file): loose extracted files (any layout),
    /// chunk packs (.BPK) and full archives (.RKV). Lookups are by file name, case-insensitive.
    /// </summary>
    public sealed class GameData
    {
        public string Root { get; }
        public IReadOnlyList<RkvArchive> Archives => _archives;
        public IReadOnlyDictionary<string, RkvArchive> ChunkPacks => _bpkByName;
        public List<string> Log { get; } = new();

        private readonly Dictionary<string, string> _loose = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<RkvArchive> _archives = new();
        private readonly Dictionary<string, RkvArchive> _bpkByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<(RkvArchive arc, RkvArchive.Entry e)>> _archived =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", "bin", "obj", "node_modules", "Mk2LevelViewer", "purpleEdit" };

        private static readonly HashSet<string> AssetExt = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mdl", ".mdg", ".tex", ".min", ".bni", ".cgr", ".anm", ".ang", ".bbi", ".pkg", ".txt", ".sbk", ".sub"
        };

        public GameData(string root, Action<string> progress = null)
        {
            Root = root;
            var files = new List<string>();
            if (File.Exists(root)) files.Add(root);
            else Collect(root, files, 0);
            files.Sort(StringComparer.OrdinalIgnoreCase);

            // Pass 1: loose chunk packs (.BPK). Pass 2: full archives (.RKV). Pass 3: loose files.
            var packs = files.Where(f => f.EndsWith(".bpk", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(f => f.Contains("extracted", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            var rkvs = files.Where(f => f.EndsWith(".rkv", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var f in packs.Concat(rkvs))
            {
                bool isPack = f.EndsWith(".bpk", StringComparison.OrdinalIgnoreCase);
                try
                {
                    progress?.Invoke("Indexing " + Path.GetFileName(f));
                    var a = RkvArchive.Open(f, loadIntoMemory: false);
                    _archives.Add(a);
                    if (isPack)
                    {
                        string chunk = Path.GetFileNameWithoutExtension(f);
                        _bpkByName.TryAdd(chunk, a);
                    }
                    foreach (var e in a.Entries)
                    {
                        if (e.Extension == ".bpk")
                        {
                            // BPK nested inside a full RKV: materialise lazily on first use.
                            string chunk = Path.GetFileNameWithoutExtension(e.Name);
                            if (!_bpkByName.ContainsKey(chunk)) _lazyPacks.TryAdd(chunk, (a, e));
                            continue;
                        }
                        AddArchived(a, e);
                    }
                }
                catch (Exception ex) { Log.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
            }
            foreach (var f in files)
            {
                string ext = Path.GetExtension(f);
                if (!AssetExt.Contains(ext)) continue;
                string name = Path.GetFileName(f);
                if (!_loose.TryGetValue(name, out var prev)
                    || (f.Contains("extracted", StringComparison.OrdinalIgnoreCase) && !prev.Contains("extracted", StringComparison.OrdinalIgnoreCase)))
                    _loose[name] = f;
            }
            Log.Add($"Indexed {_loose.Count} loose files, {_archives.Count} archives, {_bpkByName.Count} chunk packs");
        }

        private readonly Dictionary<string, (RkvArchive arc, RkvArchive.Entry e)> _lazyPacks = new(StringComparer.OrdinalIgnoreCase);

        private void AddArchived(RkvArchive a, RkvArchive.Entry e)
        {
            if (!_archived.TryGetValue(e.Name, out var list)) _archived[e.Name] = list = new();
            list.Add((a, e));
        }

        private static void Collect(string dir, List<string> files, int depth)
        {
            if (depth > 12) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir)) files.Add(f);
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    if (SkipDirs.Contains(Path.GetFileName(d))) continue;
                    Collect(d, files, depth + 1);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        public bool Exists(string fileName, IEnumerable<RkvArchive> preferred = null) =>
            (preferred != null && preferred.Any(a => a != null && a.Contains(fileName)))
            || _loose.ContainsKey(fileName) || _archived.ContainsKey(fileName);

        /// <summary>
        /// Reads a file by name. Search order: preferred archives (e.g. the chunk's own BPK),
        /// loose extracted files, then any archive.
        /// </summary>
        public byte[] Read(string fileName, IEnumerable<RkvArchive> preferred = null)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            if (preferred != null)
                foreach (var a in preferred)
                {
                    if (a == null) continue;
                    var e = a.Find(fileName);
                    if (e != null) return a.Read(e);
                }
            if (_loose.TryGetValue(fileName, out var path)) return File.ReadAllBytes(path);
            if (_archived.TryGetValue(fileName, out var list) && list.Count > 0) return list[0].arc.Read(list[0].e);
            return null;
        }

        public string Describe(string fileName, IEnumerable<RkvArchive> preferred = null)
        {
            if (preferred != null)
                foreach (var a in preferred)
                    if (a != null && a.Contains(fileName)) return Path.GetFileName(a.SourcePath);
            if (_loose.TryGetValue(fileName, out var path)) return Path.GetRelativePath(Root, path);
            if (_archived.TryGetValue(fileName, out var list) && list.Count > 0) return Path.GetFileName(list[0].arc.SourcePath);
            return "(missing)";
        }

        public IEnumerable<string> AllNames(string extension = null)
        {
            var set = new HashSet<string>(_loose.Keys, StringComparer.OrdinalIgnoreCase);
            set.UnionWith(_archived.Keys);
            return extension == null ? set : set.Where(n => n.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        }

        public RkvArchive ChunkPack(string chunkName)
        {
            if (string.IsNullOrEmpty(chunkName)) return null;
            if (_bpkByName.TryGetValue(chunkName, out var a)) return a;
            if (_lazyPacks.TryGetValue(chunkName, out var lz))
            {
                try
                {
                    var inner = RkvArchive.FromBytes(lz.arc.Read(lz.e), lz.arc.SourcePath + "|" + lz.e.Name);
                    _archives.Add(inner);
                    _bpkByName[chunkName] = inner;
                    foreach (var e in inner.Entries) AddArchived(inner, e);
                    return inner;
                }
                catch (Exception ex) { Log.Add($"{lz.e.Name}: {ex.Message}"); }
            }
            return null;
        }

        public IEnumerable<string> ChunkPackNames => _bpkByName.Keys.Concat(_lazyPacks.Keys).Distinct(StringComparer.OrdinalIgnoreCase);

        public BniFile ReadBni(string fileName, IEnumerable<RkvArchive> preferred = null)
        {
            var d = Read(fileName, preferred);
            return d == null ? null : BniFile.Parse(d, fileName);
        }

        /// <summary>Find the folder that most likely holds the game data under a user-picked folder.</summary>
        public static bool LooksLikeGameRoot(string dir)
        {
            try
            {
                return Directory.EnumerateFiles(dir, "levels.ini.bni", SearchOption.AllDirectories).Any()
                    || Directory.EnumerateFiles(dir, "*.BPK", SearchOption.AllDirectories).Any()
                    || Directory.EnumerateFiles(dir, "*.RKV", SearchOption.AllDirectories).Any();
            }
            catch { return false; }
        }
    }
}
