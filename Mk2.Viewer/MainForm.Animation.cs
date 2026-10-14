using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Mk2.Formats;
using Mk2.Game;
using Mk2.Viewer.Rendering;
using Vec3 = System.Numerics.Vector3;

namespace Mk2.Viewer
{
    /// <summary>The Animations and Skeletons tabs: ANM3 playback with CPU skinning, and bone hierarchy inspection.</summary>
    public sealed partial class MainForm
    {
        private const float AnimFps = 30f;   // Krome timelines run at 30 frames per second (?)

        // ---- UI
        private ListBox _animList, _skelList, _clipList;
        private TextBox _animFilter, _skelFilter, _animModelBox;
        private TreeView _boneTree;
        private Button _playButton;
        private TrackBar _frameBar;
        private Label _frameLabel, _animModelLabel, _skelInfoLabel;
        private CheckBox _loopBox;
        private ComboBox _speedBox;
        private List<string> _allAnims = new List<string>();
        private List<string> _allSkeletons = new List<string>();

        // ---- playback state
        private Animation _anim;
        private AnimSet _animSet;
        private AnimClip _clip;
        private string _animName, _animModelName;
        private bool _playing;
        private float _animFrame = -1;
        private float _animSpeed = 1f;
        private double _animLastTick;
        private bool _suppressFrameBar;
        private bool _skeletonMode;          // loaded from the Skeletons tab (bind pose, bone tree)

        private TabPage BuildAnimationsPage()
        {
            _animList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            _animList.DoubleClick += async (s, e) => { if (_animList.SelectedItem is string a) await LoadAnimation(a, null, skeletonMode: false); };
            _animList.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter && _animList.SelectedItem is string a) { e.Handled = true; await LoadAnimation(a, null, false); } };
            _animFilter = new TextBox { Dock = DockStyle.Top };
            SetCue(_animFilter, "Filter animations…");
            _animFilter.TextChanged += (s, e) => FillAnimList();

            // Model row: which model the animation drives (auto-matched, can be overridden).
            _animModelLabel = new Label { Dock = DockStyle.Top, Height = 18, Text = "Model: —" };
            _animModelBox = new TextBox { Dock = DockStyle.Fill };
            SetCue(_animModelBox, "Model to animate (blank = automatic)");
            var useModel = new Button { Dock = DockStyle.Right, Width = 54, Text = "Use" };
            useModel.Click += async (s, e) =>
            {
                if (_animName == null) return;
                string m = _animModelBox.Text.Trim();
                await LoadAnimation(_animName, m.Length > 0 ? m : null, skeletonMode: false);
            };
            _animModelBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; useModel.PerformClick(); } };
            var modelRow = new Panel { Dock = DockStyle.Top, Height = 24 };
            modelRow.Controls.Add(_animModelBox);
            modelRow.Controls.Add(useModel);

            _clipList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, FormattingEnabled = true };
            _clipList.Format += FormatClip;
            _clipList.SelectedIndexChanged += (s, e) => { if (_clipList.SelectedItem is AnimClip c) SelectClip(c, play: true); };
            var clipHeader = new Label { Dock = DockStyle.Top, Height = 18, Text = "Clips (from the .bbi)" };

            // Playback controls.
            _playButton = new Button { Width = 64, Height = 26, Text = "Play" };
            _playButton.Click += (s, e) => TogglePlay();
            var prev = new Button { Width = 30, Height = 26, Text = "<" };
            prev.Click += (s, e) => StepFrame(-1);
            var next = new Button { Width = 30, Height = 26, Text = ">" };
            next.Click += (s, e) => StepFrame(1);
            var restart = new Button { Width = 30, Height = 26, Text = "|<" };
            restart.Click += (s, e) => { if (_clip != null) SetAnimFrame(_clip.Start); };
            _loopBox = new CheckBox { Text = "Loop", AutoSize = true, Checked = true, Margin = new Padding(6, 6, 3, 3) };
            _speedBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 64 };
            foreach (var sp in new[] { "0.1x", "0.25x", "0.5x", "1x", "1.5x", "2x" }) _speedBox.Items.Add(sp);
            _speedBox.SelectedIndex = 3;
            _speedBox.SelectedIndexChanged += (s, e) =>
                _animSpeed = float.Parse(((string)_speedBox.SelectedItem).TrimEnd('x'), System.Globalization.CultureInfo.InvariantCulture);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, WrapContents = false };
            buttons.Controls.AddRange(new Control[] { restart, prev, _playButton, next, _loopBox, _speedBox });

            _frameBar = new TrackBar { Dock = DockStyle.Top, Height = 32, TickStyle = TickStyle.None, Minimum = 0, Maximum = 1 };
            _frameBar.ValueChanged += (s, e) =>
            {
                if (_suppressFrameBar) return;
                _playing = false;
                UpdatePlayButton();
                SetAnimFrame(_frameBar.Value);
            };
            _frameLabel = new Label { Dock = DockStyle.Top, Height = 18, Text = "" };

            var showRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 26, WrapContents = false };
            showRow.Controls.Add(ViewCheck("Skeleton", () => _vs.Skeleton, v => _vs.Skeleton = v));
            showRow.Controls.Add(ViewCheck("Mesh", () => _vs.AnimatedMesh, v => _vs.AnimatedMesh = v));

            var playback = new Panel { Dock = DockStyle.Bottom, Height = 32 + 32 + 18 + 26 + 4 };
            playback.Controls.Add(showRow);
            playback.Controls.Add(_frameLabel);
            playback.Controls.Add(_frameBar);
            playback.Controls.Add(buttons);

            var lower = new Panel { Dock = DockStyle.Fill };
            lower.Controls.Add(_clipList);
            lower.Controls.Add(clipHeader);
            lower.Controls.Add(modelRow);
            lower.Controls.Add(_animModelLabel);
            lower.Controls.Add(playback);

            var split = NewListSplit();
            split.Panel1.Controls.Add(_animList);
            split.Panel1.Controls.Add(_animFilter);
            split.Panel2.Controls.Add(lower);

            var page = new TabPage("Animations");
            page.Controls.Add(split);
            page.Controls.Add(new Label { Dock = DockStyle.Top, Height = 34, Text = "Double-click an animation (.anm) to play it on its model. Pick a clip below.", ForeColor = SystemColors.GrayText });
            return page;
        }

        private TabPage BuildSkeletonsPage()
        {
            _skelList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            _skelList.DoubleClick += async (s, e) => { if (_skelList.SelectedItem is string m) await LoadAnimation(m, m, skeletonMode: true); };
            _skelList.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter && _skelList.SelectedItem is string m) { e.Handled = true; await LoadAnimation(m, m, true); } };
            _skelFilter = new TextBox { Dock = DockStyle.Top };
            SetCue(_skelFilter, "Filter models with skeletons…");
            _skelFilter.TextChanged += (s, e) => FillSkeletonList();

            _boneTree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
            _boneTree.AfterSelect += (s, e) => { if (e.Node?.Tag is int b) SelectBone(b); };
            _skelInfoLabel = new Label { Dock = DockStyle.Top, Height = 18, Text = "Bones" };

            var showRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 26, WrapContents = false };
            showRow.Controls.Add(ViewCheck("Skeleton", () => _vs.Skeleton, v => _vs.Skeleton = v));
            showRow.Controls.Add(ViewCheck("Mesh", () => _vs.AnimatedMesh, v => _vs.AnimatedMesh = v));
            var expand = new Button { Text = "Expand all", AutoSize = true };
            expand.Click += (s, e) => _boneTree.ExpandAll();
            showRow.Controls.Add(expand);

            var lower = new Panel { Dock = DockStyle.Fill };
            lower.Controls.Add(_boneTree);
            lower.Controls.Add(_skelInfoLabel);
            lower.Controls.Add(showRow);

            var split = NewListSplit();
            split.Panel1.Controls.Add(_skelList);
            split.Panel1.Controls.Add(_skelFilter);
            split.Panel2.Controls.Add(lower);

            var page = new TabPage("Skeletons");
            page.Controls.Add(split);
            page.Controls.Add(new Label { Dock = DockStyle.Top, Height = 34, Text = "Double-click a model to show its skeleton in bind pose. Click a bone to highlight it.", ForeColor = SystemColors.GrayText });
            return page;
        }

        /// <summary>
        /// List on top, details below. SplitterDistance can only be set once the container has a real size
        /// (WinForms throws otherwise), so it is applied on the first useful resize.
        /// </summary>
        private static SplitContainer NewListSplit()
        {
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            bool placed = false;
            split.SizeChanged += (s, e) =>
            {
                if (placed || split.Height < 200) return;
                placed = true;
                try { split.SplitterDistance = Math.Max(80, split.Height * 2 / 5); } catch (InvalidOperationException) { }
            };
            return split;
        }

        private CheckBox ViewCheck(string text, Func<bool> get, Action<bool> set)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Checked = get(), Margin = new Padding(3, 4, 6, 3) };
            c.CheckedChanged += (s, e) => { set(c.Checked); Redraw(); };
            // Keep the copies in both tabs and the View menu in sync.
            c.VisibleChanged += (s, e) => { if (c.Visible) c.Checked = get(); };
            return c;
        }

        private void FillAnimationLists()
        {
            if (_game == null) return;
            _allAnims = AnimationLookup.AllAnimations(_game);
            _allSkeletons = AnimationLookup.ModelsWithSkeletons(_game);
            FillAnimList();
            FillSkeletonList();
        }

        private void FillAnimList()
        {
            if (_animList == null) return;
            var f = _animFilter.Text.Trim();
            _animList.BeginUpdate();
            _animList.Items.Clear();
            foreach (var a in _allAnims)
                if (f.Length == 0 || a.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) _animList.Items.Add(a);
            _animList.EndUpdate();
        }

        private void FillSkeletonList()
        {
            if (_skelList == null) return;
            var f = _skelFilter.Text.Trim();
            _skelList.BeginUpdate();
            _skelList.Items.Clear();
            foreach (var m in _allSkeletons)
                if (f.Length == 0 || m.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) _skelList.Items.Add(m);
            _skelList.EndUpdate();
        }

        // =====================================================================================
        // Loading
        // =====================================================================================

        /// <summary>
        /// Loads an animation and the model it drives (auto-matched unless <paramref name="modelOverride"/> is given)
        /// into a single-model scene. Skeleton mode shows the bind pose and fills the bone tree.
        /// </summary>
        private async Task LoadAnimation(string animName, string modelOverride, bool skeletonMode)
        {
            if (_game == null) return;
            var sw = Stopwatch.StartNew();
            UseWaitCursor = true;
            try
            {
                _assets = new AssetCache(_game);
                var cache = _assets;
                var game = _game;
                var r = await Task.Run(() =>
                {
                    var anim = cache.GetAnimation(animName);
                    string modelName = anim == null ? null : modelOverride ?? AnimationLookup.FindModelFor(game, animName, anim);
                    var set = anim == null ? null : cache.GetAnimSet(animName, anim);
                    var m = modelName != null ? cache.GetModel(modelName, null) : null;
                    var def = new LevelDef { Name = animName };
                    var cd = new ChunkDef { Name = animName };
                    def.Chunks.Add(cd);
                    var sc = new LevelScene { Def = def, Assets = cache };
                    var ch = new SceneChunk { Def = cd };
                    if (m != null)
                    {
                        var o = new LevelObject { ClassName = m.Name, ChunkName = animName, Section = new BniSection { Name = m.Name }, Category = ObjectCategory.Prop };
                        ch.Objects.Add(o);
                        ch.ObjectModels[o] = m;
                    }
                    sc.Chunks.Add(ch);
                    if (anim == null) sc.Warnings.Add($"Could not load {animName}.anm");
                    else if (modelName == null) sc.Warnings.Add($"{animName}: no matching model found — showing the skeleton only. Type a model name to use one.");
                    else if (m == null) sc.Warnings.Add($"Could not load model {modelName}");
                    var compat = AnimationLookup.CompatibilityWarning(m?.Model, anim);
                    if (compat != null) sc.Warnings.Add($"{animName} on {m.Name}: {compat}");
                    sc.Warnings.AddRange(cache.Warnings);
                    return (scene: sc, anim, set, model: m?.Name);
                });

                _anim = r.anim;
                _animSet = r.set;
                _animName = animName;
                _animModelName = r.model;
                _skeletonMode = skeletonMode;
                _clip = null;
                _playing = false;

                ShowScene(r.scene, sw, switchToSceneTab: false);
                if (_glReady) _gl.MakeCurrent();
                var item = _renderer.Items.FirstOrDefault(i => i.Kind == ItemKind.Object);
                _renderer.SetAnimation(item, _anim);
                FrameWorld(_renderer.SceneMin, _renderer.SceneMax, fromFront: true);
                UpdateSpeedLabel();

                _animModelLabel.Text = "Model: " + (r.model ?? "(none — skeleton only)");
                if (!skeletonMode && modelOverride == null) _animModelBox.Text = "";
                FillClipList();
                FillBoneTree();
                if (!skeletonMode && _animSet != null && _animSet.Clips.Count > 0)
                {
                    // Start on the first real clip (skip a leading single-frame bind pose).
                    int first = _animSet.Clips.FindIndex(c => c.End > c.Start);
                    _clipList.SelectedIndex = first >= 0 ? first : 0;
                }
                else
                {
                    _animFrame = -1;
                    _renderer.SetPose(-1);
                    UpdateFrameUi();
                }
                ShowAnimationProperties();
                SetStatus($"{animName}: {(_anim == null ? "failed" : $"{_anim.Bones.Count} bones, {_anim.FrameCount} frames, {_animSet?.Clips.Count ?? 0} clips")}{(r.model != null ? " on " + r.model : "")} — {sw.ElapsedMilliseconds} ms");
                Redraw();
            }
            catch (Exception ex)
            {
                Log("Animation load failed: " + ex);
                MessageBox.Show(this, ex.Message, "Animation load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { UseWaitCursor = false; }
        }

        private void FillClipList()
        {
            _clipList.BeginUpdate();
            _clipList.Items.Clear();
            if (_animSet != null && !_skeletonMode)
                foreach (var c in _animSet.Clips) _clipList.Items.Add(c);
            _clipList.EndUpdate();
        }

        private static void FormatClip(object sender, ListControlConvertEventArgs e)
        {
            if (e.ListItem is AnimClip c)
                e.Value = $"{c.Name}   {c.Start}–{c.End}{(c.Loop ? "  (loop)" : "")}{(c.Events.Count > 0 ? $"  ({c.Events.Count} events)" : "")}";
        }

        private void FillBoneTree()
        {
            _boneTree.BeginUpdate();
            _boneTree.Nodes.Clear();
            if (_anim != null && _skeletonMode)
            {
                var nodes = new TreeNode[_anim.Bones.Count];
                foreach (int i in _anim.EvalOrder)
                {
                    var b = _anim.Bones[i];
                    var n = new TreeNode($"{b.Name}  [{i}]") { Tag = i, ForeColor = b.IsAnimated ? Theme.Fore : Theme.Dim };
                    nodes[i] = n;
                    if (b.Parent >= 0 && nodes[b.Parent] != null) nodes[b.Parent].Nodes.Add(n);
                    else _boneTree.Nodes.Add(n);
                }
                // Expand the spine of the hierarchy, but not every leaf.
                foreach (TreeNode root in _boneTree.Nodes) ExpandToDepth(root, 6);
                _skelInfoLabel.Text = $"{_anim.Bones.Count} bones ({_anim.Bones.Count(b => b.IsAnimated)} animated) — dim = never moves";
            }
            else _skelInfoLabel.Text = "Bones";
            _boneTree.EndUpdate();
            if (_boneTree.Nodes.Count > 0) _boneTree.Nodes[0].EnsureVisible();
        }

        private static void ExpandToDepth(TreeNode n, int depth)
        {
            if (depth <= 0) return;
            n.Expand();
            foreach (TreeNode c in n.Nodes) ExpandToDepth(c, depth - 1);
        }

        // =====================================================================================
        // Playback
        // =====================================================================================

        private void SelectClip(AnimClip c, bool play)
        {
            if (_anim == null || c == null) return;
            _clip = c;
            _loopBox.Checked = c.Loop;
            _suppressFrameBar = true;
            _frameBar.Minimum = 0;
            _frameBar.Maximum = Math.Max(c.Start + 1, c.End);
            _frameBar.Minimum = c.Start;
            _frameBar.Value = c.Start;
            _suppressFrameBar = false;
            _animFrame = c.Start;
            _playing = play && c.End > c.Start;
            _animLastTick = _clock.Elapsed.TotalSeconds;
            UpdatePlayButton();
            UpdateFrameUi();
            ShowAnimationProperties();
            Redraw();
        }

        private void TogglePlay()
        {
            if (_clip == null)
            {
                if (_clipList.Items.Count > 0) _clipList.SelectedIndex = Math.Max(0, _clipList.SelectedIndex);
                return;
            }
            if (!_playing && _animFrame >= _clip.End && !_loopBox.Checked) _animFrame = _clip.Start;
            _playing = !_playing && _clip.End > _clip.Start;
            _animLastTick = _clock.Elapsed.TotalSeconds;
            UpdatePlayButton();
            Redraw();
        }

        private void StepFrame(int d)
        {
            if (_clip == null) return;
            _playing = false;
            UpdatePlayButton();
            float f = (float)Math.Round(_animFrame) + d;
            if (f > _clip.End) f = _loopBox.Checked ? _clip.Start : _clip.End;
            if (f < _clip.Start) f = _loopBox.Checked ? _clip.End : _clip.Start;
            SetAnimFrame(f);
        }

        private void SetAnimFrame(float f)
        {
            _animFrame = f;
            UpdateFrameUi();
            Redraw();
        }

        private void UpdatePlayButton() { if (_playButton != null) _playButton.Text = _playing ? "Pause" : "Play"; }

        private int _lastShownFrame = int.MinValue;

        private void UpdateFrameUi()
        {
            int f = (int)Math.Floor(_animFrame);
            if (f == _lastShownFrame && _playing) return;   // avoid re-layout every render frame
            _lastShownFrame = f;
            if (_clip != null)
            {
                _suppressFrameBar = true;
                _frameBar.Value = Math.Clamp(f, _frameBar.Minimum, _frameBar.Maximum);
                _suppressFrameBar = false;
                int len = Math.Max(1, _clip.End - _clip.Start);
                var ev = _clip.Events.Where(e => e.Frame == f).Select(e => e.Name).ToList();
                _frameLabel.Text = $"frame {f - _clip.Start} / {len}   (timeline {f} of {_anim?.FrameCount ?? 0})" +
                                   (ev.Count > 0 ? "   event: " + string.Join(", ", ev) : "");
            }
            else _frameLabel.Text = _anim != null ? "bind pose" : "";
        }

        /// <summary>Advances playback and poses the model. Called once per rendered frame with the GL context current.</summary>
        private void AnimationTick()
        {
            if (_renderer.Anim == null) return;
            double now = _clock.Elapsed.TotalSeconds;
            if (_playing && _clip != null)
            {
                float dt = (float)Math.Min(0.1, now - _animLastTick);
                _animFrame += dt * AnimFps * _animSpeed;
                float len = _clip.End - _clip.Start;
                if (_animFrame > _clip.End)
                {
                    if (_loopBox.Checked && len > 0) _animFrame = _clip.Start + (_animFrame - _clip.Start) % len;
                    else { _animFrame = _clip.End; _playing = false; UpdatePlayButton(); }
                }
                UpdateFrameUi();
            }
            _animLastTick = now;
            float frame = _clip == null ? -1 : Math.Min(_animFrame, Math.Max(0, (_anim?.FrameCount ?? 1) - 1));
            _renderer.SetPose(frame);
        }

        private bool AnimationPlaying => _playing && _renderer.Anim != null;

        // =====================================================================================
        // Properties
        // =====================================================================================

        private void SelectBone(int b)
        {
            if (_anim == null || b < 0 || b >= _anim.Bones.Count) return;
            if (_glReady) _gl.MakeCurrent();
            _renderer.SetSelectedBone(b);
            var bone = _anim.Bones[b];
            var sb = new StringBuilder();
            sb.AppendLine($"BONE {b}  {bone.Name}");
            sb.AppendLine(new string('=', Math.Min(60, bone.Name.Length + 10)));
            sb.AppendLine($"parent      {(bone.Parent >= 0 ? $"{bone.Parent} {_anim.Bones[bone.Parent].Name}" : "(root)")}");
            if (bone.Children.Count > 0)
                sb.AppendLine($"children    {string.Join(", ", bone.Children.Select(c => $"{c} {_anim.Bones[c].Name}"))}");
            sb.AppendLine($"bind pos    {V(bone.BindPosition)}   (model space)");
            sb.AppendLine($"local       {V(bone.LocalOffset)}   (from parent)");
            sb.AppendLine($"key type    {(int)bone.KeyType} {bone.KeyType}");
            if (bone.Keys.Count > 0)
                sb.AppendLine($"keys        {bone.Keys.Count}  (frames {bone.Keys[0].Frame}–{bone.Keys[^1].Frame})");
            sb.AppendLine($"animated    {(bone.IsAnimated ? "yes" : "no (holds its bind pose)")}");
            if (_renderer.Anim != null && _renderer.Anim.HasPose)
                sb.AppendLine($"posed pos   {V(_renderer.Anim.Globals[b].Translation)}");

            var lm = _renderer.Anim?.Item?.Model.Source;
            if (lm != null)
            {
                int full = 0, partial = 0;
                foreach (var mesh in lm.Model.Meshes)
                {
                    if (!mesh.Skinned) continue;
                    foreach (var st in mesh.Strips)
                        foreach (var v in st.Vertices)
                        {
                            bool on0 = v.Bone0 == b && v.Weight0 > 0.001f;
                            bool on1 = v.Bone1 == b && v.Weight0 < 0.999f;
                            if (on0 && (v.Bone1 == b || v.Weight0 >= 0.999f)) full++;
                            else if (on0 || on1) partial++;
                        }
                }
                var rigid = lm.Model.Components.Where(c => c.RigidBone == b).Select(c => c.Name).ToList();
                sb.AppendLine();
                sb.AppendLine($"MODEL {lm.Name}");
                sb.AppendLine($"  skinned verts  {full} fully, {partial} blended with another bone (strip vertices)");
                if (rigid.Count > 0) sb.AppendLine($"  rigid parts    {string.Join(", ", rigid)}");
                if (b < lm.Model.AnimNodePositions.Count && (lm.Model.AnimNodePositions[b] - bone.BindPosition).Length() > 0.01f)
                    sb.AppendLine($"  note: MDL anim node {b} is at {V(lm.Model.AnimNodePositions[b])}");
            }
            _props.Text = sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
            FillLinks(null);
            _rightTabs.SelectedIndex = 0;
            Redraw();
        }

        private void ShowAnimationProperties()
        {
            var sb = new StringBuilder();
            if (_anim == null)
            {
                sb.AppendLine(_animName ?? "");
                sb.AppendLine("(animation could not be loaded — see the Log tab)");
            }
            else
            {
                sb.AppendLine($"ANIMATION {_animName}");
                sb.AppendLine(new string('=', Math.Min(60, (_animName ?? "").Length + 10)));
                sb.AppendLine($"model       {_animModelName ?? "(none)"}");
                sb.AppendLine($"source      {_game?.Describe(_animName + ".anm")}");
                sb.AppendLine($"bones       {_anim.Bones.Count}  ({_anim.Bones.Count(b => b.IsAnimated)} animated)");
                sb.AppendLine($"frames      {_anim.FrameCount}  ({_anim.FrameCount / AnimFps:0.0} s at {AnimFps:0} fps)");
                sb.AppendLine($"transforms  {_anim.TransformCount} in the .ang");
                var types = _anim.Bones.GroupBy(b => b.KeyType).Select(g => $"{g.Key} ×{g.Count()}");
                sb.AppendLine($"key types   {string.Join(", ", types)}");
                foreach (var w in _anim.Warnings) sb.AppendLine("warning     " + w);
                if (_clip != null)
                {
                    sb.AppendLine();
                    sb.AppendLine($"CLIP {_clip.Name}");
                    sb.AppendLine($"  frames {_clip.Start}–{_clip.End} ({_clip.End - _clip.Start} frames, {(_clip.End - _clip.Start) / AnimFps:0.00} s), {(_clip.Loop ? "loops" : "plays once")}");
                    foreach (var e in _clip.Events)
                        sb.AppendLine($"  event @{e.Frame,-5} {(e.Type == 1 ? "sound " : $"type {e.Type} ")}{e.Name}");
                }
                if (_animSet != null && _animSet.Clips.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"CLIPS ({_animSet.Clips.Count})");
                    foreach (var c in _animSet.Clips)
                        sb.AppendLine($"  {c.Start,6}–{c.End,-6} {(c.Loop ? "loop" : "once")}  {c.Name}");
                }
            }
            _props.Text = sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
            FillLinks(null);
        }
    }
}
