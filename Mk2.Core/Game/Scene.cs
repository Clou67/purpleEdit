using System.Numerics;
using Mk2.Formats;

namespace Mk2.Game
{
    public sealed class LoadedModel
    {
        public string Name;
        public Model Model;
        public string Source;
        public List<Material> Materials = new();       // parallel to Model.MaterialNames
        public List<Collision> Collisions = new();     // <name>_NN.cgr
        public override string ToString() => Name;
    }

    public sealed class SceneChunk
    {
        public ChunkDef Def;
        public RkvArchive Pack;
        public List<(OverlayRef overlay, LoadedModel model)> Overlays = new();
        public List<(string name, Collision collision)> Grounds = new();
        public LoadedModel EnvDome;
        public BniFile Layout;
        public List<LevelObject> Objects = new();
        public Dictionary<LevelObject, LoadedModel> ObjectModels = new();
        public override string ToString() => Def.Name;
    }

    public sealed class LevelScene
    {
        public LevelDef Def;
        public List<SceneChunk> Chunks = new();
        public List<string> Warnings = new();
        public AssetCache Assets;

        public IEnumerable<LevelObject> AllObjects => Chunks.SelectMany(c => c.Objects);

        public LoadedModel ModelFor(LevelObject o)
        {
            foreach (var c in Chunks)
                if (c.ObjectModels.TryGetValue(o, out var m)) return m;
            return null;
        }
    }

    /// <summary>Caches decoded models, materials and textures for one scene/session.</summary>
    public sealed class AssetCache
    {
        public readonly GameData Game;
        private readonly Dictionary<string, LoadedModel> _models = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Material> _materials = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Texture> _textures = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Warnings { get; } = new();

        public AssetCache(GameData game) { Game = game; }

        public LoadedModel GetModel(string name, IList<RkvArchive> packs)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_models.TryGetValue(name, out var cached)) return cached;
            var mdl = Game.Read(name + ".mdl", packs);
            var mdg = Game.Read(name + ".mdg", packs);
            if (mdl == null || mdg == null) { _models[name] = null; return null; }
            LoadedModel lm;
            try
            {
                lm = new LoadedModel
                {
                    Name = name,
                    Model = Model.Load(mdl, mdg, name),
                    Source = Game.Describe(name + ".mdl", packs),
                };
                foreach (var w in lm.Model.Warnings) Warnings.Add($"{name}: {w}");
                foreach (var mn in lm.Model.MaterialNames) lm.Materials.Add(GetMaterial(mn, packs));
                for (int i = 0; i < 16; i++)
                {
                    var c = Game.Read($"{name}_{i:00}.cgr", packs);
                    if (c == null) { if (i > 0) break; else continue; }
                    try { lm.Collisions.Add(Collision.Load(c, $"{name}_{i:00}")); } catch { }
                }
            }
            catch (Exception ex)
            {
                Warnings.Add($"{name}: {ex.Message}");
                lm = null;
            }
            _models[name] = lm;
            return lm;
        }

        public Material GetMaterial(string name, IList<RkvArchive> packs)
        {
            if (_materials.TryGetValue(name, out var m)) return m;
            var d = Game.Read(name + ".min", packs);
            try { m = d != null ? Material.Parse(d, name) : Material.Default(name); }
            catch { m = Material.Default(name); }
            _materials[name] = m;
            // Pre-resolve the texture with the same pack priority.
            GetTexture(m.TextureName, packs);
            return m;
        }

        public Texture GetTexture(string name, IList<RkvArchive> packs = null)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_textures.TryGetValue(name, out var t)) return t;
            var d = Game.Read(name + ".tex", packs);
            try { t = d != null ? Texture.Decode(d, name) : null; }
            catch (Exception ex) { Warnings.Add($"{name}.tex: {ex.Message}"); t = null; }
            _textures[name] = t;
            return t;
        }

        public Collision GetCollision(string fileName, IList<RkvArchive> packs)
        {
            var d = Game.Read(fileName, packs);
            if (d == null) return null;
            try { return Collision.Load(d, Path.GetFileNameWithoutExtension(fileName)); }
            catch (Exception ex) { Warnings.Add($"{fileName}: {ex.Message}"); return null; }
        }
    }

    public static class LevelLoader
    {
        public static List<LevelDef> LoadLevelList(GameData game)
        {
            var f = game.ReadBni("levels.ini.bni");
            return f == null ? new List<LevelDef>() : LevelsIni.Parse(f);
        }

        public static LevelScene Load(GameData game, LevelDef def, ModelDb db, AssetCache cache = null,
                                      Action<string> progress = null, ISet<string> onlyChunks = null)
        {
            cache ??= new AssetCache(game);
            var scene = new LevelScene { Def = def, Assets = cache };
            var envPack = game.ChunkPack(def.EnvChunk);

            foreach (var cd in def.Chunks)
            {
                if (onlyChunks != null && !cd.IsEnvironment && !onlyChunks.Contains(cd.Name)) continue;
                progress?.Invoke("Loading " + cd.Name);
                var sc = new SceneChunk { Def = cd, Pack = game.ChunkPack(cd.Name) };
                if (sc.Pack == null) scene.Warnings.Add($"{cd.Name}: chunk pack (.BPK) not found");
                var packs = new List<RkvArchive> { sc.Pack, envPack }.Where(p => p != null).Distinct().ToList();

                foreach (var ov in cd.Overlays)
                {
                    var m = cache.GetModel(ov.Name, packs);
                    if (m == null) scene.Warnings.Add($"{cd.Name}: overlay {ov.Name} not found");
                    sc.Overlays.Add((ov, m));
                }
                foreach (var g in cd.Grounds)
                {
                    var c = cache.GetCollision(g + "_all.cgr", packs) ?? cache.GetCollision(g + ".cgr", packs);
                    if (c == null) scene.Warnings.Add($"{cd.Name}: ground collision {g} not found");
                    else sc.Grounds.Add((g, c));
                }
                if (cd.EnvDome != null) sc.EnvDome = cache.GetModel(cd.EnvDome, packs);

                sc.Layout = game.ReadBni(cd.Name + ".lc3.bni", packs);
                if (sc.Layout != null)
                {
                    int idx = 0;
                    foreach (var s in sc.Layout.Sections)
                    {
                        var o = LevelObject.FromSection(s, cd.Name, idx++, db);
                        sc.Objects.Add(o);
                        if (db != null && db.IsHidden(o.ClassName)) continue;
                        if (o.Category != ObjectCategory.Prop && o.Category != ObjectCategory.Critter && o.Category != ObjectCategory.Checkpoint) continue;
                        var cands = db?.ModelCandidates(o.ClassName) ?? new List<string> { o.ClassName };
                        foreach (var cand in cands)
                        {
                            if (!game.Exists(cand + ".mdl", packs)) continue;
                            var m = cache.GetModel(cand, packs);
                            if (m != null) { sc.ObjectModels[o] = m; break; }
                        }
                    }
                }
                scene.Chunks.Add(sc);
            }
            scene.Warnings.AddRange(cache.Warnings);
            return scene;
        }

        /// <summary>Standalone BPK: show every model in the pack side by side (no layout).</summary>
        public static LevelScene LoadPack(GameData game, string chunkName, AssetCache cache = null)
        {
            cache ??= new AssetCache(game);
            var pack = game.ChunkPack(chunkName);
            var def = new LevelDef { Name = chunkName };
            var cd = new ChunkDef { Name = chunkName };
            def.Chunks.Add(cd);
            var scene = new LevelScene { Def = def, Assets = cache };
            var sc = new SceneChunk { Def = cd, Pack = pack };
            if (pack != null)
            {
                var packs = new List<RkvArchive> { pack };
                foreach (var e in pack.Entries.Where(e => e.Extension == ".mdl"))
                {
                    string n = Path.GetFileNameWithoutExtension(e.Name);
                    var m = cache.GetModel(n, packs);
                    if (m != null) { cd.Overlays.Add(new OverlayRef { Name = n }); sc.Overlays.Add((cd.Overlays[^1], m)); }
                }
            }
            scene.Chunks.Add(sc);
            scene.Warnings.AddRange(cache.Warnings);
            return scene;
        }
    }
}
