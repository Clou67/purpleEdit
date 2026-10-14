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
            foreach (var field in Section.Fields)
            {
                foreach (var command in field.Children)
                    if (string.Equals(command.Key, "ScriptCommand", StringComparison.OrdinalIgnoreCase) && command.Values.Count > 0)
                        yield return $"{field.Key}: {command.JoinedValues}";
            }
        }

        private static readonly HashSet<string> ScriptWords = new(StringComparer.OrdinalIgnoreCase)
        { "send", "delay", "wait", "if", "then", "else", "and", "or", "not", "true", "false", "on", "off" };

        private static readonly char[] StatementSeparators = { ';', '(', ')' };
        private static readonly char[] TokenSeparators = { ' ', ',', '\t', '=' };

        /// <summary>
        /// The command names used in this object's ScriptCommands, in order of first use: the word after "send",
        /// or a bare command that starts a statement ("send Autosave 12; delay 2 (KillHero)" → Autosave, KillHero).
        /// </summary>
        public List<string> ScriptActions()
        {
            var actions = new List<string>();
            if (Section == null) return actions;
            void Add(string token)
            {
                if (token.Length > 0 && char.IsLetter(token[0]) && !ScriptWords.Contains(token)
                    && !actions.Contains(token, StringComparer.OrdinalIgnoreCase)) actions.Add(token);
            }
            foreach (var field in Section.Fields)
                foreach (var command in field.Children)
                {
                    if (!string.Equals(command.Key, "ScriptCommand", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var statement in command.JoinedValues.Split(StatementSeparators, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var tokens = statement.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length == 0) continue;
                        Add(tokens[0]);
                        for (int index = 0; index + 1 < tokens.Length; index++)
                            if (tokens[index].Equals("send", StringComparison.OrdinalIgnoreCase)) Add(tokens[index + 1]);
                    }
                }
            return actions;
        }

        /// <summary>
        /// World matrix (System.Numerics row-vector convention, v' = v * M), matching
        /// Matrix::SetRotationPYR in the game: M = Pitch(x) * Yaw(y) * Roll(z) where each
        /// axis matrix is the transpose of System.Numerics' CreateRotationX/Y/Z.
        /// </summary>
        public static Matrix4x4 Transform(Vector3 pos, Vector3 rotation, Vector3 scale)
        {
            return Matrix4x4.CreateScale(scale) * RotationPYR(rotation) * Matrix4x4.CreateTranslation(pos);
        }

        public static Matrix4x4 RotationPYR(Vector3 rotation) =>
            Matrix4x4.CreateRotationX(-rotation.X) * Matrix4x4.CreateRotationY(-rotation.Y) * Matrix4x4.CreateRotationZ(-rotation.Z);

        public Matrix4x4 ModelMatrix => Transform(Position, Rotation, ScaleIsVolume ? Vector3.One : Scale);

        public static LevelObject FromSection(BniSection section, string chunk, int index, ModelDb modelDb)
        {
            var levelObject = new LevelObject { ClassName = section.Name, ChunkName = chunk, Index = index, Section = section };
            levelObject.Position = Vec(section.Field("pos")) ?? Vector3.Zero;
            levelObject.Rotation = Vec(section.Field("rot")) ?? Vector3.Zero;
            levelObject.Scale = Vec(section.Field("scale")) ?? Vector3.One;
            levelObject.Color = Vec(section.Field("color"));
            var id = section.Field("ID");
            if (id != null)
            {
                levelObject.Id = Bni.ParseInt(id.Value);
                levelObject.IdName = id.Values.Count > 1 ? id.Values[1] : null;
            }
            levelObject.Zone = section.Get("zone");
            levelObject.Category = Categorize(section.Name, modelDb);
            if (levelObject.Category == ObjectCategory.Critter && (Math.Abs(levelObject.Scale.X) > 20 || Math.Abs(levelObject.Scale.Y) > 20 || Math.Abs(levelObject.Scale.Z) > 20))
            {
                levelObject.ScaleIsVolume = true;
                levelObject.Shapes.Add(new ObjectShape { Kind = ShapeKind.Box, Label = "swarm volume", Center = levelObject.Position, Rotation = levelObject.Rotation, Size = levelObject.Scale });
            }

            foreach (var node in section.Fields) CollectShapes(levelObject, node);
            return levelObject;
        }

        private static void CollectShapes(LevelObject levelObject, BniNode node)
        {
            string key = node.Key?.ToLowerInvariant();
            switch (key)
            {
                case "box":
                {
                    var center = Vec(node.Child("pos")) ?? levelObject.Position;
                    levelObject.Shapes.Add(new ObjectShape
                    {
                        Kind = ShapeKind.Box, Label = "box", Center = center,
                        Rotation = Vec(node.Child("rot")) ?? Vector3.Zero,
                        Size = Vec(node.Child("scale")) ?? new Vector3(100),
                    });
                    break;
                }
                case "sphere":
                case "dummysphere":
                {
                    var center = Vec(node.Child("pos")) ?? levelObject.Position;
                    float radius = Bni.ParseFloat(node.Get("radius"), 100);
                    levelObject.Shapes.Add(new ObjectShape { Kind = ShapeKind.Sphere, Label = node.Key, Center = center, Radius = radius });
                    break;
                }
                case "waypoints":
                case "wallpoints":
                case "region":
                {
                    var points = node.Children.Where(child => string.Equals(child.Key, "waypoint", StringComparison.OrdinalIgnoreCase))
                                        .Select(Vec).Where(point => point.HasValue).Select(point => point.Value).ToList();
                    if (points.Count == 0) break;
                    bool closed = key == "region" || levelObject.Category == ObjectCategory.Volume || levelObject.Category == ObjectCategory.Trigger
                                  || levelObject.ClassName.Contains("Region", StringComparison.OrdinalIgnoreCase);
                    var shape = new ObjectShape { Kind = closed ? ShapeKind.Polygon : ShapeKind.Polyline, Label = node.Key, Center = levelObject.Position };
                    shape.Points.AddRange(points);
                    levelObject.Shapes.Add(shape);
                    break;
                }
                case "camera":
                {
                    var source = Vec(node.Child("cameraSource"));
                    var target = Vec(node.Child("cameraTarget"));
                    if (source.HasValue && target.HasValue)
                    {
                        var shape = new ObjectShape { Kind = ShapeKind.CameraLine, Label = "camera", Center = source.Value };
                        shape.Points.Add(source.Value); shape.Points.Add(target.Value);
                        levelObject.Shapes.Add(shape);
                    }
                    break;
                }
                case "spawnpos":
                {
                    var position = Vec(node);
                    if (position.HasValue) levelObject.Shapes.Add(new ObjectShape { Kind = ShapeKind.Sphere, Label = "spawn", Center = position.Value, Radius = 60 });
                    break;
                }
            }
            if (levelObject.ClassName.Equals("CollideBox", StringComparison.OrdinalIgnoreCase) || levelObject.ClassName.Equals("CameraCollideProp", StringComparison.OrdinalIgnoreCase))
            {
                if (key == "scale")
                    levelObject.Shapes.Add(new ObjectShape { Kind = ShapeKind.Box, Label = "collide box (100u * scale, unverified)", Center = levelObject.Position, Rotation = levelObject.Rotation, Size = levelObject.Scale * 100f });
            }
        }

        public static ObjectCategory Categorize(string className, ModelDb modelDb)
        {
            bool Has(string part) => className.Contains(part, StringComparison.OrdinalIgnoreCase);
            if (Has("Trigger")) return ObjectCategory.Trigger;
            if (Has("Checkpoint")) return ObjectCategory.Checkpoint;
            if (Has("Spline") || Has("Path") || Has("GraphNode")) return ObjectCategory.Path;
            if (Has("Region") || Has("Volume") || Has("Connector") || Has("CollideBox") || Has("CameraCollide")) return ObjectCategory.Volume;
            if (Has("Spawn")) return ObjectCategory.Spawn;
            if (Has("Camera")) return ObjectCategory.Camera;
            if (Has("Sound")) return ObjectCategory.Sound;
            if (Has("Script") || Has("Counter") || Has("Hint") || Has("Lesson") || Has("CutScene") || Has("Emitter") || Has("PropPool") || Has("ChunkProp"))
                return ObjectCategory.Logic;
            if (modelDb != null)
            {
                foreach (var alias in modelDb.AliasChain(className))
                    if (alias.Contains("Critter", StringComparison.OrdinalIgnoreCase) || alias.Contains("Flicker", StringComparison.OrdinalIgnoreCase)
                        || alias.Contains("Bird", StringComparison.OrdinalIgnoreCase) || alias.Contains("Swarm", StringComparison.OrdinalIgnoreCase))
                        return ObjectCategory.Critter;
            }
            if (className.StartsWith("C0", StringComparison.OrdinalIgnoreCase)) return ObjectCategory.Critter;
            return ObjectCategory.Prop;
        }

        private static Vector3? Vec(BniNode node)
        {
            if (node == null || node.Values.Count < 3) return null;
            return new Vector3(Bni.ParseFloat(node.Values[0]), Bni.ParseFloat(node.Values[1]), Bni.ParseFloat(node.Values[2]));
        }
    }
}
