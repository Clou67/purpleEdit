using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Mk2.Formats;
using Mk2.Game;
using OpenTK.Graphics.OpenGL4;

namespace Mk2.Viewer.Rendering {
    public enum ItemKind { Overlay, Object, EnvDome }

    public sealed class DrawItem {
        public GpuModel Model;
        public Matrix4x4 World;
        public LevelObject Object;
        public SceneChunk Chunk;
        public ItemKind Kind;
        public string Name;
        public Vector3 WorldMin, WorldMax;
        public Vector4 Tint = Vector4.One;
    }

    public sealed class CollisionItem {
        public GpuMesh Mesh;
        public Matrix4x4 World;
        public SceneChunk Chunk;
        public LevelObject Object;
        public string Name;
        public int Triangles;
        public Vector3 WorldMin, WorldMax;
    }

    public sealed class PickResult {
        public DrawItem Item;
        public LevelObject Object;
        public float Distance;
        public Vector3 Point;
        public string Description;
    }

    /// <summary>Owns all GPU resources for one loaded scene and draws it.</summary>
    public sealed partial class SceneRenderer : IDisposable {
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

        public void InitGl() {
            _mesh ??= new ShaderProgram(Shaders.MeshVS, Shaders.MeshFS);
            _lines ??= new ShaderProgram(Shaders.LineVS, Shaders.LineFS);
            if (_gridLines == null) {
                var g = new List<float>();
                var col = new Vector4(0.35f, 0.35f, 0.4f, 1);
                for (int i = -20; i <= 20; i++) {
                    Line(g, new Vector3(i * 1000, 0, -20000), new Vector3(i * 1000, 0, 20000), col);
                    Line(g, new Vector3(-20000, 0, i * 1000), new Vector3(20000, 0, i * 1000), col);
                }
                _gridLines = GpuLines.Create(g);
            }
        }

        public void Load(LevelScene scene) {
            DisposeSceneResources();
            Scene = scene;
            _textures = new TextureCache(scene.Assets);
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);

            foreach (var ch in scene.Chunks) {
                foreach (var (ov, lm) in ch.Overlays) {
                    if (lm == null) continue;
                    AddItem(new DrawItem { Model = Gpu(lm), World = Matrix4x4.Identity, Chunk = ch, Kind = ItemKind.Overlay, Name = ov.Name });
                }
                if (ch.EnvDome != null)
                    AddItem(new DrawItem { Model = Gpu(ch.EnvDome), World = Matrix4x4.Identity, Chunk = ch, Kind = ItemKind.EnvDome, Name = ch.EnvDome.Name });
                foreach (var o in ch.Objects) {
                    if (ch.ObjectModels.TryGetValue(o, out var lm)) {
                        var w = o.ModelMatrix;
                        AddItem(new DrawItem { Model = Gpu(lm), World = w, Chunk = ch, Kind = ItemKind.Object, Object = o, Name = lm.Name, Tint = ObjectTint(o) });
                        foreach (var c in lm.Collisions)
                            AddCollision(c, w, ch, o);
                    }
                }
                foreach (var (name, c) in ch.Grounds) AddCollision(c, Matrix4x4.Identity, ch, null, name);
                BuildLines(ch);
            }
            foreach (var it in Items.Where(i => i.Kind == ItemKind.Overlay || i.Kind == ItemKind.Object)) {
                min = Vector3.Min(min, it.WorldMin);
                max = Vector3.Max(max, it.WorldMax);
            }
            if (min.X > max.X) {
                foreach (var c in Collisions) { /* fall back to collision extents */ }
                min = new Vector3(-1000); max = new Vector3(1000);
            }
            SceneMin = min; SceneMax = max;
        }

        /// <summary>The layout's "color r g b" tint. Values above 2 are taken as PS2 style, where 128 is 1.0.</summary>
        private static Vector4 ObjectTint(LevelObject levelObject) {
            if (!levelObject.Color.HasValue) return Vector4.One;
            var color = levelObject.Color.Value;
            float largest = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
            if (largest <= 0) return Vector4.One;
            if (largest > 2) color /= 128f;
            color = Vector3.Clamp(color, Vector3.Zero, new Vector3(2));
            return new Vector4(color, 1);
        }

        private GpuModel Gpu(LoadedModel lm) {
            if (_models.TryGetValue(lm, out var g)) return g;
            g = GpuModel.Build(lm, _textures);
            _models[lm] = g;
            return g;
        }

        private void AddItem(DrawItem it) {
            TransformBounds(it.Model.Min, it.Model.Max, it.World, out it.WorldMin, out it.WorldMax);
            Items.Add(it);
        }

        private static void TransformBounds(Vector3 min, Vector3 max, Matrix4x4 m, out Vector3 wmin, out Vector3 wmax) {
            wmin = new Vector3(float.MaxValue); wmax = new Vector3(float.MinValue);
            for (int i = 0; i < 8; i++) {
                var p = new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
                var w = Vector3.Transform(p, m);
                wmin = Vector3.Min(wmin, w); wmax = Vector3.Max(wmax, w);
            }
        }

        private void AddCollision(Collision c, Matrix4x4 world, SceneChunk ch, LevelObject o, string name = null) {
            if (c == null || c.Triangles.Count == 0) return;
            // Flat-shaded: unshared vertices so each face gets its own normal and material colour.
            var data = new float[c.Triangles.Count * 3 * GpuMesh.Stride];
            var idx = new uint[c.Triangles.Count * 3];
            int k = 0;
            foreach (var t in c.Triangles) {
                var a = c.Vertices[t.A]; var b = c.Vertices[t.B]; var cc = c.Vertices[t.C];
                var n = Vector3.Cross(b - a, cc - a);
                n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
                var col = MaterialColor(c.MaterialName(t), t.Flags);
                foreach (var p in new[] { a, b, cc }) {
                    int o2 = k * GpuMesh.Stride;
                    data[o2] = p.X; data[o2 + 1] = p.Y; data[o2 + 2] = p.Z;
                    data[o2 + 3] = n.X; data[o2 + 4] = n.Y; data[o2 + 5] = n.Z;
                    data[o2 + 8] = col.X; data[o2 + 9] = col.Y; data[o2 + 10] = col.Z; data[o2 + 11] = 1;
                    idx[k] = (uint)k;
                    k++;
                }
            }
            var lmin = new Vector3(float.MaxValue); var lmax = new Vector3(float.MinValue);
            foreach (var p in c.Vertices) { lmin = Vector3.Min(lmin, p); lmax = Vector3.Max(lmax, p); }
            TransformBounds(lmin, lmax, world, out var wmin, out var wmax);
            Collisions.Add(new CollisionItem {
                Mesh = GpuMesh.Create(data, idx),
                World = world,
                Chunk = ch,
                Object = o,
                Name = name ?? c.Name,
                Triangles = c.Triangles.Count,
                WorldMin = wmin,
                WorldMax = wmax,
            });
        }

        /// <summary>Stable pastel colour per collision material name.</summary>
        public static Vector3 MaterialColor(string name, ushort flags) {
            uint h = 2166136261;
            foreach (char ch in name ?? "") h = (h ^ char.ToLowerInvariant(ch)) * 16777619;
            float r = 0.35f + ((h >> 0) & 0xFF) / 255f * 0.6f;
            float g = 0.35f + ((h >> 8) & 0xFF) / 255f * 0.6f;
            float b = 0.35f + ((h >> 16) & 0xFF) / 255f * 0.6f;
            return new Vector3(r, g, b);
        }

        // ---------------------------------------------------------------- debug lines

        private static void Line(List<float> d, Vector3 a, Vector3 b, Vector4 c) {
            d.Add(a.X); d.Add(a.Y); d.Add(a.Z); d.Add(c.X); d.Add(c.Y); d.Add(c.Z); d.Add(c.W);
            d.Add(b.X); d.Add(b.Y); d.Add(b.Z); d.Add(c.X); d.Add(c.Y); d.Add(c.Z); d.Add(c.W);
        }

        public static void Box(List<float> d, Vector3 center, Vector3 rot, Vector3 size, Vector4 c) {
            var m = LevelObject.RotationPYR(rot);
            var h = size * 0.5f;
            var p = new Vector3[8];
            for (int i = 0; i < 8; i++) {
                var l = new Vector3((i & 1) != 0 ? h.X : -h.X, (i & 2) != 0 ? h.Y : -h.Y, (i & 4) != 0 ? h.Z : -h.Z);
                p[i] = center + Vector3.TransformNormal(l, m);
            }
            int[] e = { 0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
            for (int i = 0; i < e.Length; i += 2) Line(d, p[e[i]], p[e[i + 1]], c);
        }

        public static void AabbLines(List<float> d, Vector3 min, Vector3 max, Vector4 c) =>
            Box(d, (min + max) * 0.5f, Vector3.Zero, max - min, c);

        public static void Sphere(List<float> d, Vector3 center, float r, Vector4 c) {
            const int N = 32;
            for (int axis = 0; axis < 3; axis++)
                for (int i = 0; i < N; i++) {
                    float a0 = i * MathF.PI * 2 / N, a1 = (i + 1) * MathF.PI * 2 / N;
                    Vector3 P(float a) => axis == 0 ? new Vector3(0, MathF.Cos(a), MathF.Sin(a)) * r
                                       : axis == 1 ? new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * r
                                       : new Vector3(MathF.Cos(a), MathF.Sin(a), 0) * r;
                    Line(d, center + P(a0), center + P(a1), c);
                }
        }

        public static void Cross(List<float> d, Vector3 p, float s, Vector4 c) {
            Line(d, p - Vector3.UnitX * s, p + Vector3.UnitX * s, c);
            Line(d, p - Vector3.UnitY * s, p + Vector3.UnitY * s, c);
            Line(d, p - Vector3.UnitZ * s, p + Vector3.UnitZ * s, c);
        }

        public static void ShapeLines(List<float> d, LevelObject o, Vector4 col) {
            foreach (var s in o.Shapes) {
                switch (s.Kind) {
                    case ShapeKind.Box: Box(d, s.Center, s.Rotation, s.Size, col); break;
                    case ShapeKind.Sphere: Sphere(d, s.Center, s.Radius, col); break;
                    case ShapeKind.Polyline:
                    case ShapeKind.Polygon: {
                            var dim = col * new Vector4(0.6f, 0.6f, 0.6f, 1);
                            for (int i = 0; i + 1 < s.Points.Count; i++) Line(d, s.Points[i], s.Points[i + 1], col);
                            if (s.Kind == ShapeKind.Polygon && s.Points.Count > 2) Line(d, s.Points[^1], s.Points[0], col);
                            foreach (var p in s.Points) Line(d, p, p + Vector3.UnitY * 150, dim);
                            if (s.Points.Count > 0) Line(d, o.Position, s.Points[0], dim);
                            break;
                        }
                    case ShapeKind.CameraLine:
                        if (s.Points.Count == 2) {
                            Line(d, s.Points[0], s.Points[1], col);
                            Box(d, s.Points[0], Vector3.Zero, new Vector3(60), col);
                        }
                        break;
                }
            }
        }

        /// <summary>Re-create the debug line batches (after individual objects were hidden/shown).</summary>
        public void RebuildLines(ViewSettings s) {
            foreach (var l in _shapeLines.Values) l.Dispose();
            foreach (var l in _markerLines.Values) l.Dispose();
            _shapeLines.Clear();
            _markerLines.Clear();
            if (Scene == null) return;
            foreach (var ch in Scene.Chunks) BuildLines(ch, s);
        }

        private void BuildLines(SceneChunk ch, ViewSettings s = null) {
            foreach (var grp in ch.Objects.Where(o => s == null || !s.HiddenObjects.Contains(o)).GroupBy(o => o.Category)) {
                var col = CategoryColors[grp.Key];
                var shapes = new List<float>();
                var markers = new List<float>();
                foreach (var o in grp) {
                    ShapeLines(shapes, o, col);
                    if (!ch.ObjectModels.ContainsKey(o)) {
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

        public void SetSelection(LevelObject o, DrawItem item = null) {
            Selected = o;
            SelectedItem = item ?? (o == null ? null : Items.FirstOrDefault(i => i.Object == o));
            _selectionLines?.Dispose();
            _selectionLines = null;
            var d = new List<float>();
            var y = new Vector4(1, 1, 0, 1);
            if (SelectedItem != null) AabbLines(d, SelectedItem.WorldMin, SelectedItem.WorldMax, y);
            if (o != null) {
                ShapeLines(d, o, y);
                Cross(d, o.Position, 120, y);
            }
            if (d.Count > 0) _selectionLines = GpuLines.Create(d);

            _selectionFill?.Dispose();
            _selectionFill = null;
            if (o != null && SelectedItem == null) {
                var v = new List<float>(); var ix = new List<uint>();
                foreach (var sh in o.Shapes) {
                    if (sh.Kind == ShapeKind.Box) FillBox(v, ix, sh.Center, sh.Rotation, sh.Size);
                    else if (sh.Kind == ShapeKind.Sphere) FillSphere(v, ix, sh.Center, sh.Radius);
                    else if (sh.Kind == ShapeKind.Polygon && sh.Points.Count >= 3) FillPolygon(v, ix, sh.Points);
                }
                if (o.Shapes.Count == 0 || !o.Shapes.Any(x => x.Kind == ShapeKind.Box || x.Kind == ShapeKind.Sphere))
                    FillBox(v, ix, o.Position, o.Rotation, new Vector3(90));
                if (ix.Count > 0) _selectionFill = GpuMesh.Create(v.ToArray(), ix.ToArray());
            }
        }

        private static void Vert(List<float> v, Vector3 p, Vector3 n) {
            v.Add(p.X); v.Add(p.Y); v.Add(p.Z); v.Add(n.X); v.Add(n.Y); v.Add(n.Z);
            v.Add(0); v.Add(0); v.Add(1); v.Add(1); v.Add(1); v.Add(1);
        }

        private static void FillBox(List<float> v, List<uint> ix, Vector3 c, Vector3 rot, Vector3 size) {
            var m = LevelObject.RotationPYR(rot);
            var h = size * 0.5f;
            uint b = (uint)(v.Count / GpuMesh.Stride);
            for (int i = 0; i < 8; i++) {
                var l = new Vector3((i & 1) != 0 ? h.X : -h.X, (i & 2) != 0 ? h.Y : -h.Y, (i & 4) != 0 ? h.Z : -h.Z);
                Vert(v, c + Vector3.TransformNormal(l, m), Vector3.UnitY);
            }
            int[] f = { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 };
            foreach (var k in f) ix.Add(b + (uint)k);
        }

        private static void FillSphere(List<float> v, List<uint> ix, Vector3 c, float r) {
            const int S = 16, R = 10;
            uint b = (uint)(v.Count / GpuMesh.Stride);
            for (int y = 0; y <= R; y++)
                for (int x = 0; x <= S; x++) {
                    float th = y * MathF.PI / R, ph = x * 2 * MathF.PI / S;
                    var n = new Vector3(MathF.Sin(th) * MathF.Cos(ph), MathF.Cos(th), MathF.Sin(th) * MathF.Sin(ph));
                    Vert(v, c + n * r, n);
                }
            for (int y = 0; y < R; y++)
                for (int x = 0; x < S; x++) {
                    uint a = b + (uint)(y * (S + 1) + x), d = a + S + 1;
                    ix.Add(a); ix.Add(d); ix.Add(a + 1); ix.Add(a + 1); ix.Add(d); ix.Add(d + 1);
                }
        }

        private static void FillPolygon(List<float> v, List<uint> ix, List<Vector3> pts) {
            uint b = (uint)(v.Count / GpuMesh.Stride);
            foreach (var p in pts) Vert(v, p, Vector3.UnitY);
            for (int i = 1; i + 1 < pts.Count; i++) { ix.Add(b); ix.Add(b + (uint)i); ix.Add(b + (uint)i + 1); }
        }

        public bool Visible(DrawItem it, ViewSettings s) {
            if (Anim != null && Anim.Item == it && !s.AnimatedMesh) return false;
            string ch = it.Chunk.Def.Name;
            switch (it.Kind) {
                case ItemKind.Overlay: return s.OverlayVisible(ch, it.Name);
                case ItemKind.EnvDome: return s.DomeVisible(ch);
                case ItemKind.Object: return s.ObjectVisible(it.Object);
            }
            return !s.HiddenChunks.Contains(ch);
        }

        // ---- frustum culling (planes extracted from the row-vector view-projection matrix)
        private readonly Vector4[] _planes = new Vector4[6];
        private readonly List<DrawItem> _visible = new List<DrawItem>();

        private void ExtractFrustum(Matrix4x4 m) {
            var c1 = new Vector4(m.M11, m.M21, m.M31, m.M41);
            var c2 = new Vector4(m.M12, m.M22, m.M32, m.M42);
            var c3 = new Vector4(m.M13, m.M23, m.M33, m.M43);
            var c4 = new Vector4(m.M14, m.M24, m.M34, m.M44);
            _planes[0] = c4 + c1; _planes[1] = c4 - c1;
            _planes[2] = c4 + c2; _planes[3] = c4 - c2;
            _planes[4] = c4 + c3; _planes[5] = c4 - c3;
        }

        private bool InFrustum(Vector3 min, Vector3 max) {
            for (int i = 0; i < 6; i++) {
                var p = _planes[i];
                // farthest corner along the plane normal
                float x = p.X >= 0 ? max.X : min.X, y = p.Y >= 0 ? max.Y : min.Y, z = p.Z >= 0 ? max.Z : min.Z;
                if (p.X * x + p.Y * y + p.Z * z + p.W < 0) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- drawing

        /// <summary>Set non-null to capture which labelled stage of Render() first raised a GL error (debugging the model-viewer FBO path).</summary>
        public Action<string> DebugStageError;

        private void CheckGl(string stage) {
            if (DebugStageError == null) return;
            var err = GL.GetError();
            if (err != ErrorCode.NoError) DebugStageError(stage + ": " + err);
        }

        public void Render(Camera cam, int width, int height, ViewSettings s, Vector3 clearColor, Vector3 fogColor, Vector2 fogRange) {
            if (DebugStageError != null) { while (GL.GetError() != ErrorCode.NoError) { } } // drain any stale/pre-existing error so later checks aren't misattributed
            InitGl();
            CheckGl("InitGl");
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
            GL.Disable(EnableCap.StencilTest);
            GL.PolygonMode(TriangleFace.FrontAndBack, s.Wireframe ? PolygonMode.Line : PolygonMode.Fill);
            CheckGl("State setup");

            _mesh.Use();
            _mesh.Set("uViewProj", viewProj);
            _mesh.Set("uTex", 0);
            _mesh.Set("uLightDir", Vector3.Normalize(new Vector3(0.4f, 1f, 0.3f)));
            _mesh.Set("uFog", s.Fog);
            _mesh.Set("uFogColor", fogColor);
            _mesh.Set("uFogRange", fogRange);
            _mesh.Set("uFogMax", 0.7f);
            _mesh.Set("uEye", s.MirrorX ? cam.Position * new Vector3(-1, 1, 1) : cam.Position);
            _mesh.Set("uBrightness", s.Brightness);
            GL.ActiveTexture(TextureUnit.Texture0);

            // 1. Environment dome, centred on the camera, behind everything.
            if (s.EnvDome && Scene != null) {
                GL.Disable(EnableCap.DepthTest);
                GL.DepthMask(false);
                GL.Disable(EnableCap.CullFace);
                var eye = s.MirrorX ? cam.Position * new Vector3(-1, 1, 1) : cam.Position;
                foreach (var it in Items) {
                    if (it.Kind != ItemKind.EnvDome || !Visible(it, s)) continue;
                    var c = (it.Model.Min + it.Model.Max) * 0.5f;
                    var w = Matrix4x4.CreateTranslation(eye - new Vector3(c.X, 0, c.Z));
                    DrawModel(it, w, s, time, pass: 0, forceNoFog: true);
                    GL.Enable(EnableCap.Blend);
                    DrawModel(it, w, s, time, pass: 1, forceNoFog: true);
                    GL.Disable(EnableCap.Blend);
                    GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                }
                GL.Enable(EnableCap.DepthTest);
                GL.DepthMask(true);
            }
            CheckGl("EnvDome pass");

            if (s.CullBackfaces) { GL.Enable(EnableCap.CullFace); GL.CullFace(TriangleFace.Back); }
            else GL.Disable(EnableCap.CullFace);

            // 2. Opaque + alpha-tested geometry (visibility + frustum culled once per frame).
            ExtractFrustum(viewProj);
            _visible.Clear();
            foreach (var it in Items)
                if (it.Kind != ItemKind.EnvDome && Visible(it, s) && InFrustum(it.WorldMin, it.WorldMax)) _visible.Add(it);
            GL.Disable(EnableCap.Blend);
            int opaqueIndex = 0;
            foreach (var it in _visible) {
                DrawModel(it, it.World, s, time, pass: 0);
                if (DebugStageError != null) CheckGl($"Opaque pass, item {opaqueIndex} ({it.Name})");
                opaqueIndex++;
            }

            // 3. Collision.
            if (s.GroundCollision || s.ObjectCollision) {
                GL.Enable(EnableCap.PolygonOffsetFill);
                GL.PolygonOffset(-1f, -2f);
                _mesh.Set("uUseTex", 0);
                _mesh.Set("uUseVCol", 1);
                _mesh.Set("uLighting", 1);
                _mesh.Set("uAlphaRef", 0f);
                _mesh.Set("uUVOffset", Vector2.Zero);
                _mesh.Set("uTint", Vector4.One);
                _mesh.Set("uBrightness", 1f);
                foreach (var collision in Collisions) {
                    bool shown = collision.Object == null
                        ? s.GroundVisible(collision.Chunk.Def.Name, collision.Name)
                        : s.ObjectCollisionVisible(collision.Object);
                    if (!shown || !InFrustum(collision.WorldMin, collision.WorldMax)) continue;
                    _mesh.Set("uModel", collision.World);
                    collision.Mesh.Draw();
                    LastDrawnTriangles += collision.Triangles;
                }
                _mesh.Set("uBrightness", s.Brightness);
                GL.Disable(EnableCap.PolygonOffsetFill);
            }
            CheckGl("Collision pass");

            // 4. Transparent geometry (no sorting; depth writes off).
            if (s.Transparency) {
                GL.Enable(EnableCap.Blend);
                GL.DepthMask(false);
                foreach (var it in _visible) DrawModel(it, it.World, s, time, pass: 1);
                GL.DepthMask(true);
                GL.Disable(EnableCap.Blend);
            }
            CheckGl("Transparent pass");

            // 4b. Selection highlight on the selected model itself.
            if (SelectedItem != null && Visible(SelectedItem, s))
                DrawSelectionHighlight(SelectedItem, s);
            CheckGl("Selection highlight");

            // 5. Lines.
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
            _lines.Use();
            _lines.Set("uViewProj", viewProj);
            _lines.Set("uModel", Matrix4x4.Identity);
            _lines.Set("uTint", Vector4.One);
            if (Scene == null || Items.Count == 0) _gridLines?.Draw();
            foreach (var entry in _shapeLines)
                if (s.CategoryBatchVisible(entry.Key.chunk, entry.Key.cat) && HelpersVisible(entry.Key.cat, s)) entry.Value.Draw();
            foreach (var entry in _markerLines)
                if (s.CategoryBatchVisible(entry.Key.chunk, entry.Key.cat) && HelpersVisible(entry.Key.cat, s)) entry.Value.Draw();
            if (_selectionFill != null) {
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
            CheckGl("Shapes/markers/selectionFill");
            DrawSkeleton(s);
            CheckGl("Skeleton");
            if (_selectionLines != null) {
                GL.Disable(EnableCap.DepthTest);
                float pl = 0.6f + 0.4f * MathF.Sin((float)(DateTime.Now - _start).TotalSeconds * 4f);
                _lines.Set("uTint", new Vector4(1, 1, pl, 1));
                _selectionLines.Draw();
                GL.Enable(EnableCap.DepthTest);
            }
            CheckGl("Selection lines");
            GL.BindVertexArray(0);
        }

        /// <summary>pass 0 = opaque/alpha-test parts, pass 1 = blended parts.</summary>
        private void DrawSelectionHighlight(DrawItem it, ViewSettings s) {
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

            void Parts() {
                foreach (var p in it.Model.Parts) {
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

        private void DrawModel(DrawItem item, Matrix4x4 world, ViewSettings s, float time, int pass, bool forceNoFog = false) {
            var m = item.Model;
            bool prelit = item.Kind != ItemKind.Object && s.VertexColors;
            bool modelSet = false;
            foreach (var p in m.Parts) {
                if (p.IsCollisionComponent && !s.ModelCollisionComponents) continue;
                if (p.IsFragment && !s.Fragments) continue;
                bool transparent = s.Transparency && p.Transparent;
                if ((pass == 1) != transparent) continue;
                if (!modelSet) {
                    _mesh.Set("uModel", world);
                    modelSet = true;
                }
                var mat = p.Material;
                bool useTex = s.Textures && p.Texture != 0;
                _mesh.Set("uUseTex", useTex);
                _mesh.Set("uUseVCol", s.VertexColors);
                _mesh.Set("uLighting", (s.Lighting && !prelit) || p.IsCollisionComponent);
                float aref = 0f;
                if (useTex && (mat.AlphaTest || p.TextureIsCutout || (p.TextureHasAlpha && !transparent)))
                    aref = mat.AlphaRef > 0 ? mat.AlphaRef : 0.5f;
                if (pass == 1) aref = 0.01f;
                _mesh.Set("uAlphaRef", aref);
                var tint = p.IsCollisionComponent ? new Vector4(0.4f, 1f, 0.4f, 1f) : item.Tint;
                _mesh.Set("uTint", tint);
                var off = mat.Scrolls ? new Vector2(Frac(mat.ScrollU * time), Frac(mat.ScrollV * time)) : Vector2.Zero;
                _mesh.Set("uUVOffset", off);
                if (forceNoFog) _mesh.Set("uFog", false);
                if (pass == 1) {
                    if (mat.Blend == BlendMode.Additive) GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                    else if (mat.Blend == BlendMode.Subtractive) {
                        GL.BlendEquation(BlendEquationMode.FuncReverseSubtract);
                        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                    }
                    else GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                }
                GL.BindTexture(TextureTarget.Texture2D, useTex ? p.Texture : 0);
                if (DebugStageError != null) CheckGl($"  BindTexture (tex={p.Texture}, useTex={useTex})");
                p.Mesh.Draw();
                if (DebugStageError != null) CheckGl($"  Mesh.Draw (vao={p.Mesh.Vao}, idx={p.Mesh.IndexCount})");
                LastDrawnTriangles += p.Triangles;
                if (pass == 1 && mat.Blend == BlendMode.Subtractive) GL.BlendEquation(BlendEquationMode.FuncAdd);
                if (forceNoFog) _mesh.Set("uFog", s.Fog);
            }
        }

        private static float Frac(float v) => v - MathF.Floor(v);

        // ---------------------------------------------------------------- picking

        private Vector3[] PickTriangles(GpuModel g) {
            if (_pickTris.TryGetValue(g, out var t)) return t;
            var list = new List<Vector3>();
            var model = g.Source.Model;
            foreach (var mesh in model.Meshes) {
                var comp = model.Components[mesh.ComponentIndex];
                if (comp.IsCollision || comp.IsFragment) continue;
                foreach (var s in mesh.Strips)
                    for (int i = 0; i + 2 < s.Vertices.Count; i++) {
                        list.Add(s.Vertices[i].Position);
                        list.Add(s.Vertices[i + 1].Position);
                        list.Add(s.Vertices[i + 2].Position);
                    }
            }
            t = list.ToArray();
            _pickTris[g] = t;
            return t;
        }

        public PickResult Pick(Vector3 origin, Vector3 dir, ViewSettings s) {
            if (s.MirrorX) {
                origin *= new Vector3(-1, 1, 1);
                dir *= new Vector3(-1, 1, 1);
            }
            PickResult best = null;
            void Consider(float t, DrawItem it, LevelObject o, string desc) {
                if (t < 0 || (best != null && t >= best.Distance)) return;
                best = new PickResult { Distance = t, Item = it, Object = o, Point = origin + dir * t, Description = desc };
            }

            foreach (var it in Items) {
                if (it.Kind == ItemKind.EnvDome || !Visible(it, s)) continue;
                if (!RayAabb(origin, dir, it.WorldMin, it.WorldMax, out float tb) || (best != null && tb > best.Distance)) continue;
                if (!Matrix4x4.Invert(it.World, out var inv)) continue;
                var lo = Vector3.Transform(origin, inv);
                var ld = Vector3.TransformNormal(dir, inv);
                var tris = PickTriangles(it.Model);
                float bestLocal = float.MaxValue;
                for (int i = 0; i < tris.Length; i += 3)
                    if (RayTri(lo, ld, tris[i], tris[i + 1], tris[i + 2], out float tl) && tl < bestLocal) bestLocal = tl;
                if (bestLocal < float.MaxValue) {
                    var hit = Vector3.Transform(lo + ld * bestLocal, it.World);
                    Consider(Vector3.Distance(origin, hit), it, it.Object, it.Kind == ItemKind.Overlay ? "overlay " + it.Name : it.Name);
                }
            }

            if (Scene != null) {
                foreach (var chunk in Scene.Chunks) {
                    if (s.HiddenChunks.Contains(chunk.Def.Name)) continue;
                    foreach (var levelObject in chunk.Objects) {
                        if (!s.ObjectVisible(levelObject) || !HelpersVisible(levelObject.Category, s)) continue;
                        if (!chunk.ObjectModels.ContainsKey(levelObject) && RaySphere(origin, dir, levelObject.Position, 60, out float markerDistance))
                            Consider(Math.Max(0f, markerDistance - HelperPickBias), null, levelObject, levelObject.DisplayName);
                        var segments = PickSegments(levelObject);
                        for (int index = 0; index + 1 < segments.Length; index += 2) {
                            if (RaySegmentHit(origin, dir, segments[index], segments[index + 1], out float lineDistance))
                                Consider(Math.Max(0f, lineDistance - HelperPickBias), null, levelObject, levelObject.DisplayName);
                        }
                    }
                }
            }
            return best;
        }

        /// <summary>Lines and markers win over a surface they sit on, as long as they are within this distance of it.</summary>
        private const float HelperPickBias = 100f;

        private readonly Dictionary<LevelObject, Vector3[]> pickSegments = new Dictionary<LevelObject, Vector3[]>();

        /// <summary>Endpoint pairs of the lines drawn for an object's shapes.</summary>
        private Vector3[] PickSegments(LevelObject levelObject) {
            if (pickSegments.TryGetValue(levelObject, out var segments)) return segments;
            var lineData = new List<float>();
            ShapeLines(lineData, levelObject, Vector4.One);
            segments = new Vector3[lineData.Count / 7];
            for (int index = 0; index < segments.Length; index++)
                segments[index] = new Vector3(lineData[index * 7], lineData[index * 7 + 1], lineData[index * 7 + 2]);
            pickSegments[levelObject] = segments;
            return segments;
        }

        /// <summary>True when the ray passes within a few pixels' worth of a line segment; distance is along the ray.</summary>
        private static bool RaySegmentHit(Vector3 origin, Vector3 direction, Vector3 start, Vector3 end, out float distance) {
            distance = 0;
            var segment = end - start;
            var offset = origin - start;
            float segmentLengthSquared = Vector3.Dot(segment, segment);
            float alignment = Vector3.Dot(direction, segment);
            float rayOffset = Vector3.Dot(direction, offset);
            float segmentOffset = Vector3.Dot(segment, offset);
            float denominator = segmentLengthSquared - alignment * alignment;
            float along = denominator > 1e-6f ? Math.Clamp((segmentOffset - alignment * rayOffset) / denominator, 0f, 1f) : 0f;
            var closestOnSegment = start + segment * along;
            distance = Vector3.Dot(closestOnSegment - origin, direction);
            if (distance <= 0) return false;
            var closestOnRay = origin + direction * distance;
            float tolerance = Math.Max(6f, distance * 0.008f);
            return Vector3.DistanceSquared(closestOnRay, closestOnSegment) <= tolerance * tolerance;
        }

        /// <summary>Prop markers and shapes are optional; every other category always shows its helpers.</summary>
        private static bool HelpersVisible(ObjectCategory category, ViewSettings s) => category != ObjectCategory.Prop || s.PropMarkers;

        private static bool RayAabb(Vector3 o, Vector3 d, Vector3 min, Vector3 max, out float t) {
            float tmin = 0, tmax = float.MaxValue;
            t = 0;
            for (int i = 0; i < 3; i++) {
                float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z;
                float di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
                float mn = i == 0 ? min.X : i == 1 ? min.Y : min.Z;
                float mx = i == 0 ? max.X : i == 1 ? max.Y : max.Z;
                if (MathF.Abs(di) < 1e-9f) {
                    if (oi < mn || oi > mx) return false;
                }
                else {
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

        private static bool RayTri(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t) {
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

        private static bool RaySphere(Vector3 o, Vector3 d, Vector3 c, float r, out float t) {
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

        // ---------------------------------------------------------------- screenshots

        public static byte[] ReadPixels(int w, int h) {
            var px = new byte[w * h * 4];
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.ReadPixels(0, 0, w, h, PixelFormat.Rgba, PixelType.UnsignedByte, px);
            // flip rows (GL origin is bottom-left)
            var row = new byte[w * 4];
            for (int y = 0; y < h / 2; y++) {
                System.Buffer.BlockCopy(px, y * w * 4, row, 0, w * 4);
                System.Buffer.BlockCopy(px, (h - 1 - y) * w * 4, px, y * w * 4, w * 4);
                System.Buffer.BlockCopy(row, 0, px, (h - 1 - y) * w * 4, w * 4);
            }
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            return px;
        }
        private void DisposeSceneResources() {
            ClearAnimation();
            foreach (var m in _models.Values) m.Dispose();
            _models.Clear();
            _pickTris.Clear();
            pickSegments.Clear();
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

        public void Dispose() {
            DisposeSceneResources();
            _skeletonLines?.Dispose();
            _skeletonLines = null;
            _gridLines?.Dispose();
            _mesh?.Dispose();
            _lines?.Dispose();
        }
    }
}