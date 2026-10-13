using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Mk2.Formats;
using Mk2.Game;
using OpenTK.Graphics.OpenGL4;

namespace Mk2.Viewer.Rendering
{
    public enum ItemKind { Overlay, Object, EnvDome }

    public sealed class DrawItem
    {
        public GpuModel Model;
        public Matrix4x4 World;
        public LevelObject Object;
        public SceneChunk Chunk;
        public ItemKind Kind;
        public string Name;
        public Vector3 WorldMin, WorldMax;
    }

    public sealed class CollisionItem
    {
        public GpuMesh Mesh;
        public Matrix4x4 World;
        public SceneChunk Chunk;
        public LevelObject Object;
        public string Name;
        public int Triangles;
    }

    public sealed class PickResult
    {
        public DrawItem Item;
        public LevelObject Object;
        public float Distance;
        public Vector3 Point;
        public string Description;
    }

    /// <summary>Owns all GPU resources for one loaded scene and draws it.</summary>
    public sealed class SceneRenderer : IDisposable
    {
        public LevelScene Scene { get; private set; }
        public readonly List<DrawItem> Items = new List<DrawItem>();
        public readonly List<CollisionItem> Collisions = new List<CollisionItem>();
        public Vector3 SceneMin, SceneMax;
        public int LastDrawnTriangles;
        public LevelObject Selected;
        public DrawItem SelectedItem;

        private readonly Dictionary<LoadedModel, GpuModel> _models = new Dictionary<LoadedModel, GpuModel>();
        private readonly Dictionary<GpuModel, Vector3[]> _pickTris = new Dictionary<GpuModel, Vector3[]>();
        private readonly Dictionary<(string chunk, ObjectCategory cat), GpuLines> _shapeLines = new Dictionary<(string, ObjectCategory), GpuLines>();
        private readonly Dictionary<(string chunk, ObjectCategory cat), GpuLines> _markerLines = new Dictionary<(string, ObjectCategory), GpuLines>();
        private GpuLines _selectionLines;
        private GpuMesh _selectionFill;
        private GpuLines _gridLines;
        private TextureCache _textures;
        private ShaderProgram _mesh, _lines;
        private readonly DateTime _start = DateTime.Now;

        public static readonly Dictionary<ObjectCategory, Vector4> CategoryColors = new Dictionary<ObjectCategory, Vector4>
        {
            { ObjectCategory.Prop, new Vector4(0.75f, 0.75f, 0.75f, 1) },
            { ObjectCategory.Trigger, new Vector4(1.0f, 0.55f, 0.1f, 1) },
            { ObjectCategory.Path, new Vector4(0.2f, 0.9f, 1.0f, 1) },
            { ObjectCategory.Volume, new Vector4(0.3f, 1.0f, 0.3f, 1) },
            { ObjectCategory.Spawn, new Vector4(1.0f, 0.25f, 0.25f, 1) },
            { ObjectCategory.Camera, new Vector4(1.0f, 0.3f, 1.0f, 1) },
            { ObjectCategory.Sound, new Vector4(0.35f, 0.5f, 1.0f, 1) },
            { ObjectCategory.Checkpoint, new Vector4(1.0f, 1.0f, 0.2f, 1) },
            { ObjectCategory.Logic, new Vector4(1.0f, 1.0f, 1.0f, 1) },
            { ObjectCategory.Critter, new Vector4(0.6f, 1.0f, 0.6f, 1) },
        };

        public void InitGl()
        {
            _mesh ??= new ShaderProgram(Shaders.MeshVS, Shaders.MeshFS);
            _lines ??= new ShaderProgram(Shaders.LineVS, Shaders.LineFS);
            if (_gridLines == null)
            {
                var g = new List<float>();
                var col = new Vector4(0.35f, 0.35f, 0.4f, 1);
                for (int i = -20; i <= 20; i++)
                {
                    Line(g, new Vector3(i * 1000, 0, -20000), new Vector3(i * 1000, 0, 20000), col);
                    Line(g, new Vector3(-20000, 0, i * 1000), new Vector3(20000, 0, i * 1000), col);
                }
                _gridLines = GpuLines.Create(g);
            }
        }

        public void Load(LevelScene scene)
        {
            DisposeSceneResources();
            Scene = scene;
            _textures = new TextureCache(scene.Assets);
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);

            foreach (var ch in scene.Chunks)
            {
                foreach (var (ov, lm) in ch.Overlays)
                {
                    if (lm == null) continue;
                    AddItem(new DrawItem { Model = Gpu(lm), World = Matrix4x4.Identity, Chunk = ch, Kind = ItemKind.Overlay, Name = ov.Name });
                }
                if (ch.EnvDome != null)
                    AddItem(new DrawItem { Model = Gpu(ch.EnvDome), World = Matrix4x4.Identity, Chunk = ch, Kind = ItemKind.EnvDome, Name = ch.EnvDome.Name });
                foreach (var o in ch.Objects)
                {
                    if (ch.ObjectModels.TryGetValue(o, out var lm))
                    {
                        var w = o.ModelMatrix;
                        AddItem(new DrawItem { Model = Gpu(lm), World = w, Chunk = ch, Kind = ItemKind.Object, Object = o, Name = lm.Name });
                        foreach (var c in lm.Collisions)
                            AddCollision(c, w, ch, o);
                    }
                }
                foreach (var (name, c) in ch.Grounds) AddCollision(c, Matrix4x4.Identity, ch, null, name);
                BuildLines(ch);
            }
            foreach (var it in Items.Where(i => i.Kind == ItemKind.Overlay || i.Kind == ItemKind.Object))
            {
                min = Vector3.Min(min, it.WorldMin);
                max = Vector3.Max(max, it.WorldMax);
            }
            if (min.X > max.X)
            {
                foreach (var c in Collisions) { /* fall back to collision extents */ }
                min = new Vector3(-1000); max = new Vector3(1000);
            }
            SceneMin = min; SceneMax = max;
        }

        private GpuModel Gpu(LoadedModel lm)
        {
            if (_models.TryGetValue(lm, out var g)) return g;
            g = GpuModel.Build(lm, _textures);
            _models[lm] = g;
            return g;
        }

        private void AddItem(DrawItem it)
        {
            TransformBounds(it.Model.Min, it.Model.Max, it.World, out it.WorldMin, out it.WorldMax);
            Items.Add(it);
        }

        private static void TransformBounds(Vector3 min, Vector3 max, Matrix4x4 m, out Vector3 wmin, out Vector3 wmax)
        {
            wmin = new Vector3(float.MaxValue); wmax = new Vector3(float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                var p = new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
                var w = Vector3.Transform(p, m);
                wmin = Vector3.Min(wmin, w); wmax = Vector3.Max(wmax, w);
            }
        }

        private void AddCollision(Collision c, Matrix4x4 world, SceneChunk ch, LevelObject o, string name = null)
        {
            if (c == null || c.Triangles.Count == 0) return;
            // Flat-shaded: unshared vertices so each face gets its own normal and material colour.
            var data = new float[c.Triangles.Count * 3 * GpuMesh.Stride];
            var idx = new uint[c.Triangles.Count * 3];
            int k = 0;
            foreach (var t in c.Triangles)
            {
                var a = c.Vertices[t.A]; var b = c.Vertices[t.B]; var cc = c.Vertices[t.C];
                var n = Vector3.Cross(b - a, cc - a);
                n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
                var col = MaterialColor(c.MaterialName(t), t.Flags);
                foreach (var p in new[] { a, b, cc })
                {
                    int o2 = k * GpuMesh.Stride;
                    data[o2] = p.X; data[o2 + 1] = p.Y; data[o2 + 2] = p.Z;
                    data[o2 + 3] = n.X; data[o2 + 4] = n.Y; data[o2 + 5] = n.Z;
                    data[o2 + 8] = col.X; data[o2 + 9] = col.Y; data[o2 + 10] = col.Z; data[o2 + 11] = 1;
                    idx[k] = (uint)k;
                    k++;
                }
            }
            Collisions.Add(new CollisionItem
            {
                Mesh = GpuMesh.Create(data, idx), World = world, Chunk = ch, Object = o,
                Name = name ?? c.Name, Triangles = c.Triangles.Count,
            });
        }

        /// <summary>Stable pastel colour per collision material name.</summary>
        public static Vector3 MaterialColor(string name, ushort flags)
        {
            uint h = 2166136261;
            foreach (char ch in name ?? "") h = (h ^ char.ToLowerInvariant(ch)) * 16777619;
            float r = 0.35f + ((h >> 0) & 0xFF) / 255f * 0.6f;
            float g = 0.35f + ((h >> 8) & 0xFF) / 255f * 0.6f;
            float b = 0.35f + ((h >> 16) & 0xFF) / 255f * 0.6f;
            return new Vector3(r, g, b);
        }

        // ---------------------------------------------------------------- debug lines

        private static void Line(List<float> d, Vector3 a, Vector3 b, Vector4 c)
        {
            d.Add(a.X); d.Add(a.Y); d.Add(a.Z); d.Add(c.X); d.Add(c.Y); d.Add(c.Z); d.Add(c.W);
            d.Add(b.X); d.Add(b.Y); d.Add(b.Z); d.Add(c.X); d.Add(c.Y); d.Add(c.Z); d.Add(c.W);
        }

        public static void Box(List<float> d, Vector3 center, Vector3 rot, Vector3 size, Vector4 c)
        {
            var m = LevelObject.RotationPYR(rot);
            var h = size * 0.5f;
            var p = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                var l = new Vector3((i & 1) != 0 ? h.X : -h.X, (i & 2) != 0 ? h.Y : -h.Y, (i & 4) != 0 ? h.Z : -h.Z);
                p[i] = center + Vector3.TransformNormal(l, m);
            }
            int[] e = { 0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
            for (int i = 0; i < e.Length; i += 2) Line(d, p[e[i]], p[e[i + 1]], c);
        }

        public static void AabbLines(List<float> d, Vector3 min, Vector3 max, Vector4 c) =>
            Box(d, (min + max) * 0.5f, Vector3.Zero, max - min, c);

        public static void Sphere(List<float> d, Vector3 center, float r, Vector4 c)
        {
            const int N = 32;
            for (int axis = 0; axis < 3; axis++)
                for (int i = 0; i < N; i++)
                {
                    float a0 = i * MathF.PI * 2 / N, a1 = (i + 1) * MathF.PI * 2 / N;
                    Vector3 P(float a) => axis == 0 ? new Vector3(0, MathF.Cos(a), MathF.Sin(a)) * r
                                       : axis == 1 ? new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * r
                                       : new Vector3(MathF.Cos(a), MathF.Sin(a), 0) * r;
                    Line(d, center + P(a0), center + P(a1), c);
                }
        }

        public static void Cross(List<float> d, Vector3 p, float s, Vector4 c)
        {
            Line(d, p - Vector3.UnitX * s, p + Vector3.UnitX * s, c);
            Line(d, p - Vector3.UnitY * s, p + Vector3.UnitY * s, c);
            Line(d, p - Vector3.UnitZ * s, p + Vector3.UnitZ * s, c);
        }

        public static void ShapeLines(List<float> d, LevelObject o, Vector4 col)
        {
            foreach (var s in o.Shapes)
            {
                switch (s.Kind)
                {
                    case ShapeKind.Box: Box(d, s.Center, s.Rotation, s.Size, col); break;
                    case ShapeKind.Sphere: Sphere(d, s.Center, s.Radius, col); break;
                    case ShapeKind.Polyline:
                    case ShapeKind.Polygon:
                    {
                        var dim = col * new Vector4(0.6f, 0.6f, 0.6f, 1);
                        for (int i = 0; i + 1 < s.Points.Count; i++) Line(d, s.Points[i], s.Points[i + 1], col);
                        if (s.Kind == ShapeKind.Polygon && s.Points.Count > 2) Line(d, s.Points[^1], s.Points[0], col);
                        foreach (var p in s.Points) Line(d, p, p + Vector3.UnitY * 150, dim);
                        if (s.Points.Count > 0) Line(d, o.Position, s.Points[0], dim);
                        break;
                    }
                    case ShapeKind.CameraLine:
                        if (s.Points.Count == 2)
                        {
                            Line(d, s.Points[0], s.Points[1], col);
                            Box(d, s.Points[0], Vector3.Zero, new Vector3(60), col);
                        }
                        break;
                }
            }
        }

        private void BuildLines(SceneChunk ch)
        {
            foreach (var grp in ch.Objects.GroupBy(o => o.Category))
            {
                var col = CategoryColors[grp.Key];
                var shapes = new List<float>();
                var markers = new List<float>();
                foreach (var o in grp)
                {
                    ShapeLines(shapes, o, col);
                    if (!ch.ObjectModels.ContainsKey(o))
                    {
                        Cross(markers, o.Position, 60, col);
                        Box(markers, o.Position, o.Rotation, new Vector3(40), col);
                        // forward tick to show facing
                        var fwd = Vector3.TransformNormal(new Vector3(0, 0, 90), LevelObject.RotationPYR(o.Rotation));
                        Line(markers, o.Position, o.Position + fwd, col);
                    }
                }
                if (shapes.Count > 0) _shapeLines[(ch.Def.Name, grp.Key)] = GpuLines.Create(shapes);
                if (markers.Count > 0) _markerLines[(ch.Def.Name, grp.Key)] = GpuLines.Create(markers);
            }
        }

        public void SetSelection(LevelObject o, DrawItem item = null)
        {
            Selected = o;
            SelectedItem = item ?? (o == null ? null : Items.FirstOrDefault(i => i.Object == o));
            _selectionLines?.Dispose();
            _selectionLines = null;
            var d = new List<float>();
            var y = new Vector4(1, 1, 0, 1);
            if (SelectedItem != null) AabbLines(d, SelectedItem.WorldMin, SelectedItem.WorldMax, y);
            if (o != null)
            {
                ShapeLines(d, o, y);
                Cross(d, o.Position, 120, y);
            }
            if (d.Count > 0) _selectionLines = GpuLines.Create(d);

            _selectionFill?.Dispose();
            _selectionFill = null;
            if (o != null && SelectedItem == null)
            {
                var v = new List<float>(); var ix = new List<uint>();
                foreach (var sh in o.Shapes)
                {
                    if (sh.Kind == ShapeKind.Box) FillBox(v, ix, sh.Center, sh.Rotation, sh.Size);
                    else if (sh.Kind == ShapeKind.Sphere) FillSphere(v, ix, sh.Center, sh.Radius);
                    else if (sh.Kind == ShapeKind.Polygon && sh.Points.Count >= 3) FillPolygon(v, ix, sh.Points);
                }
                if (o.Shapes.Count == 0 || !o.Shapes.Any(x => x.Kind == ShapeKind.Box || x.Kind == ShapeKind.Sphere))
                    FillBox(v, ix, o.Position, o.Rotation, new Vector3(90));
                if (ix.Count > 0) _selectionFill = GpuMesh.Create(v.ToArray(), ix.ToArray());
            }
        }

        private static void Vert(List<float> v, Vector3 p, Vector3 n)
        {
            v.Add(p.X); v.Add(p.Y); v.Add(p.Z); v.Add(n.X); v.Add(n.Y); v.Add(n.Z);
            v.Add(0); v.Add(0); v.Add(1); v.Add(1); v.Add(1); v.Add(1);
        }

        private static void FillBox(List<float> v, List<uint> ix, Vector3 c, Vector3 rot, Vector3 size)
        {
            var m = LevelObject.RotationPYR(rot);
            var h = size * 0.5f;
            uint b = (uint)(v.Count / GpuMesh.Stride);
            for (int i = 0; i < 8; i++)
            {
                var l = new Vector3((i & 1) != 0 ? h.X : -h.X, (i & 2) != 0 ? h.Y : -h.Y, (i & 4) != 0 ? h.Z : -h.Z);
                Vert(v, c + Vector3.TransformNormal(l, m), Vector3.UnitY);
            }
            int[] f = { 0,1,3, 0,3,2, 4,6,7, 4,7,5, 0,4,5, 0,5,1, 2,3,7, 2,7,6, 0,2,6, 0,6,4, 1,5,7, 1,7,3 };
            foreach (var k in f) ix.Add(b + (uint)k);
        }

        private static void FillSphere(List<float> v, List<uint> ix, Vector3 c, float r)
        {
            const int S = 16, R = 10;
            uint b = (uint)(v.Count / GpuMesh.Stride);
            for (int y = 0; y <= R; y++)
                for (int x = 0; x <= S; x++)
                {
                    float th = y * MathF.PI / R, ph = x * 2 * MathF.PI / S;
                    var n = new Vector3(MathF.Sin(th) * MathF.Cos(ph), MathF.Cos(th), MathF.Sin(th) * MathF.Sin(ph));
                    Vert(v, c + n * r, n);
                }
            for (int y = 0; y < R; y++)
                for (int x = 0; x < S; x++)
                {
                    uint a = b + (uint)(y * (S + 1) + x), d = a + S + 1;
                    ix.Add(a); ix.Add(d); ix.Add(a + 1); ix.Add(a + 1); ix.Add(d); ix.Add(d + 1);
                }
        }

        private static void FillPolygon(List<float> v, List<uint> ix, List<Vector3> pts)
        {
            uint b = (uint)(v.Count / GpuMesh.Stride);
            foreach (var p in pts) Vert(v, p, Vector3.UnitY);
            for (int i = 1; i + 1 < pts.Count; i++) { ix.Add(b); ix.Add(b + (uint)i); ix.Add(b + (uint)i + 1); }
        }

        public bool Visible(DrawItem it, ViewSettings s)
        {
            if (s.HiddenChunks.Contains(it.Chunk.Def.Name)) return false;
            switch (it.Kind)
            {
                case ItemKind.Overlay: return s.Overlays;
                case ItemKind.EnvDome: return s.EnvDome;
                case ItemKind.Object: return s.Objects && s.CategoryVisible(it.Object.Category);
            }
            return true;
        }

        // ---------------------------------------------------------------- drawing

        public void Render(Camera cam, int width, int height, ViewSettings s, Vector3 clearColor, Vector3 fogColor, Vector2 fogRange)
        {
            InitGl();
            GL.Viewport(0, 0, Math.Max(width, 1), Math.Max(height, 1));
            GL.ClearColor(clearColor.X, clearColor.Y, clearColor.Z, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            LastDrawnTriangles = 0;

            var mirror = s.MirrorX ? Matrix4x4.CreateScale(-1, 1, 1) : Matrix4x4.Identity;
            var view = cam.View;
            var proj = cam.Projection(width / (float)Math.Max(height, 1));
            var viewProj = mirror * view * proj;
            float time = (float)(DateTime.Now - _start).TotalSeconds;

            GL.FrontFace(s.MirrorX ? FrontFaceDirection.Cw : FrontFaceDirection.Ccw);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.PolygonMode(TriangleFace.FrontAndBack, s.Wireframe ? PolygonMode.Line : PolygonMode.Fill);

            _mesh.Use();
            _mesh.Set("uViewProj", viewProj);
            _mesh.Set("uTex", 0);
            _mesh.Set("uLightDir", Vector3.Normalize(new Vector3(0.4f, 1f, 0.3f)));
            _mesh.Set("uFog", s.Fog);
            _mesh.Set("uFogColor", fogColor);
            _mesh.Set("uFogRange", fogRange);
            _mesh.Set("uEye", s.MirrorX ? cam.Position * new Vector3(-1, 1, 1) : cam.Position);
            _mesh.Set("uBrightness", s.Brightness);
            GL.ActiveTexture(TextureUnit.Texture0);

            // 1. Environment dome, centred on the camera, behind everything.
            if (s.EnvDome && Scene != null)
            {
                GL.Disable(EnableCap.DepthTest);
                GL.DepthMask(false);
                GL.Disable(EnableCap.CullFace);
                var eye = s.MirrorX ? cam.Position * new Vector3(-1, 1, 1) : cam.Position;
                foreach (var it in Items.Where(i => i.Kind == ItemKind.EnvDome && !s.HiddenChunks.Contains(i.Chunk.Def.Name)))
                {
                    var c = (it.Model.Min + it.Model.Max) * 0.5f;
                    var w = Matrix4x4.CreateTranslation(eye - new Vector3(c.X, 0, c.Z));
                    DrawModel(it.Model, w, s, time, pass: 0, forceNoFog: true);
                    DrawModel(it.Model, w, s, time, pass: 1, forceNoFog: true);
                }
                GL.Enable(EnableCap.DepthTest);
                GL.DepthMask(true);
            }

            if (s.CullBackfaces) { GL.Enable(EnableCap.CullFace); GL.CullFace(TriangleFace.Back); }
            else GL.Disable(EnableCap.CullFace);

            // 2. Opaque + alpha-tested geometry.
            GL.Disable(EnableCap.Blend);
            foreach (var it in Items)
                if (it.Kind != ItemKind.EnvDome && Visible(it, s)) DrawModel(it.Model, it.World, s, time, pass: 0);

            // 3. Collision.
            if (s.GroundCollision || s.ObjectCollision)
            {
                bool overlay = s.CollisionOverlay;
                if (overlay)
                {
                    GL.Enable(EnableCap.Blend);
                    GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    GL.Enable(EnableCap.PolygonOffsetFill);
                    GL.PolygonOffset(-1f, -2f);
                    GL.DepthMask(false);
                }
                _mesh.Set("uUseTex", 0);
                _mesh.Set("uUseVCol", 1);
                _mesh.Set("uLighting", 1);
                _mesh.Set("uAlphaRef", 0f);
                _mesh.Set("uUVOffset", Vector2.Zero);
                _mesh.Set("uTint", new Vector4(1, 1, 1, overlay ? 0.45f : 1f));
                float prevBright = s.Brightness;
                _mesh.Set("uBrightness", 1f);
                foreach (var c in Collisions)
                {
                    if (s.HiddenChunks.Contains(c.Chunk.Def.Name)) continue;
                    if (c.Object == null ? !s.GroundCollision : !s.ObjectCollision) continue;
                    if (c.Object != null && (!s.Objects || !s.CategoryVisible(c.Object.Category))) continue;
                    _mesh.Set("uModel", c.World);
                    c.Mesh.Draw();
                    LastDrawnTriangles += c.Triangles;
                }
                _mesh.Set("uBrightness", prevBright);
                GL.Disable(EnableCap.PolygonOffsetFill);
                GL.DepthMask(true);
                GL.Disable(EnableCap.Blend);
            }

            // 4. Transparent geometry (no sorting; depth writes off).
            if (s.Transparency)
            {
                GL.Enable(EnableCap.Blend);
                GL.DepthMask(false);
                foreach (var it in Items)
                    if (it.Kind != ItemKind.EnvDome && Visible(it, s)) DrawModel(it.Model, it.World, s, time, pass: 1);
                GL.DepthMask(true);
                GL.Disable(EnableCap.Blend);
            }

            // 4b. Selection highlight on the selected model itself.
            if (SelectedItem != null && Visible(SelectedItem, s))
                DrawSelectionHighlight(SelectedItem, s);

            // 5. Lines.
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
            _lines.Use();
            _lines.Set("uViewProj", viewProj);
            _lines.Set("uModel", Matrix4x4.Identity);
            _lines.Set("uTint", Vector4.One);
            if (Scene == null || Items.Count == 0) _gridLines?.Draw();
            if (s.Objects)
            {
                if (s.Shapes)
                    foreach (var kv in _shapeLines)
                        if (!s.HiddenChunks.Contains(kv.Key.chunk) && s.CategoryVisible(kv.Key.cat)) kv.Value.Draw();
                if (s.Markers)
                    foreach (var kv in _markerLines)
                        if (!s.HiddenChunks.Contains(kv.Key.chunk) && s.CategoryVisible(kv.Key.cat)) kv.Value.Draw();
            }
            if (_selectionFill != null)
            {
                float pulse = 0.5f + 0.5f * MathF.Sin((float)(DateTime.Now - _start).TotalSeconds * 4f);
                _mesh.Use();
                _mesh.Set("uModel", Matrix4x4.Identity);
                _mesh.Set("uUseTex", false); _mesh.Set("uUseVCol", false); _mesh.Set("uLighting", false);
                _mesh.Set("uAlphaRef", 0f); _mesh.Set("uFog", false); _mesh.Set("uUVOffset", Vector2.Zero);
                _mesh.Set("uTint", new Vector4(1f, 0.85f, 0.1f, 0.12f + 0.12f * pulse));
                GL.Enable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                GL.DepthMask(false);
                GL.Disable(EnableCap.CullFace);
                _selectionFill.Draw();
                GL.DepthMask(true);
                GL.Disable(EnableCap.Blend);
                _mesh.Set("uFog", s.Fog);
                _lines.Use();
            }
            if (_selectionLines != null)
            {
                GL.Disable(EnableCap.DepthTest);
                float pl = 0.6f + 0.4f * MathF.Sin((float)(DateTime.Now - _start).TotalSeconds * 4f);
                _lines.Set("uTint", new Vector4(1, 1, pl, 1));
                _selectionLines.Draw();
                GL.Enable(EnableCap.DepthTest);
            }
            GL.BindVertexArray(0);
        }

        /// <summary>pass 0 = opaque/alpha-test parts, pass 1 = blended parts.</summary>
        private void DrawSelectionHighlight(DrawItem it, ViewSettings s)
        {
            float pulse = 0.5f + 0.5f * MathF.Sin((float)(DateTime.Now - _start).TotalSeconds * 4f);
            _mesh.Use();
            _mesh.Set("uModel", it.World);
            _mesh.Set("uUseTex", false);
            _mesh.Set("uUseVCol", false);
            _mesh.Set("uLighting", false);
            _mesh.Set("uAlphaRef", 0f);
            _mesh.Set("uFog", false);
            _mesh.Set("uUVOffset", Vector2.Zero);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.DepthMask(false);
            GL.Disable(EnableCap.CullFace);

            void Parts()
            {
                foreach (var p in it.Model.Parts)
                {
                    if (p.IsCollisionComponent && !s.ModelCollisionComponents) continue;
                    if (p.IsFragment && !s.Fragments) continue;
                    p.Mesh.Draw();
                }
            }

            // a) translucent yellow fill where visible
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.Enable(EnableCap.PolygonOffsetFill);
            GL.PolygonOffset(-1f, -4f);
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
            _mesh.Set("uTint", new Vector4(1f, 0.85f, 0.1f, 0.25f + 0.15f * pulse));
            Parts();
            GL.Disable(EnableCap.PolygonOffsetFill);

            // b) faint x-ray wireframe so it can be found behind walls
            GL.Disable(EnableCap.DepthTest);
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            _mesh.Set("uTint", new Vector4(1f, 0.9f, 0.2f, 0.18f));
            Parts();

            // c) bright wireframe where visible
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.PolygonOffsetLine);
            GL.PolygonOffset(-1f, -8f);
            _mesh.Set("uTint", new Vector4(1f, 1f, 0.25f, 0.9f));
            Parts();
            GL.Disable(EnableCap.PolygonOffsetLine);

            GL.PolygonMode(TriangleFace.FrontAndBack, s.Wireframe ? PolygonMode.Line : PolygonMode.Fill);
            GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);
            _mesh.Set("uFog", s.Fog);
            if (s.CullBackfaces) GL.Enable(EnableCap.CullFace);
        }

        private void DrawModel(GpuModel m, Matrix4x4 world, ViewSettings s, float time, int pass, bool forceNoFog = false)
        {
            bool modelSet = false;
            foreach (var p in m.Parts)
            {
                if (p.IsCollisionComponent && !s.ModelCollisionComponents) continue;
                if (p.IsFragment && !s.Fragments) continue;
                bool transparent = s.Transparency && p.Transparent;
                if ((pass == 1) != transparent) continue;
                if (!modelSet)
                {
                    _mesh.Set("uModel", world);
                    modelSet = true;
                }
                var mat = p.Material;
                bool useTex = s.Textures && p.Texture != 0;
                _mesh.Set("uUseTex", useTex);
                _mesh.Set("uUseVCol", s.VertexColors);
                _mesh.Set("uLighting", s.Lighting || p.IsCollisionComponent);
                float aref = 0f;
                if (useTex && (mat.AlphaTest || p.TextureIsCutout || (p.TextureHasAlpha && !transparent)))
                    aref = mat.AlphaRef > 0 ? mat.AlphaRef : 0.5f;
                if (pass == 1) aref = 0.01f;
                _mesh.Set("uAlphaRef", aref);
                var tint = p.IsCollisionComponent ? new Vector4(0.4f, 1f, 0.4f, 1f) : Vector4.One;
                _mesh.Set("uTint", tint);
                var off = mat.Scrolls ? new Vector2(Frac(mat.ScrollU * time), Frac(mat.ScrollV * time)) : Vector2.Zero;
                _mesh.Set("uUVOffset", off);
                if (forceNoFog) _mesh.Set("uFog", false);
                if (pass == 1)
                {
                    if (mat.Blend == BlendMode.Additive) GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                    else if (mat.Blend == BlendMode.Subtractive)
                    {
                        GL.BlendEquation(BlendEquationMode.FuncReverseSubtract);
                        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                    }
                    else GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                }
                GL.BindTexture(TextureTarget.Texture2D, useTex ? p.Texture : 0);
                p.Mesh.Draw();
                LastDrawnTriangles += p.Triangles;
                if (pass == 1 && mat.Blend == BlendMode.Subtractive) GL.BlendEquation(BlendEquationMode.FuncAdd);
                if (forceNoFog) _mesh.Set("uFog", s.Fog);
            }
        }

        private static float Frac(float v) => v - MathF.Floor(v);

        // ---------------------------------------------------------------- picking

        private Vector3[] PickTriangles(GpuModel g)
        {
            if (_pickTris.TryGetValue(g, out var t)) return t;
            var list = new List<Vector3>();
            var model = g.Source.Model;
            foreach (var mesh in model.Meshes)
            {
                var comp = model.Components[mesh.ComponentIndex];
                if (comp.IsCollision || comp.IsFragment) continue;
                foreach (var s in mesh.Strips)
                    for (int i = 0; i + 2 < s.Vertices.Count; i++)
                    {
                        list.Add(s.Vertices[i].Position);
                        list.Add(s.Vertices[i + 1].Position);
                        list.Add(s.Vertices[i + 2].Position);
                    }
            }
            t = list.ToArray();
            _pickTris[g] = t;
            return t;
        }

        public PickResult Pick(Vector3 origin, Vector3 dir, ViewSettings s)
        {
            if (s.MirrorX)
            {
                origin *= new Vector3(-1, 1, 1);
                dir *= new Vector3(-1, 1, 1);
            }
            PickResult best = null;
            void Consider(float t, DrawItem it, LevelObject o, string desc)
            {
                if (t < 0 || (best != null && t >= best.Distance)) return;
                best = new PickResult { Distance = t, Item = it, Object = o, Point = origin + dir * t, Description = desc };
            }

            foreach (var it in Items)
            {
                if (it.Kind == ItemKind.EnvDome || !Visible(it, s)) continue;
                if (!RayAabb(origin, dir, it.WorldMin, it.WorldMax, out float tb) || (best != null && tb > best.Distance)) continue;
                if (!Matrix4x4.Invert(it.World, out var inv)) continue;
                var lo = Vector3.Transform(origin, inv);
                var ld = Vector3.TransformNormal(dir, inv);
                var tris = PickTriangles(it.Model);
                float bestLocal = float.MaxValue;
                for (int i = 0; i < tris.Length; i += 3)
                    if (RayTri(lo, ld, tris[i], tris[i + 1], tris[i + 2], out float tl) && tl < bestLocal) bestLocal = tl;
                if (bestLocal < float.MaxValue)
                {
                    var hit = Vector3.Transform(lo + ld * bestLocal, it.World);
                    Consider(Vector3.Distance(origin, hit), it, it.Object, it.Kind == ItemKind.Overlay ? "overlay " + it.Name : it.Name);
                }
            }

            if (Scene != null && s.Objects)
            {
                foreach (var ch in Scene.Chunks)
                {
                    if (s.HiddenChunks.Contains(ch.Def.Name)) continue;
                    foreach (var o in ch.Objects)
                    {
                        if (!s.CategoryVisible(o.Category)) continue;
                        if (!ch.ObjectModels.ContainsKey(o) && s.Markers && RaySphere(origin, dir, o.Position, 70, out float t0))
                            Consider(t0, null, o, o.DisplayName);
                        if (!s.Shapes) continue;
                        foreach (var sh in o.Shapes)
                        {
                            switch (sh.Kind)
                            {
                                case ShapeKind.Sphere:
                                    if (RaySphereShell(origin, dir, sh.Center, sh.Radius, out float ts)) Consider(ts, null, o, o.DisplayName);
                                    break;
                                case ShapeKind.Box:
                                {
                                    var m = LevelObject.RotationPYR(sh.Rotation) * Matrix4x4.CreateTranslation(sh.Center);
                                    if (Matrix4x4.Invert(m, out var inv))
                                    {
                                        var lo = Vector3.Transform(origin, inv);
                                        var ld = Vector3.TransformNormal(dir, inv);
                                        if (RayAabb(lo, ld, -sh.Size * 0.5f, sh.Size * 0.5f, out float tbx)) Consider(tbx, null, o, o.DisplayName);
                                    }
                                    break;
                                }
                                default:
                                    foreach (var p in sh.Points)
                                        if (RaySphere(origin, dir, p, 50, out float tp)) Consider(tp, null, o, o.DisplayName);
                                    break;
                            }
                        }
                    }
                }
            }
            return best;
        }

        private static bool RayAabb(Vector3 o, Vector3 d, Vector3 min, Vector3 max, out float t)
        {
            float tmin = 0, tmax = float.MaxValue;
            t = 0;
            for (int i = 0; i < 3; i++)
            {
                float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z;
                float di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
                float mn = i == 0 ? min.X : i == 1 ? min.Y : min.Z;
                float mx = i == 0 ? max.X : i == 1 ? max.Y : max.Z;
                if (MathF.Abs(di) < 1e-9f)
                {
                    if (oi < mn || oi > mx) return false;
                }
                else
                {
                    float t1 = (mn - oi) / di, t2 = (mx - oi) / di;
                    if (t1 > t2) (t1, t2) = (t2, t1);
                    tmin = Math.Max(tmin, t1);
                    tmax = Math.Min(tmax, t2);
                    if (tmin > tmax) return false;
                }
            }
            t = tmin;
            return true;
        }

        private static bool RayTri(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0;
            var e1 = b - a; var e2 = c - a;
            var p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-9f) return false;
            float inv = 1f / det;
            var s = o - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0 || u > 1) return false;
            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0 || u + v > 1) return false;
            t = Vector3.Dot(e2, q) * inv;
            return t > 0;
        }

        private static bool RaySphere(Vector3 o, Vector3 d, Vector3 c, float r, out float t)
        {
            t = 0;
            var oc = o - c;
            float b = Vector3.Dot(oc, d);
            float cc = oc.LengthSquared() - r * r;
            float disc = b * b - cc;
            if (disc < 0) return false;
            float sq = MathF.Sqrt(disc);
            t = -b - sq;
            if (t < 0) t = -b + sq;
            return t >= 0;
        }

        /// <summary>Only hits when the camera is outside the sphere (so big trigger spheres don't swallow every click).</summary>
        private static bool RaySphereShell(Vector3 o, Vector3 d, Vector3 c, float r, out float t)
        {
            t = 0;
            if (Vector3.DistanceSquared(o, c) < r * r) return false;
            return RaySphere(o, d, c, r, out t);
        }

        // ---------------------------------------------------------------- screenshots

        public static byte[] ReadPixels(int w, int h)
        {
            var px = new byte[w * h * 4];
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.ReadPixels(0, 0, w, h, PixelFormat.Rgba, PixelType.UnsignedByte, px);
            // flip rows (GL origin is bottom-left)
            var row = new byte[w * 4];
            for (int y = 0; y < h / 2; y++)
            {
                System.Buffer.BlockCopy(px, y * w * 4, row, 0, w * 4);
                System.Buffer.BlockCopy(px, (h - 1 - y) * w * 4, px, y * w * 4, w * 4);
                System.Buffer.BlockCopy(row, 0, px, (h - 1 - y) * w * 4, w * 4);
            }
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            return px;
        }

        // ---------------------------------------------------------------- cleanup

        private void DisposeSceneResources()
        {
            foreach (var m in _models.Values) m.Dispose();
            _models.Clear();
            _pickTris.Clear();
            foreach (var c in Collisions) c.Mesh.Dispose();
            Collisions.Clear();
            foreach (var l in _shapeLines.Values) l.Dispose();
            foreach (var l in _markerLines.Values) l.Dispose();
            _shapeLines.Clear();
            _markerLines.Clear();
            _selectionLines?.Dispose();
            _selectionLines = null;
            _selectionFill?.Dispose();
            _selectionFill = null;
            _textures?.Dispose();
            _textures = null;
            Items.Clear();
            Selected = null;
            SelectedItem = null;
            Scene = null;
        }

        public void Dispose()
        {
            DisposeSceneResources();
            _gridLines?.Dispose();
            _mesh?.Dispose();
            _lines?.Dispose();
        }
    }
}
