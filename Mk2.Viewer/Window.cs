using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Hexa.NET.ImGui;
using Mk2.Formats;
using Mk2.Game;
using Mk2.Viewer.Frames;
using Mk2.Viewer.ImGuiUi;
using Mk2.Viewer.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vec2 = System.Numerics.Vector2;
using Vec3 = System.Numerics.Vector3;

namespace Mk2.Viewer {
    public sealed class Window : GameWindow {
        private readonly string[] _args;

        private GameData _game;
        private ModelDb _db;
        private List<LevelDef> _levels = new List<LevelDef>();
        private LevelScene _scene;
        private AssetCache _assets;

        private readonly SceneRenderer _renderer = new SceneRenderer();
        private readonly Camera _cam = new Camera();
        private readonly ViewSettings _vs = new ViewSettings();
        private bool _glReady;
        private Vec3 _clear = new Vec3(0.18f, 0.2f, 0.26f);
        private Vec3 _fogColor = new Vec3(0.5f, 0.5f, 0.6f);
        private Vec2 _fogRange = new Vec2(10000, 100000);

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastTick;
        private bool _animate;

        private List<string> _allModels = new List<string>();
        private List<string> _allAnims = new List<string>();
        private List<string> _allSkeletons = new List<string>();
        private string _status = "Ready";
        private readonly StringBuilder _log = new StringBuilder();
        private string _propsText = "";
        private bool _vsync;

        private MainFrame _ui;

        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _mainThreadQueue = new System.Collections.Concurrent.ConcurrentQueue<Action>();

        private void RunOnMainThread(Action a) => _mainThreadQueue.Enqueue(a);

        private void DrainMainThreadQueue() {
            while (_mainThreadQueue.TryDequeue(out var a)) {
                try { a(); }
                catch (Exception ex) { Log("Main-thread task failed: " + ex); }
            }
        }

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "purpleEdit", "settings.txt");

        public Window(string[] args) : base(GameWindowSettings.Default,
            new NativeWindowSettings {
                ClientSize = new Vector2i(1600, 950),
                APIVersion = new Version(3, 3),
                Flags = ContextFlags.ForwardCompatible,
                Profile = ContextProfile.Core,
                Vsync = VSyncMode.On,
                Title = "purpleEdit v1.10",
            }) {
            _args = args;
        }

        protected override void OnLoad() {
            base.OnLoad();

            _renderer.InitGl();
            _glReady = true;
            _vsync = true;

            _ui = BuildImGuiUi();

            string root = _args.Length > 0 ? _args[0] : LoadSetting("root");
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                _ = OpenGameFolder(root);
            else
                Log("Use the Browser frame's \"Open game folder…\" option and pick the folder with your extracted files / .BPK / .RKV.");
        }

        protected override void OnResize(ResizeEventArgs e) {
            base.OnResize(e);
            GL.Viewport(0, 0, ClientSize.X, ClientSize.Y);
        }

        protected override void OnRenderFrame(FrameEventArgs e) {
            base.OnRenderFrame(e);
            if (!_glReady) return;

            DrainMainThreadQueue();

            try {
                AnimationTick();
            }
            catch (Exception ex) {
                _glReady = false;
                Log("Render error: " + ex);
            }

            GL.ClearColor(_clear.X, _clear.Y, _clear.Z, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            if (_ui != null) {
                _ui.ClearColor = _clear;
                _ui.FogColor = _fogColor;
                _ui.FogRange = _fogRange;
            }

            _ui?.RenderUi();

            SwapBuffers();
        }

        protected override void OnUnload() {
            _ui?.Dispose();
            _renderer.Dispose();
            base.OnUnload();
        }

        protected override void OnTextInput(TextInputEventArgs e) { }

        private void PickRay(Vec3 origin, Vec3 dir) {
            if (_scene == null) return;
            var hit = _renderer.Pick(origin, dir, _vs);
            if (hit == null) { Select(null, null); SetStatus("Nothing under cursor"); return; }
            Select(hit.Object, hit.Item);
            SetStatus($"Picked {hit.Description} at {V(hit.Point)} (distance {hit.Distance:0})");
        }
        private MainFrame BuildImGuiUi() {
            var ui = new MainFrame(this) {
                ViewSettings = _vs,
                Camera = _cam,
                Renderer = _renderer,
                Redraw = () => { },

                GetLevels = () => _levels,
                GetPacks = () => _game?.ChunkPackNames ?? Enumerable.Empty<string>(),
                GetModels = () => _allModels,
                GetAnimations = () => _allAnims,
                GetSkeletons = () => _allSkeletons,
                GetGameData = () => _game,
                GetAnimationForIndexing = name => _assets?.GetAnimation(name),
                GetScene = () => _scene,
                OnSelect = (o, item) => Select(o, item),
                OnFocusSelection = FocusSelection,
                OnPickRay = (o, d) => PickRay(o, d),
                OnVisibilityChanged = () => { if (_glReady) _renderer.RebuildLines(_vs); },
                RunOnMainThread = RunOnMainThread,

                GetPropertiesText = () => _propsText,
                GetLogText = () => _log.ToString(),
                GetStatusText = () => _status,
            };

            ui.OnLoadLevel = lv => _ = LoadLevel(lv, null);
            ui.OnLoadPack = pack => _ = LoadPack(pack);
            ui.OnLoadModel = model => _ = LoadSingleModel(model);
            ui.OnLoadAnimation = anim => _ = LoadAnimation(anim, null, skeletonMode: false);
            ui.OnLoadSkeleton = model => _ = LoadAnimation(null, model, skeletonMode: true);
            ui.OnOpenGameFolder = folder => _ = OpenGameFolder(folder);

            return ui;
        }
        private async Task OpenGameFolder(string root) {
            SetStatus("Indexing " + root + " …");
            try {
                var game = await Task.Run(() => new GameData(root, SetStatus));
                _game = game;
                foreach (var l in game.Log) Log(l);
                _db = new ModelDb(game.ReadBni("global.model.bni"));
                if (!_db.Has("default")) Log("global.model.bni not found — object classes will be matched to models by name only.");
                _levels = LevelLoader.LoadLevelList(game);
                if (_levels.Count == 0) Log("levels.ini.bni not found — use the Packs / Models tabs.");
                _assets = new AssetCache(game);
                SaveSetting("root", root);
                _allModels = game.AllNames(".mdl").Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                FillAnimationLists();
                SetStatus($"{root}: {_levels.Count} levels, {game.ChunkPackNames.Count()} chunk packs, {_allModels.Count} models");
            }
            catch (Exception ex) {
                Log("Failed to open game folder: " + ex);
            }
        }

        private void FillAnimationLists() {
            if (_game == null) return;
            _allAnims = AnimationLookup.AllAnimations(_game);
            _allSkeletons = AnimationLookup.ModelsWithSkeletons(_game);
        }

        private async Task LoadLevel(LevelDef lv, HashSet<string> onlyChunks) {
            if (_game == null) return;
            SetStatus("Loading " + lv.Name + " …");
            var sw = Stopwatch.StartNew();
            try {
                _assets = new AssetCache(_game);
                var cache = _assets;
                var scene = await Task.Run(() => LevelLoader.Load(_game, lv, _db, cache, SetStatus, onlyChunks));
                RunOnMainThread(() => {
                    ShowSceneOnMainThread(scene, sw);
                    if (lv.FogColor != null && lv.FogColor.Length >= 3)
                        _fogColor = Vec3.Min(new Vec3(lv.FogColor[0], lv.FogColor[1], lv.FogColor[2]) * 0.5f, Vec3.One);
                    if (lv.FogFar > 0) _fogRange = new Vec2(lv.FogNear, lv.FogFar);
                    var start = _renderer.Items.Where(i => i.Kind == ItemKind.Overlay && string.Equals(i.Chunk.Def.Name, lv.StartChunk, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (onlyChunks == null && start.Count > 0) {
                        var mn = start.Aggregate(new Vec3(float.MaxValue), (a, i) => Vec3.Min(a, i.WorldMin));
                        var mx = start.Aggregate(new Vec3(float.MinValue), (a, i) => Vec3.Max(a, i.WorldMax));
                        FrameWorld(mn, mx);
                    }
                });
            }
            catch (Exception ex) { Log("Load failed: " + ex); }
        }

        private async Task LoadPack(string chunk) {
            if (_game == null) return;
            var sw = Stopwatch.StartNew();
            try {
                _assets = new AssetCache(_game);
                var cache = _assets;
                var scene = await Task.Run(() => {
                    var sc = LevelLoader.LoadPack(_game, chunk, cache);
                    var lc3 = _game.ReadBni(chunk + ".lc3.bni", new[] { _game.ChunkPack(chunk) });
                    var ch = sc.Chunks[0];
                    if (lc3 != null) {
                        ch.Layout = lc3;
                        int i = 0;
                        foreach (var s in lc3.Sections) ch.Objects.Add(LevelObject.FromSection(s, chunk, i++, _db));
                    }
                    ArrangeLooseModels(ch);
                    return sc;
                });
                RunOnMainThread(() => {
                    ShowSceneOnMainThread(scene, sw);
                    FrameWorld(_renderer.SceneMin, _renderer.SceneMax);
                });
            }
            catch (Exception ex) { Log("Pack load failed: " + ex); }
        }

        /// <summary>Chunk packs mix world-space level geometry with props modelled around the origin; lay loose props on a grid.</summary>
        private static void ArrangeLooseModels(SceneChunk ch) {
            var world = new List<(OverlayRef, LoadedModel)>();
            var loose = new List<LoadedModel>();
            foreach (var (ov, m) in ch.Overlays) {
                var c = m.Model.BoundsMin + m.Model.BoundsSize * 0.5f;
                float r = Math.Max(m.Model.BoundsSize.Length() * 0.5f, 1f);
                if (c.Length() > 3 * r && c.Length() > 3000) world.Add((ov, m)); else loose.Add(m);
            }
            ch.Overlays = world;
            var wmin = world.Count > 0 ? world.Select(w => w.Item2.Model.BoundsMin).Aggregate(Vec3.Min) : Vec3.Zero;
            float x = wmin.X, z = (world.Count > 0 ? world.Select(w => w.Item2.Model.BoundsMax).Aggregate(Vec3.Max).Z : 0) + 2000;
            float rowDepth = 0;
            float rowStart = x;
            float maxRow = Math.Max(20000, world.Count > 0 ? world.Select(w => w.Item2.Model.BoundsMax).Aggregate(Vec3.Max).X - wmin.X : 20000);
            int idx = 0;
            foreach (var m in loose.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)) {
                var size = m.Model.BoundsSize;
                float w = Math.Max(size.X, 200) + 300;
                if (x - rowStart + w > maxRow) { x = rowStart; z += rowDepth + 300; rowDepth = 0; }
                var o = new LevelObject {
                    ClassName = m.Name,
                    ChunkName = ch.Def.Name,
                    Index = 100000 + idx++,
                    Section = new BniSection { Name = m.Name },
                    Position = new Vec3(x - m.Model.BoundsMin.X, wmin.Y, z - m.Model.BoundsMin.Z),
                    Category = ObjectCategory.Prop,
                };
                ch.Objects.Add(o);
                ch.ObjectModels[o] = m;
                x += w;
                rowDepth = Math.Max(rowDepth, Math.Max(size.Z, 200));
            }
        }

        private async Task LoadSingleModel(string name) {
            if (_game == null) return;
            var sw = Stopwatch.StartNew();
            _assets = new AssetCache(_game);
            var cache = _assets;
            var scene = await Task.Run(() => {
                var m = cache.GetModel(name, null);
                var def = new LevelDef { Name = name };
                var cd = new ChunkDef { Name = name };
                def.Chunks.Add(cd);
                var sc = new LevelScene { Def = def, Assets = cache };
                var ch = new SceneChunk { Def = cd };
                if (m != null) {
                    var o = new LevelObject { ClassName = name, ChunkName = name, Section = new BniSection { Name = name }, Category = ObjectCategory.Prop };
                    ch.Objects.Add(o);
                    ch.ObjectModels[o] = m;
                }
                else sc.Warnings.Add("Could not load model " + name);
                sc.Chunks.Add(ch);
                sc.Warnings.AddRange(cache.Warnings);
                return sc;
            });
            RunOnMainThread(() => {
                ShowSceneOnMainThread(scene, sw);
                FrameWorld(_renderer.SceneMin, _renderer.SceneMax, fromFront: true);
                var it = _renderer.Items.FirstOrDefault();
                if (it != null) Select(it.Object, it);
            });
        }

        private async Task LoadAnimation(string animName, string modelOverride, bool skeletonMode) {
            if (_game == null) return;
            var sw = Stopwatch.StartNew();
            try {
                _assets = new AssetCache(_game);
                var cache = _assets;
                var game = _game;
                var r = await Task.Run(() => {
                    var anim = cache.GetAnimation(animName);
                    string modelName = anim == null ? null : modelOverride ?? AnimationLookup.FindModelFor(game, animName, anim);
                    var set = anim == null ? null : cache.GetAnimSet(animName, anim);
                    var m = modelName != null ? cache.GetModel(modelName, null) : null;
                    var def = new LevelDef { Name = animName ?? modelOverride };
                    var cd = new ChunkDef { Name = animName ?? modelOverride };
                    def.Chunks.Add(cd);
                    var sc = new LevelScene { Def = def, Assets = cache };
                    var ch = new SceneChunk { Def = cd };
                    if (m != null) {
                        var o = new LevelObject { ClassName = m.Name, ChunkName = cd.Name, Section = new BniSection { Name = m.Name }, Category = ObjectCategory.Prop };
                        ch.Objects.Add(o);
                        ch.ObjectModels[o] = m;
                    }
                    sc.Chunks.Add(ch);
                    if (anim == null && animName != null) sc.Warnings.Add($"Could not load {animName}.anm");
                    else if (anim != null && modelName == null) sc.Warnings.Add($"{animName}: no matching model found — showing the skeleton only.");
                    else if (anim != null && m == null) sc.Warnings.Add($"Could not load model {modelName}");
                    sc.Warnings.AddRange(cache.Warnings);
                    return (scene: sc, anim, set, model: m?.Name);
                });

                RunOnMainThread(() => {
                    ShowSceneOnMainThread(r.scene, sw);
                    _renderer.SetAnimation(_renderer.Items.FirstOrDefault(i => i.Kind == ItemKind.Object), r.anim);
                    FrameWorld(_renderer.SceneMin, _renderer.SceneMax, fromFront: true);
                    _skeletonMode = skeletonMode;
                    SetStatus($"{animName ?? modelOverride}: {(r.anim == null ? "failed" : $"{r.anim.Bones.Count} bones, {r.anim.FrameCount} frames, {r.set?.Clips.Count ?? 0} clips")}{(r.model != null ? " on " + r.model : "")} — {sw.ElapsedMilliseconds} ms");
                });
            }
            catch (Exception ex) { Log("Animation load failed: " + ex); }
        }

        private bool _skeletonMode;
        private void TogglePlay() { /* TODO */ }
        private void AnimationTick() { }

        private void ShowScene(LevelScene scene, Stopwatch sw) => RunOnMainThread(() => ShowSceneOnMainThread(scene, sw));

        private void ShowSceneOnMainThread(LevelScene scene, Stopwatch sw) {
            _scene = scene;
            _renderer.Load(scene);
            _vs.ClearSceneVisibility();
            int tris = _renderer.Items.Sum(i => i.Model.Parts.Sum(p => p.Triangles));
            int objs = scene.AllObjects.Count();
            SetStatus($"{scene.Def.Name}: {scene.Chunks.Count} chunks, {objs} objects, {_renderer.Items.Count} draw items, {tris:N0} triangles, {_renderer.Collisions.Count} collision meshes — {sw.ElapsedMilliseconds} ms");
            Log($"Loaded {scene.Def.Name} in {sw.ElapsedMilliseconds} ms");
            foreach (var w in scene.Warnings.Distinct()) Log("  warning: " + w);
            Select(null, null);
        }
        private void Select(LevelObject o, DrawItem item) {
            _renderer.SetSelection(o, item);
            ShowProperties(o, item ?? _renderer.SelectedItem);
        }

        private void ShowProperties(LevelObject o, DrawItem item) {
            var sb = new StringBuilder();
            if (o == null && item == null) {
                if (_scene != null) {
                    sb.AppendLine(_scene.Def.Name);
                    sb.AppendLine(new string('=', _scene.Def.Name.Length));
                    foreach (var ch in _scene.Chunks)
                        sb.AppendLine($"{ch.Def.Name,-24} {ch.Overlays.Count,3} overlays  {ch.Objects.Count,5} objects  {ch.Grounds.Count,3} grounds");
                }
                _propsText = sb.ToString();
                return;
            }
            if (o != null) {
                sb.AppendLine(o.DisplayName);
                sb.AppendLine(new string('=', Math.Min(o.DisplayName.Length, 80)));
                sb.AppendLine($"chunk     {o.ChunkName}   (entry #{o.Index})");
                sb.AppendLine($"category  {o.Category}");
                sb.AppendLine($"position  {V(o.Position)}");
                sb.AppendLine($"rotation  {V(o.Rotation)} rad");
                sb.AppendLine($"scale     {V(o.Scale)}");
            }
            if (item != null) {
                var lm = item.Model.Source;
                sb.AppendLine();
                sb.AppendLine($"MODEL {lm.Name}");
                sb.AppendLine($"  {lm.Model.VertexCount:N0} verts, {lm.Model.TriangleCount:N0} tris, {lm.Model.Meshes.Count} meshes");
                sb.AppendLine($"  bounds {V(lm.Model.BoundsMin)} .. {V(lm.Model.BoundsMax)}");
            }
            _propsText = sb.ToString();
        }
        private void FrameWorld(Vec3 min, Vec3 max, bool fromFront = false) {
            if (_vs.MirrorX) {
                var a = new Vec3(-max.X, min.Y, min.Z);
                var b = new Vec3(-min.X, max.Y, max.Z);
                min = a; max = b;
            }
            _cam.Frame(min, max, fromFront);
        }

        private void FocusSelection() {
            Vec3 min, max;
            if (_renderer.SelectedItem != null) { min = _renderer.SelectedItem.WorldMin; max = _renderer.SelectedItem.WorldMax; }
            else if (_renderer.Selected != null) {
                var o = _renderer.Selected;
                min = o.Position - new Vec3(300); max = o.Position + new Vec3(300);
            }
            else return;
            var spd = _cam.Speed;
            FrameWorld(min, max);
            _cam.Speed = Math.Min(spd, _cam.Speed * 2);
        }

        private static string V(Vec3 v) => $"{v.X:0.###} {v.Y:0.###} {v.Z:0.###}";
        private void SetStatus(string s) => _status = s;

        private void Log(string s) => _log.Append(s.Replace("\n", "\r\n")).Append("\r\n");

        private static string LoadSetting(string key) {
            try {
                if (!File.Exists(SettingsPath)) return null;
                foreach (var line in File.ReadAllLines(SettingsPath)) {
                    int i = line.IndexOf('=');
                    if (i > 0 && line.Substring(0, i) == key) return line.Substring(i + 1);
                }
            }
            catch { }
            return null;
        }

        private static void SaveSetting(string key, string value) {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                var lines = File.Exists(SettingsPath) ? File.ReadAllLines(SettingsPath).Where(l => !l.StartsWith(key + "=")).ToList() : new List<string>();
                lines.Add(key + "=" + value);
                File.WriteAllLines(SettingsPath, lines);
            }
            catch { }
        }
    }
}