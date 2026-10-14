using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mk2.Formats;
using Mk2.Game;
using Mk2.Viewer.Rendering;
using Vec3 = System.Numerics.Vector3;

namespace Mk2.Viewer
{
    public sealed class PropertySection
    {
        public string Heading;
        public string Body;
    }

    public sealed class PropertyLink
    {
        public LevelObject Target;
        public string Label;
        public string Via;
    }

    /// <summary>What the Properties frame shows for the current selection.</summary>
    public sealed class PropertiesView
    {
        public string Title = "";
        public readonly List<PropertySection> Sections = new List<PropertySection>();
        public readonly List<string> Scripts = new List<string>();
        public readonly List<PropertyLink> References = new List<PropertyLink>();
        public readonly List<PropertyLink> ReferencedBy = new List<PropertyLink>();
        public bool ShowsLinks;

        public static readonly PropertiesView Empty = new PropertiesView();

        private void Add(string heading, StringBuilder body)
        {
            string text = body.ToString().TrimEnd();
            if (text.Length > 0) Sections.Add(new PropertySection { Heading = heading, Body = text });
        }

        public static PropertiesView ForLevel(LevelScene scene, GameData game, SceneRenderer renderer)
        {
            var view = new PropertiesView();
            if (scene == null) return view;
            var level = scene.Def;
            view.Title = level.Name;

            var summary = new StringBuilder();
            if (level.StartChunk != null) summary.AppendLine($"start chunk   {level.StartChunk}");
            if (level.EnvChunk != null) summary.AppendLine($"env chunk     {level.EnvChunk}");
            if (level.FogFar > 0) summary.AppendLine($"fog planes    {level.FogNear} .. {level.FogFar}");
            if (level.FogColor != null) summary.AppendLine($"fog colour    {string.Join(" ", level.FogColor)}");
            int triangles = renderer.Items.Sum(item => item.Model.Parts.Sum(part => part.Triangles));
            summary.AppendLine($"objects       {scene.AllObjects.Count()}");
            summary.AppendLine($"draw items    {renderer.Items.Count}");
            summary.AppendLine($"triangles     {triangles:N0}");
            summary.AppendLine($"collision     {renderer.Collisions.Count} meshes");
            view.Add("Level", summary);

            if (level.Node != null)
            {
                var entry = new StringBuilder();
                AppendNode(entry, level.Node, 0, skipChildrenNamed: "chunk");
                view.Add("levels.ini entry", entry);
            }

            var chunks = new StringBuilder();
            foreach (var chunk in scene.Chunks)
            {
                var pack = chunk.Pack ?? game?.ChunkPack(chunk.Def.Name);
                chunks.AppendLine($"{chunk.Def.Name}{(chunk.Def.IsEnvironment ? " [env]" : "")}   pack {(pack == null ? "missing" : Path.GetFileName(pack.SourcePath))}");
                chunks.AppendLine($"  {chunk.Overlays.Count} overlays, {chunk.Grounds.Count} grounds, {chunk.Objects.Count} objects, {chunk.ObjectModels.Count} with models");
                if (chunk.Def.EnvDome != null) chunks.AppendLine($"  sky dome  {chunk.Def.EnvDome}");
                if (chunk.Objects.Count > 0)
                    chunks.AppendLine("  " + string.Join("  ", chunk.Objects.GroupBy(levelObject => levelObject.Category).OrderBy(group => group.Key).Select(group => $"{group.Key}:{group.Count()}")));
            }
            view.Add("Chunks", chunks);

            var warnings = scene.Warnings.Distinct().ToList();
            if (warnings.Count > 0)
            {
                var warningText = new StringBuilder();
                foreach (var warning in warnings.Take(100)) warningText.AppendLine(warning);
                view.Add($"Warnings ({warnings.Count})", warningText);
            }
            return view;
        }

        public static PropertiesView ForSelection(LevelObject levelObject, DrawItem item, LevelScene scene, ModelDb modelDb, AssetCache assets)
        {
            var view = new PropertiesView();
            if (levelObject != null)
            {
                view.Title = levelObject.DisplayName;
                var details = new StringBuilder();
                details.AppendLine($"chunk     {levelObject.ChunkName}   (entry #{levelObject.Index} in {levelObject.ChunkName}.lc3.bni)");
                details.AppendLine($"category  {levelObject.Category}");
                if (levelObject.Id != 0) details.AppendLine($"ID        {levelObject.Id}{(levelObject.IdName != null ? " " + levelObject.IdName : "")}");
                details.AppendLine($"position  {Format(levelObject.Position)}");
                details.AppendLine($"rotation  {Format(levelObject.Rotation)} rad  ({Format(levelObject.Rotation * (180f / MathF.PI))} deg)");
                details.AppendLine($"scale     {Format(levelObject.Scale)}{(levelObject.ScaleIsVolume ? "  (swarm volume)" : "")}");
                if (levelObject.Color.HasValue) details.AppendLine($"color     {Format(levelObject.Color.Value)}");
                if (levelObject.Zone != null) details.AppendLine($"zone      {levelObject.Zone}");
                if (modelDb != null)
                {
                    var chain = modelDb.AliasChain(levelObject.ClassName).ToList();
                    if (chain.Count > 1 || modelDb.Has(levelObject.ClassName)) details.AppendLine($"class     {string.Join(" → ", chain)}");
                    details.AppendLine($"models    {string.Join(", ", modelDb.ModelCandidates(levelObject.ClassName))}");
                }
                foreach (var shape in levelObject.Shapes)
                    details.AppendLine($"shape     {shape.Label}: {DescribeShape(shape)}");
                view.Add("Object", details);
            }
            else if (item != null)
            {
                view.Title = item.Kind == ItemKind.Overlay ? "Overlay " + item.Name : item.Name;
            }

            if (item != null) view.Add("Model " + item.Model.Source.Name, DescribeModel(item.Model.Source, assets));

            if (levelObject?.Section != null && levelObject.Section.Fields.Count > 0)
            {
                var entry = new StringBuilder();
                entry.AppendLine("name " + levelObject.ClassName);
                foreach (var field in levelObject.Section.Fields) AppendNode(entry, field, 1);
                view.Add("LC3 entry", entry);
                view.Scripts.AddRange(levelObject.ScriptLines());
            }

            if (levelObject?.Section != null && scene != null)
            {
                view.ShowsLinks = true;
                foreach (var (target, line) in FindOutgoing(levelObject, scene))
                    view.References.Add(new PropertyLink { Target = target, Label = $"{target.DisplayName}  ({target.ChunkName})", Via = line.ToString() });
                if (levelObject.Id != 0)
                    foreach (var (source, line) in FindIncoming(levelObject, scene))
                        view.ReferencedBy.Add(new PropertyLink { Target = source, Label = $"{source.DisplayName}  ({source.ChunkName})", Via = line.ToString() });
            }
            return view;
        }

        public static PropertiesView ForCollision(Collision collision)
        {
            var view = new PropertiesView { Title = "Collision " + collision.Name };
            var details = new StringBuilder();
            details.AppendLine($"grid       {collision.CellsX} x {collision.CellsY} x {collision.CellsZ}, cell {Format(collision.CellSize)}");
            details.AppendLine($"bounds     {Format(collision.BoundsMin)} .. {Format(collision.BoundsMax)}");
            details.AppendLine($"vertices   {collision.Vertices.Count}");
            details.AppendLine($"triangles  {collision.Triangles.Count}");
            details.AppendLine($"materials  {string.Join(", ", collision.Materials)}");
            view.Add("Collision", details);

            var flags = new StringBuilder();
            foreach (var group in collision.Triangles.GroupBy(triangle => triangle.Flags).OrderBy(group => group.Key))
                flags.AppendLine($"0x{group.Key:X4}  {collision.MaterialName(group.First()),-16} {group.Count()}");
            view.Add("Triangle flags", flags);
            return view;
        }

        public static PropertiesView ForBone(Animation animation, int boneIndex, AnimationState state)
        {
            var view = new PropertiesView();
            if (animation == null || boneIndex < 0 || boneIndex >= animation.Bones.Count) return view;
            var bone = animation.Bones[boneIndex];
            view.Title = $"Bone {boneIndex} {bone.Name}";

            var details = new StringBuilder();
            details.AppendLine($"parent      {(bone.Parent >= 0 ? $"{bone.Parent} {animation.Bones[bone.Parent].Name}" : "root")}");
            if (bone.Children.Count > 0)
                details.AppendLine($"children    {string.Join(", ", bone.Children.Select(child => $"{child} {animation.Bones[child].Name}"))}");
            details.AppendLine($"bind        {Format(bone.BindPosition)}");
            details.AppendLine($"local       {Format(bone.LocalOffset)}");
            details.AppendLine($"key type    {bone.KeyType}");
            if (bone.Keys.Count > 0) details.AppendLine($"keys        {bone.Keys.Count}  (frames {bone.Keys[0].Frame}–{bone.Keys[^1].Frame})");
            details.AppendLine($"animated    {(bone.IsAnimated ? "yes" : "no")}");
            if (state != null && state.HasPose) details.AppendLine($"posed       {Format(state.Globals[boneIndex].Translation)}");
            view.Add("Bone", details);

            var model = state?.Item?.Model.Source.Model;
            if (model != null)
            {
                int full = 0, blended = 0;
                foreach (var mesh in model.Meshes)
                    foreach (var strip in mesh.Strips)
                    {
                        if (!strip.Skinned) continue;
                        foreach (var vertex in strip.Vertices)
                        {
                            bool onFirst = vertex.Bone0 == boneIndex && vertex.Weight0 > 0.001f;
                            bool onSecond = vertex.Bone1 == boneIndex && vertex.Weight0 < 0.999f;
                            if (onFirst && (vertex.Bone1 == boneIndex || vertex.Weight0 >= 0.999f)) full++;
                            else if (onFirst || onSecond) blended++;
                        }
                    }
                var rigidParts = model.Components.Where(component => Skinning.ComponentBone(model, component, animation) == boneIndex).Select(component => component.Name).ToList();
                var skinning = new StringBuilder();
                skinning.AppendLine($"skinned     {full} vertices");
                skinning.AppendLine($"blended     {blended} vertices");
                if (rigidParts.Count > 0) skinning.AppendLine($"parts       {string.Join(", ", rigidParts)}");
                view.Add("Model " + state.Item.Model.Source.Name, skinning);
            }
            return view;
        }

        public static PropertiesView ForAnimation(string name, Animation animation, AnimSet clips, string modelName)
        {
            var view = new PropertiesView { Title = name };
            if (animation == null) return view;
            var details = new StringBuilder();
            details.AppendLine($"model       {modelName ?? "-"}");
            details.AppendLine($"bones       {animation.Bones.Count}  ({animation.Bones.Count(bone => bone.IsAnimated)} animated)");
            details.AppendLine($"frames      {animation.FrameCount}");
            details.AppendLine($"transforms  {animation.TransformCount}");
            details.AppendLine($"key types   {string.Join(", ", animation.Bones.GroupBy(bone => bone.KeyType).Select(group => $"{group.Key} ×{group.Count()}"))}");
            foreach (var warning in animation.Warnings) details.AppendLine("warning     " + warning);
            view.Add("Animation", details);

            if (clips != null && clips.Clips.Count > 0)
            {
                var clipText = new StringBuilder();
                foreach (var clip in clips.Clips)
                {
                    clipText.AppendLine($"{clip.Start,6}–{clip.End,-6} {(clip.Loop ? "loop" : "once")}  {clip.Name}");
                    foreach (var animationEvent in clip.Events)
                        clipText.AppendLine($"         @{animationEvent.Frame,-5} {(animationEvent.Type == 1 ? "sound" : "type " + animationEvent.Type)} {animationEvent.Name}");
                }
                view.Add($"Clips ({clips.Clips.Count})", clipText);
            }
            return view;
        }

        private static StringBuilder DescribeModel(LoadedModel loadedModel, AssetCache assets)
        {
            var model = loadedModel.Model;
            var text = new StringBuilder();
            text.AppendLine($"source      {loadedModel.Source}");
            text.AppendLine($"geometry    {model.VertexCount:N0} verts, {model.TriangleCount:N0} tris, {model.Meshes.Count} meshes, {model.AnimNodePositions.Count} anim nodes");
            text.AppendLine($"bounds      {Format(model.BoundsMin)} .. {Format(model.BoundsMax)}");
            text.AppendLine("components");
            foreach (var component in model.Components)
            {
                string kind = component.IsCollision ? "  [collision]" : component.IsFragment ? "  [fragment]" : "";
                string bone = component.RigidBone >= 0 ? $" bone={component.RigidBone}" : "";
                string skinned = component.Meshes.Any(mesh => mesh.Skinned) ? " skinned" : "";
                text.AppendLine($"  {component.Name}{kind}  meshes={component.Meshes.Count}{bone}{skinned}");
            }
            text.AppendLine("materials");
            foreach (var material in loadedModel.Materials)
            {
                var texture = assets?.GetTexture(material.TextureName);
                string textureText = texture == null ? " (missing)" : $" {texture.Width}x{texture.Height}";
                var flags = new List<string>();
                if (material.DoubleSided) flags.Add("2-sided");
                if (material.AlphaTest) flags.Add("alpha-test");
                if (material.Scrolls) flags.Add($"scroll({material.ScrollU},{material.ScrollV})");
                text.AppendLine($"  {material.Name}  tex={material.TextureName}{textureText}  blend={material.Blend}  flags=0x{material.Flags:X8} {string.Join(" ", flags)}");
            }
            if (model.RefPoints.Count > 0)
            {
                text.AppendLine("ref points");
                foreach (var refPoint in model.RefPoints) text.AppendLine($"  {refPoint.Name}  {Format(refPoint.Position)}");
            }
            if (loadedModel.Collisions.Count > 0)
                text.AppendLine($"collision   {string.Join(", ", loadedModel.Collisions.Select(collision => $"{collision.Name} ({collision.Triangles.Count} tris)"))}");
            return text;
        }

        private static string DescribeShape(ObjectShape shape) => shape.Kind switch
        {
            ShapeKind.Box => $"box at {Format(shape.Center)}, size {Format(shape.Size)}",
            ShapeKind.Sphere => $"sphere at {Format(shape.Center)}, radius {shape.Radius:0.##}",
            ShapeKind.CameraLine => $"camera {Format(shape.Points[0])} → {Format(shape.Points[1])}",
            _ => $"{shape.Points.Count} points",
        };

        private static readonly char[] ScriptSeparators = { ' ', '(', ')', ';', ',', '\t' };

        private static IEnumerable<string> Tokens(BniNode line)
        {
            if (line.Key != null && line.Key.Equals("ScriptCommand", StringComparison.OrdinalIgnoreCase))
                return line.JoinedValues.Split(ScriptSeparators, StringSplitOptions.RemoveEmptyEntries);
            return line.Values;
        }

        private static readonly HashSet<string> PositionalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "pos", "rot", "scale", "color", "waypoint", "ID" };

        private static bool IsPositional(BniNode line) => line.Key != null && PositionalKeys.Contains(line.Key);

        private static IEnumerable<BniNode> Flatten(BniNode node)
        {
            yield return node;
            foreach (var child in node.Children)
                foreach (var descendant in Flatten(child)) yield return descendant;
        }

        private static IEnumerable<(LevelObject target, BniNode line)> FindOutgoing(LevelObject source, LevelScene scene)
        {
            var objectsById = scene.AllObjects.Where(levelObject => levelObject.Id != 0)
                                   .GroupBy(levelObject => levelObject.Id)
                                   .ToDictionary(group => group.Key, group => group.ToList());
            var seen = new HashSet<LevelObject>();
            foreach (var field in source.Section.Fields)
                foreach (var line in Flatten(field))
                {
                    if (IsPositional(line)) continue;
                    foreach (var token in Tokens(line))
                        if (int.TryParse(token, out int id) && id > 0 && objectsById.TryGetValue(id, out var targets))
                            foreach (var target in targets)
                                if (target != source && seen.Add(target)) yield return (target, line);
                }
        }

        private static IEnumerable<(LevelObject source, BniNode line)> FindIncoming(LevelObject target, LevelScene scene)
        {
            string id = target.Id.ToString();
            foreach (var candidate in scene.AllObjects)
            {
                if (candidate == target || candidate.Section == null) continue;
                var match = candidate.Section.Fields.SelectMany(Flatten)
                                     .FirstOrDefault(line => !IsPositional(line) && Tokens(line).Any(token => token == id));
                if (match != null) yield return (candidate, match);
            }
        }

        private static void AppendNode(StringBuilder text, BniNode node, int depth, string skipChildrenNamed = null)
        {
            if (skipChildrenNamed != null && string.Equals(node.Key, skipChildrenNamed, StringComparison.OrdinalIgnoreCase)) return;
            if (depth > 0) text.Append(' ', depth * 2).AppendLine(node.ToString());
            foreach (var child in node.Children) AppendNode(text, child, depth + 1, skipChildrenNamed);
        }

        public static string Format(Vec3 vector) => $"{vector.X:0.###} {vector.Y:0.###} {vector.Z:0.###}";
    }
}
