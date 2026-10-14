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
        public IReadOnlyList<RkvArchive> Archives => archives;
        public IReadOnlyDictionary<string, RkvArchive> ChunkPacks => bpkByName;
        public List<string> Log { get; } = new();

        private readonly Dictionary<string, string> looseFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<RkvArchive> archives = new();
        private readonly Dictionary<string, RkvArchive> bpkByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<(RkvArchive arc, RkvArchive.Entry e)>> archivedFiles =
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
            var packs = files.Where(file => file.EndsWith(".bpk", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(file => file.Contains("extracted", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            var rkvs = files.Where(file => file.EndsWith(".rkv", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var file in packs.Concat(rkvs))
            {
                bool isPack = file.EndsWith(".bpk", StringComparison.OrdinalIgnoreCase);
                try
                {
                    progress?.Invoke("Indexing " + Path.GetFileName(file));
                    var archive = RkvArchive.Open(file, loadIntoMemory: false);
                    archives.Add(archive);
                    if (isPack)
                    {
                        string chunk = Path.GetFileNameWithoutExtension(file);
                        bpkByName.TryAdd(chunk, archive);
                    }
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.Extension == ".bpk")
                        {
                            // BPK nested inside a full RKV: materialise lazily on first use.
                            string chunk = Path.GetFileNameWithoutExtension(entry.Name);
                            if (!bpkByName.ContainsKey(chunk)) lazyPacks.TryAdd(chunk, (archive, entry));
                            continue;
                        }
                        AddArchived(archive, entry);
                    }
                }
                catch (Exception exception) { Log.Add($"{Path.GetFileName(file)}: {exception.Message}"); }
            }
            foreach (var file in files)
            {
                string extension = Path.GetExtension(file);
                if (!AssetExt.Contains(extension)) continue;
                string name = Path.GetFileName(file);
                if (!looseFiles.TryGetValue(name, out var prev)
                    || (file.Contains("extracted", StringComparison.OrdinalIgnoreCase) && !prev.Contains("extracted", StringComparison.OrdinalIgnoreCase)))
                    looseFiles[name] = file;
            }
            Log.Add($"Indexed {looseFiles.Count} loose files, {archives.Count} archives, {bpkByName.Count} chunk packs");
        }

        private readonly Dictionary<string, (RkvArchive arc, RkvArchive.Entry e)> lazyPacks = new(StringComparer.OrdinalIgnoreCase);

        private void AddArchived(RkvArchive archive, RkvArchive.Entry entry)
        {
            if (!archivedFiles.TryGetValue(entry.Name, out var list)) archivedFiles[entry.Name] = list = new();
            list.Add((archive, entry));
        }

        private static void Collect(string folder, List<string> files, int depth)
        {
            if (depth > 12) return;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder)) files.Add(file);
                foreach (var directory in Directory.EnumerateDirectories(folder))
                {
                    if (SkipDirs.Contains(Path.GetFileName(directory))) continue;
                    Collect(directory, files, depth + 1);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        public bool Exists(string fileName, IEnumerable<RkvArchive> preferred = null) =>
            (preferred != null && preferred.Any(archive => archive != null && archive.Contains(fileName)))
            || looseFiles.ContainsKey(fileName) || archivedFiles.ContainsKey(fileName);

        /// <summary>
        /// Reads a file by name. Search order: preferred archives (e.g. the chunk's own BPK),
        /// loose extracted files, then any archive.
        /// </summary>
        public byte[] Read(string fileName, IEnumerable<RkvArchive> preferred = null)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            if (preferred != null)
                foreach (var archive in preferred)
                {
                    if (archive == null) continue;
                    var entry = archive.Find(fileName);
                    if (entry != null) return archive.Read(entry);
                }
            if (looseFiles.TryGetValue(fileName, out var path)) return File.ReadAllBytes(path);
            if (archivedFiles.TryGetValue(fileName, out var list) && list.Count > 0) return list[0].arc.Read(list[0].e);
            return null;
        }

        public string Describe(string fileName, IEnumerable<RkvArchive> preferred = null)
        {
            if (preferred != null)
                foreach (var archive in preferred)
                    if (archive != null && archive.Contains(fileName)) return Path.GetFileName(archive.SourcePath);
            if (looseFiles.TryGetValue(fileName, out var path)) return Path.GetRelativePath(Root, path);
            if (archivedFiles.TryGetValue(fileName, out var list) && list.Count > 0) return Path.GetFileName(list[0].arc.SourcePath);
            return "(missing)";
        }

        public IEnumerable<string> AllNames(string extension = null)
        {
            var set = new HashSet<string>(looseFiles.Keys, StringComparer.OrdinalIgnoreCase);
            set.UnionWith(archivedFiles.Keys);
            return extension == null ? set : set.Where(name => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        }

        public RkvArchive ChunkPack(string chunkName)
        {
            if (string.IsNullOrEmpty(chunkName)) return null;
            if (bpkByName.TryGetValue(chunkName, out var archive)) return archive;
            if (lazyPacks.TryGetValue(chunkName, out var lazyPack))
            {
                try
                {
                    var inner = RkvArchive.FromBytes(lazyPack.arc.Read(lazyPack.e), lazyPack.arc.SourcePath + "|" + lazyPack.e.Name);
                    archives.Add(inner);
                    bpkByName[chunkName] = inner;
                    foreach (var entry in inner.Entries) AddArchived(inner, entry);
                    return inner;
                }
                catch (Exception exception) { Log.Add($"{lazyPack.e.Name}: {exception.Message}"); }
            }
            return null;
        }

        public IEnumerable<string> ChunkPackNames => bpkByName.Keys.Concat(lazyPacks.Keys).Distinct(StringComparer.OrdinalIgnoreCase);

        public BniFile ReadBni(string fileName, IEnumerable<RkvArchive> preferred = null)
        {
            var data = Read(fileName, preferred);
            return data == null ? null : BniFile.Parse(data, fileName);
        }

        /// <summary>Find the folder that most likely holds the game data under a user-picked folder.</summary>
        public static bool LooksLikeGameRoot(string directory)
        {
            try
            {
                return Directory.EnumerateFiles(directory, "levels.ini.bni", SearchOption.AllDirectories).Any()
                    || Directory.EnumerateFiles(directory, "*.BPK", SearchOption.AllDirectories).Any()
                    || Directory.EnumerateFiles(directory, "*.RKV", SearchOption.AllDirectories).Any();
            }
            catch { return false; }
        }
    }
}
