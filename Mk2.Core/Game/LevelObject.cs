using System.Numerics;
using Mk2.Formats;

namespace Mk2.Game
{
    public enum ObjectCategory { Prop, Trigger, Path, Volume, Spawn, Camera, Sound, Checkpoint, Logic, Critter }

    public enum ShapeKind { Box, Sphere, Polyline, Polygon, CameraLine }

    public sealed class ObjectShape
    {
        public ShapeKind Kind;
        public string Label;
        public Vector3 Center;
        public Vector3 Rotation;      // radians, game PYR convention
        public Vector3 Size;          // full extents for boxes
        public float Radius;
        public List<Vector3> Points = new();
    }

    /// <summary>One placed entity from a ".lc3.bni" layout file.</summary>
    public sealed class LevelObject
    {
        public string ClassName;
        public string ChunkName;
        public int Index;
        public BniSection Section;

        public Vector3 Position;
        public Vector3 Rotation;          // radians (pitch, yaw, roll)
        public Vector3 Scale = Vector3.One;
        public Vector3? Color;
        public int Id;
        public string IdName;
        public string Zone;
        public ObjectCategory Category;
        public List<ObjectShape> Shapes = new();

        /// <summary>Critter swarms store the swarm volume in "scale" rather than a model scale.</summary>
        public bool ScaleIsVolume;

        public string DisplayName => IdName != null && IdName != "none" ? $"{ClassName} [{IdName}]" : ClassName;

        public override string ToString() => DisplayName;

        public IEnumerable<string> ScriptLines()
        {
            foreach (var f in Section.Fields)
            {
                foreach (var c in f.Children)
                    if (string.Equals(c.Key, "ScriptCommand", StringComparison.OrdinalIgnoreCase) && c.Values.Count > 0)
                        yield return $"{f.Key}: {c.JoinedValues}";
            }
        }

        /// <summary>
        /// World matrix (System.Numerics row-vector convention, v' = v * M), matching
        /// Matrix::SetRotationPYR in the game: M = Pitch(x) * Yaw(y) * Roll(z) where each
        /// axis matrix is the transpose of System.Numerics' CreateRotationX/Y/Z.
        /// </summary>
        public static Matrix4x4 Transform(Vector3 pos, Vector3 rot, Vector3 scale)
        {
            return Matrix4x4.CreateScale(scale) * RotationPYR(rot) * Matrix4x4.CreateTranslation(pos);
        }

        public static Matrix4x4 RotationPYR(Vector3 rot) =>
            Matrix4x4.CreateRotationX(-rot.X) * Matrix4x4.CreateRotationY(-rot.Y) * Matrix4x4.CreateRotationZ(-rot.Z);

        public Matrix4x4 ModelMatrix => Transform(Position, Rotation, ScaleIsVolume ? Vector3.One : Scale);

        public static LevelObject FromSection(BniSection s, string chunk, int index, ModelDb db)
        {
            var o = new LevelObject { ClassName = s.Name, ChunkName = chunk, Index = index, Section = s };
            o.Position = Vec(s.Field("pos")) ?? Vector3.Zero;
            o.Rotation = Vec(s.Field("rot")) ?? Vector3.Zero;
            o.Scale = Vec(s.Field("scale")) ?? Vector3.One;
            o.Color = Vec(s.Field("color"));
            var id = s.Field("ID");
            if (id != null)
            {
                o.Id = Bni.ParseInt(id.Value);
                o.IdName = id.Values.Count > 1 ? id.Values[1] : null;
            }
            o.Zone = s.Get("zone");
            o.Category = Categorize(s.Name, db);
            if (o.Category == ObjectCategory.Critter && (Math.Abs(o.Scale.X) > 20 || Math.Abs(o.Scale.Y) > 20 || Math.Abs(o.Scale.Z) > 20))
            {
                o.ScaleIsVolume = true;
                o.Shapes.Add(new ObjectShape { Kind = ShapeKind.Box, Label = "swarm volume", Center = o.Position, Rotation = o.Rotation, Size = o.Scale });
            }

            foreach (var f in s.Fields) CollectShapes(o, f);
            return o;
        }

        private static void CollectShapes(LevelObject o, BniNode f)
        {
            string k = f.Key?.ToLowerInvariant();
            switch (k)
            {
                case "box":
                {
                    var c = Vec(f.Child("pos")) ?? o.Position;
                    o.Shapes.Add(new ObjectShape
                    {
                        Kind = ShapeKind.Box, Label = "box", Center = c,
                        Rotation = Vec(f.Child("rot")) ?? Vector3.Zero,
                        Size = Vec(f.Child("scale")) ?? new Vector3(100),
                    });
                    break;
                }
                case "sphere":
                case "dummysphere":
                {
                    var c = Vec(f.Child("pos")) ?? o.Position;
                    float r = Bni.ParseFloat(f.Get("radius"), 100);
                    o.Shapes.Add(new ObjectShape { Kind = ShapeKind.Sphere, Label = f.Key, Center = c, Radius = r });
                    break;
                }
                case "waypoints":
                case "wallpoints":
                case "region":
                {
                    var pts = f.Children.Where(c => string.Equals(c.Key, "waypoint", StringComparison.OrdinalIgnoreCase))
                                        .Select(Vec).Where(v => v.HasValue).Select(v => v.Value).ToList();
                    if (pts.Count == 0) break;
                    bool closed = k == "region" || o.Category == ObjectCategory.Volume || o.Category == ObjectCategory.Trigger
                                  || o.ClassName.Contains("Region", StringComparison.OrdinalIgnoreCase);
                    var sh = new ObjectShape { Kind = closed ? ShapeKind.Polygon : ShapeKind.Polyline, Label = f.Key, Center = o.Position };
                    sh.Points.AddRange(pts);
                    o.Shapes.Add(sh);
                    break;
                }
                case "camera":
                {
                    var src = Vec(f.Child("cameraSource"));
                    var tgt = Vec(f.Child("cameraTarget"));
                    if (src.HasValue && tgt.HasValue)
                    {
                        var sh = new ObjectShape { Kind = ShapeKind.CameraLine, Label = "camera", Center = src.Value };
                        sh.Points.Add(src.Value); sh.Points.Add(tgt.Value);
                        o.Shapes.Add(sh);
                    }
                    break;
                }
                case "spawnpos":
                {
                    var p = Vec(f);
                    if (p.HasValue) o.Shapes.Add(new ObjectShape { Kind = ShapeKind.Sphere, Label = "spawn", Center = p.Value, Radius = 60 });
                    break;
                }
            }
            if (o.ClassName.Equals("CollideBox", StringComparison.OrdinalIgnoreCase) || o.ClassName.Equals("CameraCollideProp", StringComparison.OrdinalIgnoreCase))
            {
                if (k == "scale")
                    o.Shapes.Add(new ObjectShape { Kind = ShapeKind.Box, Label = "collide box (100u * scale, unverified)", Center = o.Position, Rotation = o.Rotation, Size = o.Scale * 100f });
            }
        }

        public static ObjectCategory Categorize(string cls, ModelDb db)
        {
            bool Has(string s) => cls.Contains(s, StringComparison.OrdinalIgnoreCase);
            if (Has("Trigger")) return ObjectCategory.Trigger;
            if (Has("Checkpoint")) return ObjectCategory.Checkpoint;
            if (Has("Spline") || Has("Path") || Has("GraphNode")) return ObjectCategory.Path;
            if (Has("Region") || Has("Volume") || Has("Connector") || Has("CollideBox") || Has("CameraCollide")) return ObjectCategory.Volume;
            if (Has("Spawn")) return ObjectCategory.Spawn;
            if (Has("Camera")) return ObjectCategory.Camera;
            if (Has("Sound")) return ObjectCategory.Sound;
            if (Has("Script") || Has("Counter") || Has("Hint") || Has("Lesson") || Has("CutScene") || Has("Emitter") || Has("PropPool") || Has("ChunkProp"))
                return ObjectCategory.Logic;
            if (db != null)
            {
                foreach (var a in db.AliasChain(cls))
                    if (a.Contains("Critter", StringComparison.OrdinalIgnoreCase) || a.Contains("Flicker", StringComparison.OrdinalIgnoreCase)
                        || a.Contains("Bird", StringComparison.OrdinalIgnoreCase) || a.Contains("Swarm", StringComparison.OrdinalIgnoreCase))
                        return ObjectCategory.Critter;
            }
            if (cls.StartsWith("C0", StringComparison.OrdinalIgnoreCase)) return ObjectCategory.Critter;
            return ObjectCategory.Prop;
        }

        private static Vector3? Vec(BniNode n)
        {
            if (n == null || n.Values.Count < 3) return null;
            return new Vector3(Bni.ParseFloat(n.Values[0]), Bni.ParseFloat(n.Values[1]), Bni.ParseFloat(n.Values[2]));
        }
    }
}
