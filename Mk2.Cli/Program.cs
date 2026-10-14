using System.Diagnostics;
using System.Numerics;
using Mk2.Export;
using Mk2.Formats;
using Mk2.Game;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "levels": return Levels(args);
                case "bni": return DumpBni(args);
                case "extract": return Extract(args);
                case "tex": return Tex(args);
                case "model": return ExportModel(args);
                case "level": return ExportLevel(args);
                case "objects": return Objects(args);
                case "validate": return Validate(args);
                case "cgr": return Cgr(args);
                case "anim": return Anim(args);
                case "skin": return Skin(args);
                default: Usage(); return 1;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Error: " + exception);
            return 2;
        }
    }

    static void Usage()
    {
        Console.WriteLine(@"mk2cli - Krome MK2 (Legend of Spyro) tools

  mk2cli levels   <gameRoot>                         list levels and their chunks
  mk2cli objects  <gameRoot> <level> [chunk]         list placed objects (lc3)
  mk2cli level    <gameRoot> <level> <out.glb> [--chunks a,b] [--collision] [--no-objects]
  mk2cli model    <gameRoot> <modelName> <out.glb> [--collision]
  mk2cli tex      <file.tex|gameRoot name> <out.png>
  mk2cli bni      <file.bni> [out.txt]               decompile a binary ini to text
  mk2cli cgr      <file.cgr>                         collision summary
  mk2cli anim     <gameRoot> <name> [--keys]         skeleton, clips and matched model of <name>.anm
  mk2cli skin     <gameRoot> <model> [anim] [clip|frame]  bone bindings and vertices that move furthest in a pose
  mk2cli extract  <archive.rkv|.bpk> <outDir> [--group]
  mk2cli validate <gameRoot>                         parse every asset and report failures

gameRoot = any folder containing the extracted files and/or .BPK/.RKV archives.");
    }

    static GameData Open(string root)
    {
        var stopwatch = Stopwatch.StartNew();
        var game = new GameData(root);
        foreach (var line in game.Log) Console.Error.WriteLine("  " + line);
        Console.Error.WriteLine($"  (indexed in {stopwatch.ElapsedMilliseconds} ms)");
        return game;
    }

    static int Levels(string[] arguments)
    {
        var game = Open(arguments[1]);
        foreach (var level in LevelLoader.LoadLevelList(game))
        {
            Console.WriteLine($"{level.Name}  (start {level.StartChunk}, env {level.EnvChunk})");
            foreach (var chunkDef in level.Chunks)
            {
                string pack = game.ChunkPack(chunkDef.Name) != null ? "" : "  [no BPK]";
                Console.WriteLine($"   {chunkDef.Name,-22} overlays:{chunkDef.Overlays.Count,3} grounds:{chunkDef.Grounds.Count,3} {(chunkDef.EnvDome != null ? "dome:" + chunkDef.EnvDome : "")}{pack}");
            }
        }
        return 0;
    }

    static LevelDef FindLevel(GameData game, string name)
    {
        var level = LevelLoader.LoadLevelList(game).FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (level == null) throw new ArgumentException("Level not found: " + name);
        return level;
    }

    static ModelDb Db(GameData game) => new ModelDb(game.ReadBni("global.model.bni"));

    static int Objects(string[] arguments)
    {
        var game = Open(arguments[1]);
        var level = FindLevel(game, arguments[2]);
        var only = arguments.Length > 3 ? new HashSet<string>(new[] { arguments[3] }, StringComparer.OrdinalIgnoreCase) : null;
        var scene = LevelLoader.Load(game, level, Db(game), onlyChunks: only);
        foreach (var chunk in scene.Chunks)
        {
            if (only != null && !only.Contains(chunk.Def.Name)) continue;
            Console.WriteLine($"== {chunk.Def.Name}: {chunk.Objects.Count} objects");
            foreach (var levelObject in chunk.Objects)
            {
                chunk.ObjectModels.TryGetValue(levelObject, out var loadedModel);
                Console.WriteLine($"  {levelObject.Index,4} {levelObject.Category,-10} {levelObject.DisplayName,-45} pos({levelObject.Position.X:0},{levelObject.Position.Y:0},{levelObject.Position.Z:0}) model={(loadedModel?.Name ?? "-")} shapes={levelObject.Shapes.Count}");
            }
        }
        foreach (var warning in scene.Warnings.Distinct()) Console.Error.WriteLine("warn: " + warning);
        return 0;
    }

    static int ExportLevel(string[] arguments)
    {
        var game = Open(arguments[1]);
        var level = FindLevel(game, arguments[2]);
        HashSet<string> only = null;
        int chunksArgument = Array.IndexOf(arguments, "--chunks");
        if (chunksArgument > 0) only = new HashSet<string>(arguments[chunksArgument + 1].Split(','), StringComparer.OrdinalIgnoreCase);
        var stopwatch = Stopwatch.StartNew();
        var scene = LevelLoader.Load(game, level, Db(game), onlyChunks: only, progress: line => Console.Error.WriteLine("  " + line));
        Console.Error.WriteLine($"  loaded in {stopwatch.ElapsedMilliseconds} ms");
        var exporter = new GlbExporter(scene.Assets) { IncludeGroundCollision = arguments.Contains("--collision") };
        exporter.AddScene(scene, objects: !arguments.Contains("--no-objects"));
        exporter.Save(arguments[3]);
        int triangles = scene.Chunks.Sum(chunk => chunk.Overlays.Where(overlay => overlay.model != null).Sum(overlay => overlay.model.Model.TriangleCount));
        Console.WriteLine($"Wrote {arguments[3]}: {scene.Chunks.Count} chunks, {triangles} overlay triangles, {scene.AllObjects.Count()} objects ({scene.Chunks.Sum(chunk => chunk.ObjectModels.Count)} with models)");
        foreach (var warning in scene.Warnings.Distinct()) Console.Error.WriteLine("warn: " + warning);
        return 0;
    }

    static int ExportModel(string[] arguments)
    {
        var game = Open(arguments[1]);
        var cache = new AssetCache(game);
        var loadedModel = cache.GetModel(arguments[2], null) ?? throw new ArgumentException("Model not found: " + arguments[2]);
        var exporter = new GlbExporter(cache) { IncludeCollisionComponents = arguments.Contains("--collision") };
        exporter.AddModel(loadedModel, Matrix4x4.Identity, loadedModel.Name);
        exporter.Save(arguments[3]);
        Console.WriteLine($"Wrote {arguments[3]}: {loadedModel.Model.VertexCount} verts, {loadedModel.Model.TriangleCount} tris, {loadedModel.Model.MaterialNames.Count} materials, comps: {string.Join(", ", loadedModel.Model.Components.Select(component => component.Name))}");
        foreach (var material in loadedModel.Materials)
            Console.WriteLine($"   material {material.Name} -> tex {material.TextureName} {(cache.GetTexture(material.TextureName) == null ? "(missing)" : "")} flags=0x{material.Flags:X8} blend={material.Blend}");
        return 0;
    }

    static int Tex(string[] arguments)
    {
        byte[] data;
        string name;
        if (File.Exists(arguments[1])) { data = File.ReadAllBytes(arguments[1]); name = Path.GetFileNameWithoutExtension(arguments[1]); }
        else { var game = Open(arguments[1]); name = arguments[2]; data = game.Read(name + ".tex"); arguments = arguments.Skip(1).ToArray(); }
        var texture = Texture.Decode(data, name);
        Png.Save(arguments[2], texture.Rgba, texture.Width, texture.Height);
        Console.WriteLine($"{name}: {texture.Width}x{texture.Height} fmt 0x{texture.Format:X2} alpha={texture.HasAlpha}");
        return 0;
    }

    static int DumpBni(string[] arguments)
    {
        var bniFile = BniFile.Load(arguments[1]);
        var text = bniFile.ToText();
        if (arguments.Length > 2) File.WriteAllText(arguments[2], text); else Console.Write(text);
        return 0;
    }

    static int Cgr(string[] arguments)
    {
        var collision = Collision.Load(File.ReadAllBytes(arguments[1]), Path.GetFileName(arguments[1]));
        Console.WriteLine($"{collision.Name}: grid {collision.CellsX}x{collision.CellsY}x{collision.CellsZ} cell {collision.CellSize} bounds {collision.BoundsMin}..{collision.BoundsMax}");
        Console.WriteLine($"  {collision.Vertices.Count} verts, {collision.Triangles.Count} unique tris, materials: {string.Join(", ", collision.Materials)}");
        foreach (var group in collision.Triangles.GroupBy(triangle => triangle.Flags).OrderBy(group => group.Key))
            Console.WriteLine($"  flags 0x{group.Key:X4} ({collision.MaterialName(group.First())}): {group.Count()}");
        return 0;
    }

    static int Extract(string[] arguments)
    {
        var archive = RkvArchive.Open(arguments[1]);
        archive.ExtractAll(arguments[2], arguments.Contains("--group"));
        Console.WriteLine($"Extracted {archive.Entries.Count} files");
        return 0;
    }

    static int Skin(string[] arguments)
    {
        var game = Open(arguments[1]);
        string modelName = arguments[2];
        string animationName = arguments.Length > 3 ? arguments[3] : modelName;
        var cache = new AssetCache(game);
        var loadedModel = cache.GetModel(modelName, null);
        if (loadedModel == null) { Console.WriteLine($"{modelName}.mdl not found or not readable"); return 1; }
        var animation = cache.GetAnimation(animationName);
        if (animation == null) { Console.WriteLine($"{animationName}.anm not found or not readable"); return 1; }
        var model = loadedModel.Model;
        Console.WriteLine($"{modelName}: {model.AnimNodePositions.Count} anim nodes, {model.AnimNodeLists.Count} node lists; {animationName}: {animation.Bones.Count} bones");
        var warning = AnimationLookup.CompatibilityWarning(model, animation);
        if (warning != null) Console.WriteLine("  warning: " + warning);

        float frame = 0;
        string poseName = "frame 0";
        if (arguments.Length > 4)
        {
            var clip = cache.GetAnimSet(animationName, animation)?.Clips.FirstOrDefault(candidate => candidate.Name.Equals(arguments[4], StringComparison.OrdinalIgnoreCase));
            if (clip != null) { frame = clip.Start; poseName = $"clip {clip.Name} (frame {clip.Start})"; }
            else if (float.TryParse(arguments[4], out float parsed)) { frame = parsed; poseName = $"frame {parsed}"; }
        }

        string BoneName(int bone) => bone >= 0 && bone < animation.Bones.Count ? $"{bone} {animation.Bones[bone].Name}" : $"{bone} (none)";

        Console.WriteLine();
        Console.WriteLine("components:");
        foreach (var component in model.Components)
        {
            int resolved = Skinning.ComponentBone(model, component, animation);
            var lists = component.Meshes.Select(mesh => mesh.AnimNodeList == 0xFFFF ? "-" : $"{mesh.AnimNodeList}[{string.Join(",", model.NodeListBones(mesh))}]").Distinct();
            int skinnedStrips = component.Meshes.Sum(mesh => mesh.Strips.Count(strip => strip.Skinned));
            int strips = component.Meshes.Sum(mesh => mesh.Strips.Count);
            Console.WriteLine($"  {component.Name,-28} extra '{component.Extra}'  rigid {component.RigidBone,5}  follows {BoneName(resolved)}  skinned strips {skinnedStrips}/{strips}  node lists {string.Join(" ", lists)}");
        }

        var globals = new Matrix4x4[animation.Bones.Count];
        var skin = new Matrix4x4[animation.Bones.Count];
        animation.ComputePose(frame, globals);
        animation.SkinMatrices(globals, skin);

        var moved = new List<(float distance, string description)>();
        int unmappedVertices = 0;
        foreach (var component in model.Components)
        {
            int componentBone = Skinning.ComponentBone(model, component, animation);
            foreach (var mesh in component.Meshes)
            {
                var listedBones = model.NodeListBones(mesh);
                int fallback = componentBone >= 0 ? componentBone : listedBones.Count > 0 ? listedBones[0] : -1;
                foreach (var strip in mesh.Strips)
                    foreach (var vertex in strip.Vertices)
                    {
                        if (strip.Skinned && vertex.Bone0 == Model.NoBone) unmappedVertices++;
                        Skinning.PoseVertex(vertex.Position, vertex.Normal, strip.Skinned, vertex.Bone0, vertex.Bone1, vertex.Weight0, fallback, skin, out var posed, out _);
                        float distance = Vector3.Distance(posed, vertex.Position);
                        string binding = strip.Skinned
                            ? $"nodes {vertex.Node0}/{vertex.Node1} -> bones {BoneName(vertex.Bone0 == Model.NoBone ? -1 : vertex.Bone0)} / {BoneName(vertex.Bone1 == Model.NoBone ? -1 : vertex.Bone1)} w0 {vertex.Weight0:0.###}"
                            : $"unskinned -> {BoneName(fallback)}";
                        moved.Add((distance, $"{component.Name} list {mesh.AnimNodeList}  bind {vertex.Position}  moved {distance:0.0}  {binding}"));
                    }
            }
        }
        Console.WriteLine();
        Console.WriteLine($"skinned vertices whose node is outside their node list: {unmappedVertices}");
        Console.WriteLine($"vertices that move furthest at {poseName}:");
        foreach (var entry in moved.OrderByDescending(candidate => candidate.distance).Take(40)) Console.WriteLine("  " + entry.description);
        return 0;
    }

    static int Anim(string[] arguments)
    {
        var game = Open(arguments[1]);
        string name = arguments[2];
        var cache = new AssetCache(game);
        var anim = cache.GetAnimation(name);
        if (anim == null) { Console.WriteLine($"{name}.anm not found or not readable"); foreach (var warning in cache.Warnings) Console.WriteLine("  " + warning); return 1; }
        var set = cache.GetAnimSet(name, anim);
        string model = AnimationLookup.FindModelFor(game, name, anim);
        Console.WriteLine($"{name}: {anim.Bones.Count} bones, {anim.FrameCount} frames, {anim.TransformCount} ANG transforms, header byte5={anim.HeaderByte5}");
        Console.WriteLine($"  model: {model ?? "(no match)"}");
        if (model != null)
        {
            var loadedModel = cache.GetModel(model, null);
            var warn = AnimationLookup.CompatibilityWarning(loadedModel?.Model, anim);
            if (warn != null) Console.WriteLine("  warning: " + warn);
        }
        foreach (var warning in anim.Warnings) Console.WriteLine("  warning: " + warning);
        Console.WriteLine();
        foreach (var bone in anim.Bones)
        {
            Console.WriteLine($"  {bone.Index,3} {bone.Name,-32} parent {(bone.Parent < 0 ? "-" : bone.Parent.ToString()),3}  {bone.KeyType,-15} {bone.Keys.Count,5} keys{(bone.IsAnimated ? "" : "  (static)")}  bind {bone.BindPosition}");
            if (arguments.Contains("--keys"))
                foreach (var key in bone.Keys.Take(12))
                    Console.WriteLine($"        f{key.Frame,-6} pos {key.Position}  rot {key.Rotation}  scale {key.Scale}");
        }
        if (set != null)
        {
            Console.WriteLine();
            Console.WriteLine($"  clips ({set.Clips.Count}):");
            foreach (var clip in set.Clips)
            {
                Console.WriteLine($"    {clip.Start,6}-{clip.End,-6} {(clip.Loop ? "loop" : "once")}  {clip.Name}");
                foreach (var animationEvent in clip.Events) Console.WriteLine($"             @{animationEvent.Frame} {(animationEvent.Type == 1 ? "sound" : "type " + animationEvent.Type)} {animationEvent.Name}");
            }
        }
        return 0;
    }

    static int Validate(string[] arguments)
    {
        var game = Open(arguments[1]);
        int ok = 0, fail = 0;
        var fails = new List<string>();
        var stats = new Dictionary<string, int>();
        void Bump(string statistic) => stats[statistic] = stats.TryGetValue(statistic, out var count) ? count + 1 : 1;
        foreach (var archive in game.Archives.ToList())
        {
            var packs = new[] { archive };
            foreach (var entry in archive.Entries)
            {
                string extension = entry.Extension;
                try
                {
                    switch (extension)
                    {
                        case ".mdl":
                        {
                            string name = Path.GetFileNameWithoutExtension(entry.Name);
                            var mdg = game.Read(name + ".mdg", packs);
                            if (mdg == null) continue;
                            var model = Model.Load(archive.Read(entry), mdg, name);
                            if (model.Warnings.Count > 0) throw new Exception(string.Join("; ", model.Warnings));
                            if (model.DeclaredVertexCount != model.VertexCount) throw new Exception($"vertex count {model.VertexCount} != header {model.DeclaredVertexCount}");
                            if (model.DeclaredTriangleCount != model.TriangleCount) throw new Exception($"tri count {model.TriangleCount} != header {model.DeclaredTriangleCount}");
                            Bump(model.Meshes.Any(mesh => mesh.Skinned) ? "mdl(skinned)" : "mdl(static)");
                            break;
                        }
                        case ".anm":
                        {
                            string name = Path.GetFileNameWithoutExtension(entry.Name);
                            var anim = Animation.Load(archive.Read(entry), game.Read(name + ".ang", packs), name);
                            if (anim.Warnings.Count > 0) throw new Exception(string.Join("; ", anim.Warnings));
                            foreach (var bone in anim.Bones)
                                for (int keyIndex = 1; keyIndex < bone.Keys.Count; keyIndex++)
                                    if (bone.Keys[keyIndex].Frame < bone.Keys[keyIndex - 1].Frame) throw new Exception($"{bone.Name}: keys out of order");
                            foreach (var keyType in anim.Bones.Select(bone => bone.KeyType).Distinct()) Bump("anm keys " + keyType);
                            Bump("anm");
                            break;
                        }
                        case ".bbi": AnimSet.Parse(archive.Read(entry)); Bump("bbi"); break;
                        case ".tex": Bump("tex fmt " + Texture.Decode(archive.Read(entry)).Format.ToString("X2")); break;
                        case ".cgr": Collision.Load(archive.Read(entry)); Bump("cgr"); break;
                        case ".bni": BniFile.Parse(archive.Read(entry)); Bump("bni"); break;
                        case ".min": Material.Parse(archive.Read(entry)); Bump("min"); break;
                        default: continue;
                    }
                    ok++;
                }
                catch (Exception exception)
                {
                    fail++;
                    fails.Add($"{Path.GetFileName(archive.SourcePath)}/{entry.Name}: {exception.Message}");
                }
            }
        }
        foreach (var entry in stats.OrderBy(candidate => candidate.Key)) Console.WriteLine($"  {entry.Key,-16} {entry.Value}");
        Console.WriteLine($"OK {ok}, failed {fail}");
        foreach (var failure in fails.Take(50)) Console.WriteLine("  FAIL " + failure);
        return fail == 0 ? 0 : 3;
    }
}
