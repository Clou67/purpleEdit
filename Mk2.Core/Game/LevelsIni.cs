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

        public ChunkDef Env => Chunks.FirstOrDefault(chunkDef => chunkDef.IsEnvironment);
    }

    /// <summary>levels.ini.bni: levels -> chunks -> overlays/grounds.</summary>
    public static class LevelsIni
    {
        public static List<LevelDef> Parse(BniFile bniFile)
        {
            var levels = new List<LevelDef>();
            foreach (var node in bniFile.Roots)
            {
                if (!string.Equals(node.Key, "level", StringComparison.OrdinalIgnoreCase)) continue;
                var level = new LevelDef { Name = node.Value, Node = node };
                level.StartChunk = node.Get("startChunk");
                level.EnvChunk = node.Get("envChunk");
                var fogPlanes = node.Child("fogPlanes");
                if (fogPlanes != null && fogPlanes.Values.Count >= 2) { level.FogNear = Bni.ParseFloat(fogPlanes.Values[0]); level.FogFar = Bni.ParseFloat(fogPlanes.Values[1]); }
                var fogColor = node.Child("fogCol");
                if (fogColor != null && fogColor.Values.Count >= 3) level.FogColor = fogColor.Values.Take(3).Select(value => Bni.ParseFloat(value)).ToArray();
                foreach (var chunkNode in node.ChildrenNamed("chunk"))
                {
                    var chunk = new ChunkDef { Name = chunkNode.Value, Node = chunkNode };
                    chunk.IsEnvironment = string.Equals(chunk.Name, level.EnvChunk, StringComparison.OrdinalIgnoreCase);
                    foreach (var child in chunkNode.Children)
                    {
                        switch (child.Key?.ToLowerInvariant())
                        {
                            case "overlay":
                                if (child.Value != null) chunk.Overlays.Add(new OverlayRef { Name = child.Value, Flag = child.Values.Count > 1 ? child.Values[1] : null });
                                break;
                            case "ground":
                                if (child.Value != null) chunk.Grounds.Add(child.Value);
                                break;
                            case "envdome":
                                chunk.EnvDome = child.Value;
                                break;
                        }
                    }
                    level.Chunks.Add(chunk);
                }
                // Environment chunk first so shared assets are available to the others.
                level.Chunks.Sort((first, second) => (second.IsEnvironment ? 1 : 0) - (first.IsEnvironment ? 1 : 0));
                levels.Add(level);
            }
            return levels;
        }
    }
}
