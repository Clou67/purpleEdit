using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Hexa.NET.ImGui;
using OpenTK.Graphics.OpenGL4;
using Mk2.Formats;
using Mk2.Game;
using Mk2.Viewer.Frames;
using Mk2.Viewer.Rendering;
using Vec2 = System.Numerics.Vector2;
using Vec3 = System.Numerics.Vector3;

namespace Mk2.Viewer.ImGuiUi.Frames {
    public sealed class ModelViewerFrame : Frame {
        protected override string frameName { get; set; }

        private readonly SceneRenderer _renderer = new SceneRenderer();
        private readonly Camera _cam = new Camera();
        private readonly ViewSettings _vs = new ViewSettings { Skeleton = true, AnimatedMesh = true, Fog = false, EnvDome = false };
        private readonly RenderTarget _rt = new RenderTarget();
        private Vec3 _clear = new Vec3(0.14f, 0.15f, 0.18f);
        private bool _glInit;

        private GameData _game;
        private AssetCache _assets;
        private List<string> _allModels = new List<string>();
        private string _modelFilter = "";
        private string _currentModel;

        private Dictionary<string, List<string>> _animsForModel;
        private bool _animIndexBuilt;
        private bool _animIndexBuilding;

        private List<string> _currentAnims = new List<string>();
        private int _selectedAnimIndex = -1; // -1 = bind pose / no animation
        private Animation _anim;
        private string _loadedAnimName;

        private const float AnimFps = 30f;
        private AnimSet _animSet;
        private AnimClip _clip;
        private float _animFrame = -1;
        private float _animSpeed = 1f;
        private bool _playing;
        private bool _loop = true;
        private double _animLastTick;

        private bool _loading;
        private string _status = "No model loaded.";

        private bool _rmb, _mmb;
        private Vec2 _lastMouse;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastTick;

        public ModelViewerFrame(MainFrame owner) : base(owner, "Model Viewer") {
            _renderer.DebugStageError = stage => _status = "GL error in " + stage;
        }

        public override void Render(float deltaTime) {
            RefreshModelSource();

            ImGui.BeginChild("##mv_left", new Vec2(260, 0), ImGuiChildFlags.Borders);
            RenderModelList();
            ImGui.EndChild();

            ImGui.SameLine();

            ImGui.BeginChild("##mv_right", Vec2.Zero, ImGuiChildFlags.None);
            RenderAnimationPicker();
            ImGui.Separator();
            RenderViewport();
            ImGui.EndChild();
        }
        private void RenderModelList() {
            ImGui.TextDisabled("Click to view.");
            ImGui.InputTextWithHint("##mv_modelFilter", "Filter models…", ref _modelFilter, 128);
            ImGui.Separator();

            if (_game == null) {
                ImGui.TextDisabled("Open a game folder from the Browser frame first.");
                return;
            }

            if (ImGui.BeginChild("##mv_modelListScroll")) {
                foreach (var m in _allModels) {
                    if (!Matches(m, _modelFilter)) continue;
                    bool selected = string.Equals(m, _currentModel, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(m, selected) && !selected) _ = LoadModel(m);
                }
            }
            ImGui.EndChild();
        }
        private void RenderAnimationPicker() {
            if (_currentModel == null) {
                ImGui.TextDisabled(_status);
                return;
            }

            ImGui.Text(_currentModel);
            ImGui.SameLine();
            ImGui.TextDisabled(_loading ? "loading…" : _status);

            if (!_animIndexBuilt) {
                ImGui.TextDisabled(_animIndexBuilding ? "Scanning animations…" : "");
                return;
            }

            if (_currentAnims.Count == 0) {
                ImGui.TextDisabled("No animations found for this model.");
                return;
            }

            string preview = _selectedAnimIndex < 0 ? "(bind pose)" : _currentAnims[_selectedAnimIndex];
            ImGui.SetNextItemWidth(320);
            if (ImGui.BeginCombo("Animation", preview)) {
                bool bindSelected = _selectedAnimIndex < 0;
                if (ImGui.Selectable("(bind pose)", bindSelected)) SelectAnimation(-1);

                for (int i = 0; i < _currentAnims.Count; i++) {
                    bool sel = i == _selectedAnimIndex;
                    if (ImGui.Selectable(_currentAnims[i], sel)) SelectAnimation(i);
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            if (_anim != null) {
                ImGui.SameLine();
                ImGui.TextDisabled($"{_anim.Bones.Count} bones, {_anim.FrameCount} frames");
            }

            RenderClipControls();
        }

        private void SelectAnimation(int index) {
            if (index == _selectedAnimIndex) return;
            _selectedAnimIndex = index;
            string anim = index < 0 ? null : _currentAnims[index];
            _ = ApplyAnimation(anim);
        }

        private void RenderClipControls() {
            if (_animSet == null || _animSet.Clips.Count == 0) return;

            int clipIndex = _clip == null ? -1 : _animSet.Clips.IndexOf(_clip);
            string clipPreview = _clip?.Name ?? "(pick a clip)";
            ImGui.SetNextItemWidth(220);
            if (ImGui.BeginCombo("Clip", clipPreview)) {
                for (int i = 0; i < _animSet.Clips.Count; i++) {
                    var c = _animSet.Clips[i];
                    bool sel = i == clipIndex;
                    if (ImGui.Selectable(c.Name, sel)) SelectClip(c, play: true);
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            if (_clip == null) return;

            ImGui.SameLine();
            if (ImGui.Button(_playing ? "Pause" : "Play")) TogglePlay();
            ImGui.SameLine();
            if (ImGui.Button("|<")) StepFrame(-1);
            ImGui.SameLine();
            if (ImGui.Button(">|")) StepFrame(1);
            ImGui.SameLine();
            ImGui.Checkbox("Loop", ref _loop);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.DragFloat("Speed", ref _animSpeed, 0.05f, 0.1f, 4f, "%.2fx");

            int len = Math.Max(1, _clip.End - _clip.Start);
            int frame = (int)Math.Floor(_animFrame);
            float barVal = _animFrame;
            ImGui.SetNextItemWidth(320);
            if (ImGui.SliderFloat("Frame", ref barVal, _clip.Start, Math.Max(_clip.Start + 1, _clip.End))) SetAnimFrame(barVal);

            var ev = _clip.Events.Where(e => e.Frame == frame).Select(e => e.Name).ToList();
            ImGui.TextDisabled($"frame {frame - _clip.Start} / {len}   (timeline {frame} of {_anim?.FrameCount ?? 0})" +
                                (ev.Count > 0 ? "   event: " + string.Join(", ", ev) : ""));
        }

        private void SelectClip(AnimClip c, bool play) {
            if (_anim == null || c == null) return;
            _clip = c;
            _loop = c.Loop;
            _animFrame = c.Start;
            _playing = play && c.End > c.Start;
            _animLastTick = _clock.Elapsed.TotalSeconds;
        }

        private void TogglePlay() {
            if (_clip == null) return;
            if (!_playing && _animFrame >= _clip.End && !_loop) _animFrame = _clip.Start;
            _playing = !_playing && _clip.End > _clip.Start;
            _animLastTick = _clock.Elapsed.TotalSeconds;
        }

        private void StepFrame(int d) {
            if (_clip == null) return;
            _playing = false;
            float f = (float)Math.Round(_animFrame) + d;
            if (f > _clip.End) f = _loop ? _clip.Start : _clip.End;
            if (f < _clip.Start) f = _loop ? _clip.End : _clip.Start;
            SetAnimFrame(f);
        }

        private void SetAnimFrame(float f) {
            _animFrame = f;
        }

        private void RenderViewport() {
            var avail = ImGui.GetContentRegionAvail();
            int w = Math.Max(1, (int)avail.X);
            int h = Math.Max(1, (int)avail.Y - 4);
            if (h <= 1 || w <= 1) return;

            RenderSceneOffscreen(w, h);

            var cursor = ImGui.GetCursorScreenPos();
            unsafe {
                ImGui.Image(new ImTextureRef(default, (ulong)(uint)_rt.ColorTexture), new Vec2(w, h), new Vec2(0, 1), new Vec2(1, 0));
            }
            bool hovered = ImGui.IsItemHovered();
            HandleViewportInput(hovered, cursor, w, h);
        }

        private void RenderSceneOffscreen(int w, int h) {
            if (!_glInit) { _renderer.InitGl(); _glInit = true; }

            int[] prevViewport = new int[4];
            GL.GetInteger(GetPName.Viewport, prevViewport);

            _rt.RenderToTexture(w, h, () => {
                AnimationTick();
                _renderer.Render(_cam, w, h, _vs, _clear, _clear, new Vec2(100000, 200000));
            });

            GL.Viewport(prevViewport[0], prevViewport[1], prevViewport[2], prevViewport[3]);
        }

        private void HandleViewportInput(bool hovered, Vec2 origin, int w, int h) {
            double now = _clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.05, now - _lastTick);
            _lastTick = now;

            var io = ImGui.GetIO();
            var mouse = io.MousePos;

            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) _rmb = true;
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle)) _mmb = true;
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Right)) _rmb = false;
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Middle)) _mmb = false;

            if (hovered || _rmb || _mmb) {
                float dx = mouse.X - _lastMouse.X, dy = mouse.Y - _lastMouse.Y;
                if (_rmb) _cam.Rotate(dx * 0.005f, -dy * 0.005f);
                else if (_mmb) {
                    float k = _cam.Speed * 0.002f;
                    _cam.Position += (-_cam.Right * dx + _cam.Up * dy) * k;
                }
            }
            _lastMouse = mouse;

            if (hovered && io.MouseWheel != 0)
                _cam.Speed = Math.Clamp(_cam.Speed * (io.MouseWheel > 0 ? 1.25f : 0.8f), 10f, 200000f);

            if (hovered) {
                var move = Vec3.Zero;
                if (ImGui.IsKeyDown(ImGuiKey.W)) move += _cam.Forward;
                if (ImGui.IsKeyDown(ImGuiKey.S)) move -= _cam.Forward;
                if (ImGui.IsKeyDown(ImGuiKey.D)) move += _cam.Right;
                if (ImGui.IsKeyDown(ImGuiKey.A)) move -= _cam.Right;
                if (ImGui.IsKeyDown(ImGuiKey.E)) move += Vec3.UnitY;
                if (ImGui.IsKeyDown(ImGuiKey.Q)) move -= Vec3.UnitY;
                if (move != Vec3.Zero) {
                    float speed = _cam.Speed;
                    if (ImGui.IsKeyDown(ImGuiKey.LeftShift)) speed *= 4;
                    if (ImGui.IsKeyDown(ImGuiKey.LeftCtrl)) speed *= 0.25f;
                    _cam.Position += Vec3.Normalize(move) * speed * dt;
                }
                if (ImGui.IsKeyPressed(ImGuiKey.F)) FrameCurrentModel();
                if (ImGui.IsKeyPressed(ImGuiKey.Space)) TogglePlay();
            }
        }
        private void AnimationTick() {
            if (_renderer.Anim == null) return;
            double now = _clock.Elapsed.TotalSeconds;
            if (_playing && _clip != null) {
                float dt = (float)Math.Min(0.1, now - _animLastTick);
                _animFrame += dt * AnimFps * _animSpeed;
                float len = _clip.End - _clip.Start;
                if (_animFrame > _clip.End) {
                    if (_loop && len > 0) _animFrame = _clip.Start + (_animFrame - _clip.Start) % len;
                    else { _animFrame = _clip.End; _playing = false; }
                }
            }
            _animLastTick = now;
            float frame = _clip == null ? -1 : Math.Min(_animFrame, Math.Max(0, (_anim?.FrameCount ?? 1) - 1));
            _renderer.SetPose(frame);
        }

        public void RefreshModelSource() {
            var game = owner.GetGameData?.Invoke();

            if (!ReferenceEquals(game, _game)) {
                _game = game;
                _assets = null;
                _animsForModel = null;
                _animIndexBuilt = false;
                _animIndexBuilding = false;
                _currentModel = null;
                _currentAnims = new List<string>();
                _selectedAnimIndex = -1;
                _anim = null;
                _animSet = null;
                _clip = null;
                _playing = false;
                _status = _game == null ? "No model loaded." : "Pick a model.";
            }

            _allModels = owner.GetModels().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool Matches(string name, string filter) =>
            string.IsNullOrEmpty(filter) || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        private async Task LoadModel(string name) {
            if (_game == null || _loading) return;
            _loading = true;
            _currentModel = name;
            _selectedAnimIndex = -1;
            _anim = null;
            _animSet = null;
            _clip = null;
            _playing = false;
            _loadedAnimName = null;
            var sw = Stopwatch.StartNew();
            try {
                var game = _game;
                var cache = new AssetCache(game);
                _assets = cache;

                var result = await Task.Run(() => {
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

                owner.RunOnMainThread(() => {
                    _renderer.Load(result);
                    _vs.ClearSceneVisibility();
                    FrameCurrentModel();
                    int tris = _renderer.Items.Sum(i => i.Model.Parts.Sum(p => p.Triangles));
                    _status = result.Warnings.Count > 0
                        ? string.Join("; ", result.Warnings.Distinct())
                        : $"{tris:N0} triangles — {sw.ElapsedMilliseconds} ms";

                    _ = EnsureAnimationIndexThenPopulate(name);
                });
            }
            catch (Exception ex) {
                _status = "Load failed: " + ex.Message;
            }
            finally { _loading = false; }
        }

        private void FrameCurrentModel() {
            var min = _renderer.SceneMin;
            var max = _renderer.SceneMax;
            if (_vs.MirrorX) {
                var a = new Vec3(-max.X, min.Y, min.Z);
                var b = new Vec3(-min.X, max.Y, max.Z);
                min = a; max = b;
            }
            _cam.Frame(min, max, fromFront: true);
        }

        private async Task EnsureAnimationIndexThenPopulate(string forModel) {
            if (_animIndexBuilt) { PopulateAnimDropdown(forModel); return; }
            if (_animIndexBuilding) return;
            _animIndexBuilding = true;
            var game = _game;
            try {
                var map = await Task.Run(() => {
                    var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var animName in AnimationLookup.AllAnimations(game)) {
                        Animation anim;
                        try { anim = owner.GetAnimationForIndexing?.Invoke(animName); }
                        catch { anim = null; }
                        string model = anim == null ? null : AnimationLookup.FindModelFor(game, animName, anim);
                        if (model == null) continue;
                        if (!result.TryGetValue(model, out var list)) result[model] = list = new List<string>();
                        list.Add(animName);
                    }
                    foreach (var list in result.Values) list.Sort(StringComparer.OrdinalIgnoreCase);
                    return result;
                });

                if (!ReferenceEquals(game, _game)) return; // folder changed while we were scanning
                _animsForModel = map;
                _animIndexBuilt = true;
                PopulateAnimDropdown(forModel);
            }
            finally { _animIndexBuilding = false; }
        }

        private void PopulateAnimDropdown(string forModel) {
            if (!string.Equals(forModel, _currentModel, StringComparison.OrdinalIgnoreCase)) return; // user moved on
            _currentAnims = _animsForModel != null && _animsForModel.TryGetValue(forModel, out var list)
                ? list : new List<string>();
            _selectedAnimIndex = -1;
        }

        private async Task ApplyAnimation(string animName) {
            if (_assets == null || _currentModel == null) return;
            if (animName == null) {
                _anim = null;
                _animSet = null;
                _clip = null;
                _playing = false;
                _loadedAnimName = null;
                _renderer.SetAnimation(_renderer.Items.FirstOrDefault(i => i.Kind == ItemKind.Object), null);
                _renderer.SetPose(-1);
                return;
            }
            if (string.Equals(animName, _loadedAnimName, StringComparison.OrdinalIgnoreCase)) return;

            var cache = _assets;
            try {
                var (anim, set) = await Task.Run(() => {
                    var a = cache.GetAnimation(animName);
                    var s = a == null ? null : cache.GetAnimSet(animName, a);
                    return (a, s);
                });
                owner.RunOnMainThread(() => {
                    _anim = anim;
                    _animSet = set;
                    _loadedAnimName = animName;
                    var item = _renderer.Items.FirstOrDefault(i => i.Kind == ItemKind.Object);
                    _renderer.SetAnimation(item, anim);

                    var compat = AnimationLookup.CompatibilityWarning(item?.Model.Source.Model, anim);
                    _status = compat != null ? $"{animName}: {compat}" : _status;

                    int first = _animSet?.Clips.FindIndex(c => c.End > c.Start) ?? -1;
                    if (first >= 0) {
                        SelectClip(_animSet.Clips[first], play: true);
                    }
                    else {
                        _clip = null;
                        _playing = false;
                        _animFrame = -1;
                        _renderer.SetPose(-1);
                    }
                });
            }
            catch (Exception ex) {
                _status = $"Animation '{animName}' failed to load: {ex.Message}";
            }
        }

        public override void Dispose() {
            base.Dispose();
            _renderer.Dispose();
            _rt.Dispose();
        }
    }
}