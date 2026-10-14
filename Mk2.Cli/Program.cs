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
                default: Usage(); return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex);
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
  mk2cli extract  <archive.rkv|.bpk> <outDir> [--group]
  mk2cli validate <gameRoot>                         parse every asset and report failures

gameRoot = any folder containing the extracted files and/or .BPK/.RKV archives.");
    }

    static GameData Open(string root)
    {
        var sw = Stopwatch.StartNew();
        var g = new GameData(root);
        foreach (var l in g.Log) Console.Error.WriteLine("  " + l);
        Console.Error.WriteLine($"  (indexed in {sw.ElapsedMilliseconds} ms)");
        return g;
    }

    static int Levels(string[] a)
    {
        var g = Open(a[1]);
        foreach (var lv in LevelLoader.LoadLevelList(g))
        {
            Console.WriteLine($"{lv.Name}  (start {lv.StartChunk}, env {lv.EnvChunk})");
            foreach (var c in lv.Chunks)
            {
                string pack = g.ChunkPack(c.Name) != null ? "" : "  [no BPK]";
                Console.WriteLine($"   {c.Name,-22} overlays:{c.Overlays.Count,3} grounds:{c.Grounds.Count,3} {(c.EnvDome != null ? "dome:" + c.EnvDome : "")}{pack}");
            }
        }
        return 0;
    }

    static LevelDef FindLevel(GameData g, string name)
    {
        var lv = LevelLoader.LoadLevelList(g).FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        if (lv == null) throw new ArgumentException("Level not found: " + name);
        return lv;
    }

    static ModelDb Db(GameData g) => new ModelDb(g.ReadBni("global.model.bni"));

    static int Objects(string[] a)
    {
        var g = Open(a[1]);
        var lv = FindLevel(g, a[2]);
        var only = a.Length > 3 ? new HashSet<string>(new[] { a[3] }, StringComparer.OrdinalIgnoreCase) : null;
        var scene = LevelLoader.Load(g, lv, Db(g), onlyChunks: only);
        foreach (var ch in scene.Chunks)
        {
            if (only != null && !only.Contains(ch.Def.Name)) continue;
            Console.WriteLine($"== {ch.Def.Name}: {ch.Objects.Count} objects");
            foreach (var o in ch.Objects)
            {
                ch.ObjectModels.TryGetValue(o, out var m);
                Console.WriteLine($"  {o.Index,4} {o.Category,-10} {o.DisplayName,-45} pos({o.Position.X:0},{o.Position.Y:0},{o.Position.Z:0}) model={(m?.Name ?? "-")} shapes={o.Shapes.Count}");
            }
        }
        foreach (var w in scene.Warnings.Distinct()) Console.Error.WriteLine("warn: " + w);
        return 0;
    }

    static int ExportLevel(string[] a)
    {
        var g = Open(a[1]);
        var lv = FindLevel(g, a[2]);
        HashSet<string> only = null;
        int ci = Array.IndexOf(a, "--chunks");
        if (ci > 0) only = new HashSet<string>(a[ci + 1].Split(','), StringComparer.OrdinalIgnoreCase);
        var sw = Stopwatch.StartNew();
        var scene = LevelLoader.Load(g, lv, Db(g), onlyChunks: only, progress: s => Console.Error.WriteLine("  " + s));
        Console.Error.WriteLine($"  loaded in {sw.ElapsedMilliseconds} ms");
        var ex = new GlbExporter(scene.Assets) { IncludeGroundCollision = a.Contains("--collision") };
        ex.AddScene(scene, objects: !a.Contains("--no-objects"));
        ex.Save(a[3]);
        int tris = scene.Chunks.Sum(c => c.Overlays.Where(o => o.model != null).Sum(o => o.model.Model.TriangleCount));
        Console.WriteLine($"Wrote {a[3]}: {scene.Chunks.Count} chunks, {tris} overlay triangles, {scene.AllObjects.Count()} objects ({scene.Chunks.Sum(c => c.ObjectModels.Count)} with models)");
        foreach (var w in scene.Warnings.Distinct()) Console.Error.WriteLine("warn: " + w);
        return 0;
    }

    static int ExportModel(string[] a)
    {
        var g = Open(a[1]);
        var cache = new AssetCache(g);
        var m = cache.GetModel(a[2], null) ?? throw new ArgumentException("Model not found: " + a[2]);
        var ex = new GlbExporter(cache) { IncludeCollisionComponents = a.Contains("--collision") };
        ex.AddModel(m, Matrix4x4.Identity, m.Name);
        ex.Save(a[3]);
        Console.WriteLine($"Wrote {a[3]}: {m.Model.VertexCount} verts, {m.Model.TriangleCount} tris, {m.Model.MaterialNames.Count} materials, comps: {string.Join(", ", m.Model.Components.Select(c => c.Name))}");
        foreach (var mat in m.Materials)
            Console.WriteLine($"   material {mat.Name} -> tex {mat.TextureName} {(cache.GetTexture(mat.TextureName) == null ? "(missing)" : "")} flags=0x{mat.Flags:X8} blend={mat.Blend}");
        return 0;
    }

    static int Tex(string[] a)
    {
        byte[] d;
        string name;
        if (File.Exists(a[1])) { d = File.ReadAllBytes(a[1]); name = Path.GetFileNameWithoutExtension(a[1]); }
        else { var g = Open(a[1]); name = a[2]; d = g.Read(name + ".tex"); a = a.Skip(1).ToArray(); }
        var t = Texture.Decode(d, name);
        Png.Save(a[2], t.Rgba, t.Width, t.Height);
        Console.WriteLine($"{name}: {t.Width}x{t.Height} fmt 0x{t.Format:X2} alpha={t.HasAlpha}");
        return 0;
    }

    static int DumpBni(string[] a)
    {
        var f = BniFile.Load(a[1]);
        var txt = f.ToText();
        if (a.Length > 2) File.WriteAllText(a[2], txt); else Console.Write(txt);
        return 0;
    }

    static int Cgr(string[] a)
    {
        var c = Collision.Load(File.ReadAllBytes(a[1]), Path.GetFileName(a[1]));
        Console.WriteLine($"{c.Name}: grid {c.CellsX}x{c.CellsY}x{c.CellsZ} cell {c.CellSize} bounds {c.BoundsMin}..{c.BoundsMax}");
        Console.WriteLine($"  {c.Vertices.Count} verts, {c.Triangles.Count} unique tris, materials: {string.Join(", ", c.Materials)}");
        foreach (var grp in c.Triangles.GroupBy(t => t.Flags).OrderBy(x => x.Key))
            Console.WriteLine($"  flags 0x{grp.Key:X4} ({c.MaterialName(grp.First())}): {grp.Count()}");
        return 0;
    }

    static int Extract(string[] a)
    {
        var arc = RkvArchive.Open(a[1]);
        arc.ExtractAll(a[2], a.Contains("--group"));
        Console.WriteLine($"Extracted {arc.Entries.Count} files");
        return 0;
    }

    static int Anim(string[] a)
    {
        var g = Open(a[1]);
        string name = a[2];
        var cache = new AssetCache(g);
        var anim = cache.GetAnimation(name);
        if (anim == null) { Console.WriteLine($"{name}.anm not found or not readable"); foreach (var w in cache.Warnings) Console.WriteLine("  " + w); return 1; }
        var set = cache.GetAnimSet(name, anim);
        string model = AnimationLookup.FindModelFor(g, name, anim);
        Console.WriteLine($"{name}: {anim.Bones.Count} bones, {anim.FrameCount} frames, {anim.TransformCount} ANG transforms, header byte5={anim.HeaderByte5}");
        Console.WriteLine($"  model: {model ?? "(no match)"}");
        if (model != null)
        {
            var lm = cache.GetModel(model, null);
            var warn = AnimationLookup.CompatibilityWarning(lm?.Model, anim);
            if (warn != null) Console.WriteLine("  warning: " + warn);
        }
        foreach (var w in anim.Warnings) Console.WriteLine("  warning: " + w);
        Console.WriteLine();
        foreach (var b in anim.Bones)
        {
            Console.WriteLine($"  {b.Index,3} {b.Name,-32} parent {(b.Parent < 0 ? "-" : b.Parent.ToString()),3}  {b.KeyType,-15} {b.Keys.Count,5} keys{(b.IsAnimated ? "" : "  (static)")}  bind {b.BindPosition}");
            if (a.Contains("--keys"))
                foreach (var k in b.Keys.Take(12))
                    Console.WriteLine($"        f{k.Frame,-6} pos {k.Position}  rot {k.Rotation}  scale {k.Scale}");
        }
        if (set != null)
        {
            Console.WriteLine();
            Console.WriteLine($"  clips ({set.Clips.Count}):");
            foreach (var c in set.Clips)
            {
                Console.WriteLine($"    {c.Start,6}-{c.End,-6} {(c.Loop ? "loop" : "once")}  {c.Name}");
                foreach (var e in c.Events) Console.WriteLine($"             @{e.Frame} {(e.Type == 1 ? "sound" : "type " + e.Type)} {e.Name}");
            }
        }
        return 0;
    }

    static int Validate(string[] a)
    {
        var g = Open(a[1]);
        int ok = 0, fail = 0;
        var fails = new List<string>();
        var stats = new Dictionary<string, int>();
        void Bump(string k) => stats[k] = stats.TryGetValue(k, out var v) ? v + 1 : 1;
        foreach (var arc in g.Archives.ToList())
        {
            var packs = new[] { arc };
            foreach (var e in arc.Entries)
            {
                string ext = e.Extension;
                try
                {
                    switch (ext)
                    {
                        case ".mdl":
                        {
                            string n = Path.GetFileNameWithoutExtension(e.Name);
                            var mdg = g.Read(n + ".mdg", packs);
                            if (mdg == null) continue;
                            var m = Model.Load(arc.Read(e), mdg, n);
                            if (m.Warnings.Count > 0) throw new Exception(string.Join("; ", m.Warnings));
                            if (m.DeclaredVertexCount != m.VertexCount) throw new Exception($"vertex count {m.VertexCount} != header {m.DeclaredVertexCount}");
                            if (m.DeclaredTriangleCount != m.TriangleCount) throw new Exception($"tri count {m.TriangleCount} != header {m.DeclaredTriangleCount}");
                            Bump(m.Meshes.Any(x => x.Skinned) ? "mdl(skinned)" : "mdl(static)");
                            break;
                        }
                        case ".anm":
                        {
                            string n = Path.GetFileNameWithoutExtension(e.Name);
                            var anim = Animation.Load(arc.Read(e), g.Read(n + ".ang", packs), n);
                            if (anim.Warnings.Count > 0) throw new Exception(string.Join("; ", anim.Warnings));
                            foreach (var b in anim.Bones)
                                for (int k = 1; k < b.Keys.Count; k++)
                                    if (b.Keys[k].Frame < b.Keys[k - 1].Frame) throw new Exception($"{b.Name}: keys out of order");
                            foreach (var t in anim.Bones.Select(b => b.KeyType).Distinct()) Bump("anm keys " + t);
                            Bump("anm");
                            break;
                        }
                        case ".bbi": AnimSet.Parse(arc.Read(e)); Bump("bbi"); break;
                        case ".tex": Bump("tex fmt " + Texture.Decode(arc.Read(e)).Format.ToString("X2")); break;
                        case ".cgr": Collision.Load(arc.Read(e)); Bump("cgr"); break;
                        case ".bni": BniFile.Parse(arc.Read(e)); Bump("bni"); break;
                        case ".min": Material.Parse(arc.Read(e)); Bump("min"); break;
                        default: continue;
                    }
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    fails.Add($"{Path.GetFileName(arc.SourcePath)}/{e.Name}: {ex.Message}");
                }
            }
        }
        foreach (var kv in stats.OrderBy(k => k.Key)) Console.WriteLine($"  {kv.Key,-16} {kv.Value}");
        Console.WriteLine($"OK {ok}, failed {fail}");
        foreach (var f in fails.Take(50)) Console.WriteLine("  FAIL " + f);
        return fail == 0 ? 0 : 3;
    }
}
