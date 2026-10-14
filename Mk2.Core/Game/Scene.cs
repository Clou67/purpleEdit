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

        public IEnumerable<LevelObject> AllObjects => Chunks.SelectMany(chunk => chunk.Objects);

        public LoadedModel ModelFor(LevelObject levelObject)
        {
            foreach (var chunk in Chunks)
                if (chunk.ObjectModels.TryGetValue(levelObject, out var loadedModel)) return loadedModel;
            return null;
        }
    }

    /// <summary>Caches decoded models, materials and textures for one scene/session.</summary>
    public sealed class AssetCache
    {
        public readonly GameData Game;
        private readonly Dictionary<string, LoadedModel> models = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Material> materials = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Texture> textures = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Warnings { get; } = new();

        public AssetCache(GameData game) { Game = game; }

        public LoadedModel GetModel(string name, IList<RkvArchive> packs)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (models.TryGetValue(name, out var cached)) return cached;
            var mdl = Game.Read(name + ".mdl", packs);
            var mdg = Game.Read(name + ".mdg", packs);
            if (mdl == null || mdg == null) { models[name] = null; return null; }
            LoadedModel loadedModel;
            try
            {
                loadedModel = new LoadedModel
                {
                    Name = name,
                    Model = Model.Load(mdl, mdg, name),
                    Source = Game.Describe(name + ".mdl", packs),
                };
                foreach (var warning in loadedModel.Model.Warnings) Warnings.Add($"{name}: {warning}");
                foreach (var materialName in loadedModel.Model.MaterialNames) loadedModel.Materials.Add(GetMaterial(materialName, packs));
                for (int index = 0; index < 16; index++)
                {
                    var collisionData = Game.Read($"{name}_{index:00}.cgr", packs);
                    if (collisionData == null) { if (index > 0) break; else continue; }
                    try { loadedModel.Collisions.Add(Collision.Load(collisionData, $"{name}_{index:00}")); } catch { }
                }
            }
            catch (Exception exception)
            {
                Warnings.Add($"{name}: {exception.Message}");
                loadedModel = null;
            }
            models[name] = loadedModel;
            return loadedModel;
        }

        public Material GetMaterial(string name, IList<RkvArchive> packs)
        {
            if (materials.TryGetValue(name, out var material)) return material;
            var data = Game.Read(name + ".min", packs);
            try { material = data != null ? Material.Parse(data, name) : Material.Default(name); }
            catch { material = Material.Default(name); }
            materials[name] = material;
            // Pre-resolve the texture with the same pack priority.
            GetTexture(material.TextureName, packs);
            return material;
        }

        public Texture GetTexture(string name, IList<RkvArchive> packs = null)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (textures.TryGetValue(name, out var texture)) return texture;
            var data = Game.Read(name + ".tex", packs);
            try { texture = data != null ? Texture.Decode(data, name) : null; }
            catch (Exception exception) { Warnings.Add($"{name}.tex: {exception.Message}"); texture = null; }
            textures[name] = texture;
            return texture;
        }

        private readonly Dictionary<string, Animation> animations = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AnimSet> animSets = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Loads &lt;name&gt;.anm + &lt;name&gt;.ang. Returns null when there is no .anm.</summary>
        public Animation GetAnimation(string name, IList<RkvArchive> packs = null)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (animations.TryGetValue(name, out var animation)) return animation;
            var anm = Game.Read(name + ".anm", packs);
            if (anm != null)
            {
                try
                {
                    animation = Animation.Load(anm, Game.Read(name + ".ang", packs), name);
                    foreach (var warning in animation.Warnings) Warnings.Add($"{name}.anm: {warning}");
                }
                catch (Exception exception) { Warnings.Add($"{name}.anm: {exception.Message}"); animation = null; }
            }
            animations[name] = animation;
            return animation;
        }

        /// <summary>Clip list from &lt;name&gt;.bbi, or one clip covering the whole timeline when there is none.</summary>
        public AnimSet GetAnimSet(string name, Animation anim, IList<RkvArchive> packs = null)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (animSets.TryGetValue(name, out var clips)) return clips;
            var data = Game.Read(name + ".bbi", packs);
            if (data != null)
            {
                try { clips = AnimSet.Parse(data); }
                catch (Exception exception) { Warnings.Add($"{name}.bbi: {exception.Message}"); clips = null; }
            }
            if ((clips == null || clips.Clips.Count == 0) && anim != null) clips = AnimSet.WholeTimeline(anim);
            animSets[name] = clips;
            return clips;
        }

        public Collision GetCollision(string fileName, IList<RkvArchive> packs)
        {
            var data = Game.Read(fileName, packs);
            if (data == null) return null;
            try { return Collision.Load(data, Path.GetFileNameWithoutExtension(fileName)); }
            catch (Exception exception) { Warnings.Add($"{fileName}: {exception.Message}"); return null; }
        }
    }

    public static class LevelLoader
    {
        public static List<LevelDef> LoadLevelList(GameData game)
        {
            var bniFile = game.ReadBni("levels.ini.bni");
            return bniFile == null ? new List<LevelDef>() : LevelsIni.Parse(bniFile);
        }

        public static LevelScene Load(GameData game, LevelDef level, ModelDb modelDb, AssetCache cache = null,
                                      Action<string> progress = null, ISet<string> onlyChunks = null)
        {
            cache ??= new AssetCache(game);
            var scene = new LevelScene { Def = level, Assets = cache };
            var envPack = game.ChunkPack(level.EnvChunk);

            foreach (var chunkDef in level.Chunks)
            {
                if (onlyChunks != null && !chunkDef.IsEnvironment && !onlyChunks.Contains(chunkDef.Name)) continue;
                progress?.Invoke("Loading " + chunkDef.Name);
                var chunk = new SceneChunk { Def = chunkDef, Pack = game.ChunkPack(chunkDef.Name) };
                if (chunk.Pack == null) scene.Warnings.Add($"{chunkDef.Name}: chunk pack (.BPK) not found");
                var packs = new List<RkvArchive> { chunk.Pack, envPack }.Where(pack => pack != null).Distinct().ToList();

                foreach (var overlay in chunkDef.Overlays)
                {
                    var loadedModel = cache.GetModel(overlay.Name, packs);
                    if (loadedModel == null) scene.Warnings.Add($"{chunkDef.Name}: overlay {overlay.Name} not found");
                    chunk.Overlays.Add((overlay, loadedModel));
                }
                foreach (var ground in chunkDef.Grounds)
                {
                    var collision = cache.GetCollision(ground + "_all.cgr", packs) ?? cache.GetCollision(ground + ".cgr", packs);
                    if (collision == null) scene.Warnings.Add($"{chunkDef.Name}: ground collision {ground} not found");
                    else chunk.Grounds.Add((ground, collision));
                }
                if (chunkDef.EnvDome != null) chunk.EnvDome = cache.GetModel(chunkDef.EnvDome, packs);

                chunk.Layout = game.ReadBni(chunkDef.Name + ".lc3.bni", packs);
                if (chunk.Layout != null)
                {
                    int index = 0;
                    foreach (var section in chunk.Layout.Sections)
                    {
                        var levelObject = LevelObject.FromSection(section, chunkDef.Name, index++, modelDb);
                        chunk.Objects.Add(levelObject);
                        if (modelDb != null && modelDb.IsHidden(levelObject.ClassName)) continue;
                        if (levelObject.Category != ObjectCategory.Prop && levelObject.Category != ObjectCategory.Critter && levelObject.Category != ObjectCategory.Checkpoint) continue;
                        var cands = modelDb?.ModelCandidates(levelObject.ClassName) ?? new List<string> { levelObject.ClassName };
                        foreach (var candidate in cands)
                        {
                            if (!game.Exists(candidate + ".mdl", packs)) continue;
                            var loadedModel = cache.GetModel(candidate, packs);
                            if (loadedModel != null) { chunk.ObjectModels[levelObject] = loadedModel; break; }
                        }
                    }
                }
                scene.Chunks.Add(chunk);
            }
            scene.Warnings.AddRange(cache.Warnings);
            return scene;
        }

        /// <summary>Standalone BPK: show every model in the pack side by side (no layout).</summary>
        public static LevelScene LoadPack(GameData game, string chunkName, AssetCache cache = null)
        {
            cache ??= new AssetCache(game);
            var pack = game.ChunkPack(chunkName);
            var level = new LevelDef { Name = chunkName };
            var chunkDef = new ChunkDef { Name = chunkName };
            level.Chunks.Add(chunkDef);
            var scene = new LevelScene { Def = level, Assets = cache };
            var chunk = new SceneChunk { Def = chunkDef, Pack = pack };
            if (pack != null)
            {
                var packs = new List<RkvArchive> { pack };
                foreach (var entry in pack.Entries.Where(candidate => candidate.Extension == ".mdl"))
                {
                    string name = Path.GetFileNameWithoutExtension(entry.Name);
                    var loadedModel = cache.GetModel(name, packs);
                    if (loadedModel != null) { chunkDef.Overlays.Add(new OverlayRef { Name = name }); chunk.Overlays.Add((chunkDef.Overlays[^1], loadedModel)); }
                }
            }
            scene.Chunks.Add(chunk);
            scene.Warnings.AddRange(cache.Warnings);
            return scene;
        }
    }
}
