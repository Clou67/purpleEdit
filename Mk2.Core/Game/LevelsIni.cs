using Mk2.Formats;

namespace Mk2.Game
{
    public sealed class OverlayRef
    {
        public string Name;
        public string Flag;     // optional extra value (e.g. "Overlay A04_l_08 1")
        public override string ToString() => Flag == null ? Name : $"{Name} ({Flag})";
    }

    public sealed class ChunkDef
    {
        public string Name;
        public List<OverlayRef> Overlays = new();
        public List<string> Grounds = new();
        public string EnvDome;
        public BniNode Node;
        public bool IsEnvironment;
        public override string ToString() => Name;
    }

    public sealed class LevelDef
    {
        public string Name;
        public string StartChunk;
        public string EnvChunk;
        public float[] FogColor;
        public float FogNear, FogFar;
        public List<ChunkDef> Chunks = new();
        public BniNode Node;
        public override string ToString() => Name;

        public ChunkDef Env => Chunks.FirstOrDefault(c => c.IsEnvironment);
    }

    /// <summary>levels.ini.bni: levels -> chunks -> overlays/grounds.</summary>
    public static class LevelsIni
    {
        public static List<LevelDef> Parse(BniFile f)
        {
            var levels = new List<LevelDef>();
            foreach (var n in f.Roots)
            {
                if (!string.Equals(n.Key, "level", StringComparison.OrdinalIgnoreCase)) continue;
                var lv = new LevelDef { Name = n.Value, Node = n };
                lv.StartChunk = n.Get("startChunk");
                lv.EnvChunk = n.Get("envChunk");
                var fp = n.Child("fogPlanes");
                if (fp != null && fp.Values.Count >= 2) { lv.FogNear = Bni.ParseFloat(fp.Values[0]); lv.FogFar = Bni.ParseFloat(fp.Values[1]); }
                var fc = n.Child("fogCol");
                if (fc != null && fc.Values.Count >= 3) lv.FogColor = fc.Values.Take(3).Select(v => Bni.ParseFloat(v)).ToArray();
                foreach (var c in n.ChildrenNamed("chunk"))
                {
                    var ch = new ChunkDef { Name = c.Value, Node = c };
                    ch.IsEnvironment = string.Equals(ch.Name, lv.EnvChunk, StringComparison.OrdinalIgnoreCase);
                    foreach (var k in c.Children)
                    {
                        switch (k.Key?.ToLowerInvariant())
                        {
                            case "overlay":
                                if (k.Value != null) ch.Overlays.Add(new OverlayRef { Name = k.Value, Flag = k.Values.Count > 1 ? k.Values[1] : null });
                                break;
                            case "ground":
                                if (k.Value != null) ch.Grounds.Add(k.Value);
                                break;
                            case "envdome":
                                ch.EnvDome = k.Value;
                                break;
                        }
                    }
                    lv.Chunks.Add(ch);
                }
                // Environment chunk first so shared assets are available to the others.
                lv.Chunks.Sort((a, b) => (b.IsEnvironment ? 1 : 0) - (a.IsEnvironment ? 1 : 0));
                levels.Add(lv);
            }
            return levels;
        }
    }
}
