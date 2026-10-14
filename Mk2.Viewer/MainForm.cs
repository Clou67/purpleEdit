using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Mk2.Export;
using Mk2.Formats;
using Mk2.Game;
using Mk2.Viewer.Rendering;
using OpenTK.GLControl;
using OpenTK.Windowing.Common;
using Vec2 = System.Numerics.Vector2;
using Vec3 = System.Numerics.Vector3;
using Mat4 = System.Numerics.Matrix4x4;

namespace Mk2.Viewer
{
    public sealed class MainForm : Form
    {
        // ---- data
        private GameData _game;
        private ModelDb _db;
        private List<LevelDef> _levels = new List<LevelDef>();
        private LevelScene _scene;
        private AssetCache _assets;

        // ---- rendering
        private GLControl _gl;
        private readonly SceneRenderer _renderer = new SceneRenderer();
        private readonly Camera _cam = new Camera();
        private readonly ViewSettings _vs = new ViewSettings();
        private bool _glReady;
        private Vec3 _clear = new Vec3(0.18f, 0.2f, 0.26f);
        private Vec3 _fogColor = new Vec3(0.5f, 0.5f, 0.6f);
        private Vec2 _fogRange = new Vec2(10000, 100000);

        // ---- input
        private readonly HashSet<Keys> _keys = new HashSet<Keys>();
        private Point _lastMouse, _downMouse;
        private bool _rmb, _mmb, _lmb;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastTick;
        private bool _animate;

        // ---- UI
        private TreeView _levelTree, _sceneTree;
        private ListBox _packList, _modelList;
        private TextBox _modelFilter, _sceneFilter, _props, _log, _levelInfo;
        private ListBox _refList;
        private MenuStrip _menu;
        private ToolStrip _tools;
        private StatusStrip _statusStrip;
        private SplitContainer _mainSplit, _rightSplit;
        private bool _fullscreen;
        private FormWindowState _prevState;
        private FormBorderStyle _prevBorder;
        private ToolStripStatusLabel _status, _camStatus;
        private ToolStripLabel _speedLabel;
        private TabControl _leftTabs, _rightTabs;
        private List<string> _allModels = new List<string>();
        private bool _suppressTreeCheck;

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "purpleEdit", "settings.txt");

        public MainForm(string[] args)
        {
            Text = "purpleEdit v1.00";
            Width = 1600; Height = 950;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = false;
            BuildUi();
            Theme.Apply(this, LoadSetting("dark") == "1");
            Application.Idle += OnIdle;
            Shown += async (s, e) =>
            {
                string root = args.Length > 0 ? args[0] : LoadSetting("root");
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root)) await OpenGameFolder(root);
                else Log("Use File > Open game folder… and pick the folder that contains your extracted files / .BPK / .RKV.");
            };
        }

        // =====================================================================================
        // UI construction
        // =====================================================================================

        private void BuildUi()
        {
            var menu = _menu = new MenuStrip();
            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add("&Open game folder…", null, async (s, e) => await BrowseGameFolder());
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("Export scene to .glb…", null, (s, e) => ExportScene());
            file.DropDownItems.Add("Export selected model to .glb…", null, (s, e) => ExportSelectedModel());
            file.DropDownItems.Add("Export scene textures to PNG…", null, (s, e) => ExportTextures());
            file.DropDownItems.Add("Export layouts (lc3) as text…", null, (s, e) => ExportLayouts());
            file.DropDownItems.Add("Save screenshot…", null, (s, e) => SaveScreenshot());
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("E&xit", null, (s, e) => Close());
            menu.Items.Add(file);

            var view = new ToolStripMenuItem("&View");
            view.DropDownItems.Add(Toggle("Level geometry (overlays)", () => _vs.Overlays, v => _vs.Overlays = v));
            view.DropDownItems.Add(Toggle("Objects", () => _vs.Objects, v => _vs.Objects = v));
            view.DropDownItems.Add(Toggle("Sky dome", () => _vs.EnvDome, v => _vs.EnvDome = v));
            view.DropDownItems.Add(new ToolStripSeparator());
            view.DropDownItems.Add(Toggle("Textures", () => _vs.Textures, v => _vs.Textures = v));
            view.DropDownItems.Add(Toggle("Vertex colours (prelighting)", () => _vs.VertexColors, v => _vs.VertexColors = v));
            view.DropDownItems.Add(Toggle("Directional lighting", () => _vs.Lighting, v => _vs.Lighting = v));
            view.DropDownItems.Add(Toggle("Transparency / blending", () => _vs.Transparency, v => _vs.Transparency = v));
            view.DropDownItems.Add(Toggle("Wireframe", () => _vs.Wireframe, v => _vs.Wireframe = v));
            view.DropDownItems.Add(Toggle("Cull back faces", () => _vs.CullBackfaces, v => _vs.CullBackfaces = v));
            view.DropDownItems.Add(Toggle("Fog (from levels.ini)", () => _vs.Fog, v => _vs.Fog = v));
            view.DropDownItems.Add(Toggle("Animate (UV scrolling)", () => _animate, v => _animate = v));
            view.DropDownItems.Add(Toggle("Mirror X (match in-game handedness)", () => _vs.MirrorX, SetMirror));
            var bright = new ToolStripMenuItem("Vertex colour brightness");
            foreach (var b in new[] { 1.0f, 1.3f, 1.6f, 2.0f })
            {
                var item = new ToolStripMenuItem("×" + b.ToString("0.0")) { Tag = b };
                item.Click += (s, e) => { _vs.Brightness = (float)((ToolStripMenuItem)s).Tag; foreach (ToolStripMenuItem i in bright.DropDownItems) i.Checked = i == s; Redraw(); };
                item.Checked = Math.Abs(b - _vs.Brightness) < 0.01f;
                bright.DropDownItems.Add(item);
            }
            view.DropDownItems.Add(bright);
            view.DropDownItems.Add(new ToolStripSeparator());
            view.DropDownItems.Add(Toggle("Ground collision", () => _vs.GroundCollision, v => _vs.GroundCollision = v));
            view.DropDownItems.Add(Toggle("Object collision (.cgr)", () => _vs.ObjectCollision, v => _vs.ObjectCollision = v));
            view.DropDownItems.Add(Toggle("Collision as translucent overlay", () => _vs.CollisionOverlay, v => _vs.CollisionOverlay = v));
            view.DropDownItems.Add(Toggle("Model collision components (c_*)", () => _vs.ModelCollisionComponents, v => _vs.ModelCollisionComponents = v));
            view.DropDownItems.Add(Toggle("Shatter fragments (f_*)", () => _vs.Fragments, v => _vs.Fragments = v));
            view.DropDownItems.Add(new ToolStripSeparator());
            view.DropDownItems.Add(Toggle("Trigger / volume / path shapes", () => _vs.Shapes, v => _vs.Shapes = v));
            view.DropDownItems.Add(Toggle("Markers for objects without models", () => _vs.Markers, v => _vs.Markers = v));
            var cats = new ToolStripMenuItem("Object categories");
            foreach (ObjectCategory c in Enum.GetValues(typeof(ObjectCategory)))
            {
                var cc = c;
                cats.DropDownItems.Add(Toggle(c.ToString(), () => _vs.CategoryVisible(cc), v => { if (v) _vs.HiddenCategories.Remove(cc); else _vs.HiddenCategories.Add(cc); }));
            }
            view.DropDownItems.Add(cats);
            view.DropDownItems.Add(new ToolStripSeparator());
            view.DropDownItems.Add("Frame whole scene", null, (s, e) => { FrameWorld(_renderer.SceneMin, _renderer.SceneMax); Redraw(); });
            view.DropDownItems.Add("Focus selection (F)", null, (s, e) => FocusSelection());
            view.DropDownItems.Add(new ToolStripMenuItem("Fullscreen viewport", null, (s, e) => ToggleFullscreen()) { ShortcutKeys = Keys.F11 });
            view.DropDownItems.Add("Background colour…", null, (s, e) => PickBackground());
            view.DropDownItems.Add(new ToolStripSeparator());
            view.DropDownItems.Add(Toggle("Dark mode (UI)", () => Theme.Dark, v => { Theme.Apply(this, v); SaveSetting("dark", v ? "1" : "0"); }));
            menu.Items.Add(view);

            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add("Controls", null, (s, e) => MessageBox.Show(this, ControlsHelp, "Controls"));
            help.DropDownItems.Add("About file formats", null, (s, e) => MessageBox.Show(this,
                "See FORMATS.md next to the solution for everything decoded so far:\nRKV2/BPK, BNI, MDL3/MDG3 (PS2 VIF), TEX, MIN, CGR, levels.ini and .lc3 layouts.", "Formats"));
            menu.Items.Add(help);

            var tools = _tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
            tools.Items.Add(ToolToggle("Geometry", () => _vs.Overlays, v => _vs.Overlays = v));
            tools.Items.Add(ToolToggle("Objects", () => _vs.Objects, v => _vs.Objects = v));
            tools.Items.Add(ToolToggle("Shapes", () => _vs.Shapes, v => _vs.Shapes = v));
            tools.Items.Add(ToolToggle("Collision", () => _vs.GroundCollision, v => { _vs.GroundCollision = v; _vs.ObjectCollision = v; }));
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add(ToolToggle("Textures", () => _vs.Textures, v => _vs.Textures = v));
            tools.Items.Add(ToolToggle("VColour", () => _vs.VertexColors, v => _vs.VertexColors = v));
            tools.Items.Add(ToolToggle("Light", () => _vs.Lighting, v => _vs.Lighting = v));
            tools.Items.Add(ToolToggle("Wire", () => _vs.Wireframe, v => _vs.Wireframe = v));
            tools.Items.Add(ToolToggle("Fog", () => _vs.Fog, v => _vs.Fog = v));
            tools.Items.Add(new ToolStripSeparator());
            foreach (var c in new[] { ObjectCategory.Trigger, ObjectCategory.Path, ObjectCategory.Volume, ObjectCategory.Spawn, ObjectCategory.Camera, ObjectCategory.Sound, ObjectCategory.Logic })
            {
                var cc = c;
                var b = ToolToggle(c.ToString(), () => _vs.CategoryVisible(cc), v => { if (v) _vs.HiddenCategories.Remove(cc); else _vs.HiddenCategories.Add(cc); });
                var col = SceneRenderer.CategoryColors[c];
                b.Tag = Color.FromArgb((int)(col.X * 255), (int)(col.Y * 255), (int)(col.Z * 255));
                b.ForeColor = Color.FromArgb((int)(col.X * 160), (int)(col.Y * 160), (int)(col.Z * 160));
                tools.Items.Add(b);
            }
            tools.Items.Add(new ToolStripSeparator());
            _speedLabel = new ToolStripLabel("Speed: 3000");
            tools.Items.Add(_speedLabel);

            var status = _statusStrip = new StatusStrip();
            _status = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _camStatus = new ToolStripStatusLabel("");
            status.Items.Add(_status);
            status.Items.Add(_camStatus);

            // ---- left panel
            _leftTabs = new TabControl { Dock = DockStyle.Fill };
            _levelTree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
            _levelTree.NodeMouseDoubleClick += async (s, e) => await LevelTreeActivate(e.Node);
            _levelTree.AfterSelect += (s, e) =>
            {
                var lv = e.Node?.Tag as LevelDef ?? (e.Node?.Tag is ValueTuple<LevelDef, ChunkDef> t ? t.Item1 : null);
                if (lv != null && (_scene == null || _scene.Def != lv)) { ShowLevelInfo(lv, null); _rightTabs.SelectedIndex = 0; }
            };
            var levelHint = new Label { Dock = DockStyle.Top, Height = 34, Text = "Double-click a level to load it all, or a chunk to load just that chunk (+ env).", ForeColor = SystemColors.GrayText };
            var levelPage = new TabPage("Levels");
            levelPage.Controls.Add(_levelTree);
            levelPage.Controls.Add(levelHint);

            _sceneTree = new TreeView { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false };
            _sceneTree.AfterCheck += SceneTreeAfterCheck;
            _sceneTree.AfterSelect += (s, e) => SceneTreeSelect(e.Node, false);
            _sceneTree.NodeMouseDoubleClick += (s, e) => SceneTreeSelect(e.Node, true);
            _sceneFilter = new TextBox { Dock = DockStyle.Top };
            SetCue(_sceneFilter, "Filter objects (class, ID name, ID number)…");
            _sceneFilter.TextChanged += (s, e) => BuildSceneTree();
            var scenePage = new TabPage("Scene");
            scenePage.Controls.Add(_sceneTree);
            scenePage.Controls.Add(_sceneFilter);

            _packList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            _packList.DoubleClick += async (s, e) => { if (_packList.SelectedItem is string p) await LoadPack(p); };
            var packPage = new TabPage("Packs (.BPK)");
            packPage.Controls.Add(_packList);
            packPage.Controls.Add(new Label { Dock = DockStyle.Top, Height = 34, Text = "Double-click to view every model in a chunk pack.", ForeColor = SystemColors.GrayText });

            _modelList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            _modelList.DoubleClick += async (s, e) => { if (_modelList.SelectedItem is string m) await LoadSingleModel(m); };
            _modelFilter = new TextBox { Dock = DockStyle.Top };
            SetCue(_modelFilter, "Filter models…");
            _modelFilter.TextChanged += (s, e) => FillModelList();
            var modelPage = new TabPage("Models");
            modelPage.Controls.Add(_modelList);
            modelPage.Controls.Add(_modelFilter);

            _leftTabs.TabPages.Add(levelPage);
            _leftTabs.TabPages.Add(scenePage);
            _leftTabs.TabPages.Add(packPage);
            _leftTabs.TabPages.Add(modelPage);

            // ---- right panel
            _rightTabs = new TabControl { Dock = DockStyle.Fill };
            _props = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 9f), BackColor = SystemColors.Window };
            _log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 9f), BackColor = SystemColors.Window };
            _levelInfo = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 9f), BackColor = SystemColors.Window };
            var levelInfoPage = new TabPage("Level");
            levelInfoPage.Controls.Add(_levelInfo);
            _rightTabs.TabPages.Add(levelInfoPage);
            var propPage = new TabPage("Properties");
            _refList = new ListBox
            {
                Dock = DockStyle.Fill, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 18, Cursor = Cursors.Hand, HorizontalScrollbar = true, HorizontalExtent = 2400,
            };
            _refList.DrawItem += DrawLinkItem;
            _refList.MouseClick += (s, e) =>
            {
                int i = _refList.IndexFromPoint(e.Location);
                if (i >= 0 && _refList.Items[i] is RefLink l) FollowLink(l);
            };
            var linkPanel = new Panel { Dock = DockStyle.Bottom, Height = 210 };
            var linkHeader = new Label { Dock = DockStyle.Top, Height = 18, Text = "Links — click to jump to an object" };
            linkPanel.Controls.Add(_refList);
            linkPanel.Controls.Add(linkHeader);
            propPage.Controls.Add(_props);
            propPage.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 5 });
            propPage.Controls.Add(linkPanel);
            var logPage = new TabPage("Log");
            logPage.Controls.Add(_log);
            _rightTabs.TabPages.Add(propPage);
            _rightTabs.TabPages.Add(logPage);

            // ---- GL viewport
            _gl = new GLControl(new GLControlSettings
            {
                APIVersion = new Version(3, 3, 0, 0),
                Profile = ContextProfile.Core,
                Flags = ContextFlags.ForwardCompatible,
                NumberOfSamples = 4,
                DepthBits = 24,
            })
            { Dock = DockStyle.Fill };
            _gl.Load += (s, e) =>
            {
                _gl.MakeCurrent();
                try { if (_gl.Context != null) { _gl.Context.SwapInterval = 1; _vsync = true; } } catch { _vsync = false; }
                _renderer.InitGl();
                _glReady = true;
            };
            _gl.Paint += (s, e) => RenderFrame();
            _gl.Resize += (s, e) => Redraw();
            _gl.MouseDown += GlMouseDown;
            _gl.MouseUp += GlMouseUp;
            _gl.MouseMove += GlMouseMove;
            _gl.MouseWheel += GlMouseWheel;
            _gl.KeyDown += (s, e) => { _keys.Add(e.KeyCode); if (e.KeyCode == Keys.F) FocusSelection(); if (e.KeyCode == Keys.Escape) Select(null, null); e.Handled = true; };
            _gl.KeyUp += (s, e) => _keys.Remove(e.KeyCode);
            _gl.LostFocus += (s, e) => _keys.Clear();

            var right = _rightSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.Panel2 };
            right.Panel1.Controls.Add(_gl);
            right.Panel2.Controls.Add(_rightTabs);

            var main = _mainSplit = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            main.Panel1.Controls.Add(_leftTabs);
            main.Panel2.Controls.Add(right);

            Controls.Add(main);
            Controls.Add(tools);
            Controls.Add(menu);
            Controls.Add(status);
            MainMenuStrip = menu;

            Load += (s, e) =>
            {
                main.SplitterDistance = 330;
                right.SplitterDistance = Math.Max(200, right.Width - 420);
            };
        }

        private const string ControlsHelp =
            "Right mouse drag   look around\n" +
            "W A S D            move\n" +
            "Q / E              down / up\n" +
            "Shift / Ctrl       faster / slower\n" +
            "Mouse wheel        change fly speed\n" +
            "Middle drag        pan\n" +
            "Left click         select object (or overlay)\n" +
            "F                  focus selection\n" +
            "Esc                clear selection\n\n" +
            "Scene tab: tick/untick chunks and categories, double-click an object to fly to it.";

        private ToolStripMenuItem Toggle(string text, Func<bool> get, Action<bool> set)
        {
            var item = new ToolStripMenuItem(text) { CheckOnClick = true, Checked = get() };
            item.CheckedChanged += (s, e) => { set(item.Checked); Redraw(); };
            item.DropDownOpening += (s, e) => item.Checked = get();
            return item;
        }

        private ToolStripButton ToolToggle(string text, Func<bool> get, Action<bool> set)
        {
            var b = new ToolStripButton(text) { CheckOnClick = true, Checked = get(), DisplayStyle = ToolStripItemDisplayStyle.Text };
            b.CheckedChanged += (s, e) => { set(b.Checked); Redraw(); };
            return b;
        }

        private static void SetCue(TextBox box, string cue)
        {
            // EM_SETCUEBANNER
            box.HandleCreated += (s, e) =>
            {
                try { SendMessage(box.Handle, 0x1501, (IntPtr)1, cue); } catch { }
            };
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        // =====================================================================================
        // Loading
        // =====================================================================================

        private async Task BrowseGameFolder()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Pick the folder with the game files (extracted folders, .BPK chunk packs and/or DATA*.RKV)" })
            {
                var last = LoadSetting("root");
                if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) dlg.SelectedPath = last;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                await OpenGameFolder(dlg.SelectedPath);
            }
        }

        private async Task OpenGameFolder(string root)
        {
            SetStatus("Indexing " + root + " …");
            UseWaitCursor = true;
            try
            {
                var game = await Task.Run(() => new GameData(root, msg => BeginInvoke((Action)(() => SetStatus(msg)))));
                _game = game;
                foreach (var l in game.Log) Log(l);
                _db = new ModelDb(game.ReadBni("global.model.bni"));
                if (!_db.Has("default")) Log("global.model.bni not found — object classes will be matched to models by name only.");
                _levels = LevelLoader.LoadLevelList(game);
                if (_levels.Count == 0) Log("levels.ini.bni not found — use the Packs / Models tabs.");
                _assets = new AssetCache(game);
                SaveSetting("root", root);
                FillLevelTree();
                _packList.Items.Clear();
                foreach (var p in game.ChunkPackNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) _packList.Items.Add(p);
                _allModels = game.AllNames(".mdl").Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                FillModelList();
                ShowLevelInfo(null, null);
                _rightTabs.SelectedIndex = 0;
                SetStatus($"{root}: {_levels.Count} levels, {_packList.Items.Count} chunk packs, {_allModels.Count} models");
                _leftTabs.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                Log("Failed to open game folder: " + ex);
                MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { UseWaitCursor = false; }
        }

        private void FillLevelTree()
        {
            _levelTree.BeginUpdate();
            _levelTree.Nodes.Clear();
            foreach (var lv in _levels)
            {
                var n = new TreeNode($"{lv.Name}  ({lv.Chunks.Count} chunks)") { Tag = lv };
                foreach (var c in lv.Chunks)
                {
                    bool has = _game.ChunkPack(c.Name) != null;
                    var cn = new TreeNode($"{c.Name}{(c.IsEnvironment ? "  [env]" : "")}{(has ? "" : "  (no .BPK found)")}  — {c.Overlays.Count} overlays")
                    { Tag = (lv, c), ForeColor = has ? Theme.Fore : Theme.Dim };
                    n.Nodes.Add(cn);
                }
                _levelTree.Nodes.Add(n);
            }
            _levelTree.EndUpdate();
            _levelTree.ForeColor = Theme.Fore;
        }

        private void FillModelList()
        {
            if (_modelList == null) return;
            var f = _modelFilter.Text.Trim();
            _modelList.BeginUpdate();
            _modelList.Items.Clear();
            foreach (var m in _allModels)
                if (f.Length == 0 || m.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) _modelList.Items.Add(m);
            _modelList.EndUpdate();
        }

        private async Task LevelTreeActivate(TreeNode node)
        {
            if (node?.Tag is LevelDef lv) await LoadLevel(lv, null);
            else if (node?.Tag is ValueTuple<LevelDef, ChunkDef> t)
                await LoadLevel(t.Item1, new HashSet<string>(new[] { t.Item2.Name }, StringComparer.OrdinalIgnoreCase));
        }

        private async Task LoadLevel(LevelDef lv, HashSet<string> onlyChunks)
        {
            if (_game == null) return;
            UseWaitCursor = true;
            SetStatus("Loading " + lv.Name + " …");
            var sw = Stopwatch.StartNew();
            try
            {
                _assets = new AssetCache(_game);
                var cache = _assets;
                var scene = await Task.Run(() => LevelLoader.Load(_game, lv, _db, cache,
                    msg => BeginInvoke((Action)(() => SetStatus(msg))), onlyChunks));
                ShowScene(scene, sw);
                if (lv.FogColor != null && lv.FogColor.Length >= 3)
                    _fogColor = Vec3.Min(new Vec3(lv.FogColor[0], lv.FogColor[1], lv.FogColor[2]) * 0.5f, Vec3.One);
                if (lv.FogFar > 0) _fogRange = new Vec2(lv.FogNear, lv.FogFar);
                // Start at the start chunk if it was loaded.
                var start = _renderer.Items.Where(i => i.Kind == ItemKind.Overlay && string.Equals(i.Chunk.Def.Name, lv.StartChunk, StringComparison.OrdinalIgnoreCase)).ToList();
                if (onlyChunks == null && start.Count > 0)
                {
                    var mn = start.Aggregate(new Vec3(float.MaxValue), (a, i) => Vec3.Min(a, i.WorldMin));
                    var mx = start.Aggregate(new Vec3(float.MinValue), (a, i) => Vec3.Max(a, i.WorldMax));
                    FrameWorld(mn, mx);
                }
                UpdateSpeedLabel();
                Redraw();
            }
            catch (Exception ex)
            {
                Log("Load failed: " + ex);
                MessageBox.Show(this, ex.Message, "Load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { UseWaitCursor = false; }
        }

        private async Task LoadPack(string chunk)
        {
            if (_game == null) return;
            UseWaitCursor = true;
            var sw = Stopwatch.StartNew();
            try
            {
                _assets = new AssetCache(_game);
                var cache = _assets;
                var scene = await Task.Run(() =>
                {
                    var sc = LevelLoader.LoadPack(_game, chunk, cache);
                    var lc3 = _game.ReadBni(chunk + ".lc3.bni", new[] { _game.ChunkPack(chunk) });
                    var ch = sc.Chunks[0];
                    if (lc3 != null)
                    {
                        ch.Layout = lc3;
                        int i = 0;
                        foreach (var s in lc3.Sections) ch.Objects.Add(LevelObject.FromSection(s, chunk, i++, _db));
                    }
                    ArrangeLooseModels(ch);
                    return sc;
                });
                ShowScene(scene, sw);
                FrameWorld(_renderer.SceneMin, _renderer.SceneMax);
                UpdateSpeedLabel();
                Redraw();
            }
            catch (Exception ex) { Log("Pack load failed: " + ex); }
            finally { UseWaitCursor = false; }
        }

        /// <summary>
        /// Chunk packs mix world-space level geometry with props modelled around the origin.
        /// Props are converted into objects laid out on a grid next to the level geometry.
        /// </summary>
        private static void ArrangeLooseModels(SceneChunk ch)
        {
            var world = new List<(OverlayRef, LoadedModel)>();
            var loose = new List<LoadedModel>();
            foreach (var (ov, m) in ch.Overlays)
            {
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
            foreach (var m in loose.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                var size = m.Model.BoundsSize;
                float w = Math.Max(size.X, 200) + 300;
                if (x - rowStart + w > maxRow) { x = rowStart; z += rowDepth + 300; rowDepth = 0; }
                var o = new LevelObject
                {
                    ClassName = m.Name, ChunkName = ch.Def.Name, Index = 100000 + idx++,
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

        private async Task LoadSingleModel(string name)
        {
            if (_game == null) return;
            var sw = Stopwatch.StartNew();
            _assets = new AssetCache(_game);
            var cache = _assets;
            var scene = await Task.Run(() =>
            {
                var m = cache.GetModel(name, null);
                var def = new LevelDef { Name = name };
                var cd = new ChunkDef { Name = name };
                def.Chunks.Add(cd);
                var sc = new LevelScene { Def = def, Assets = cache };
                var ch = new SceneChunk { Def = cd };
                if (m != null)
                {
                    var o = new LevelObject { ClassName = name, ChunkName = name, Section = new BniSection { Name = name }, Category = ObjectCategory.Prop };
                    ch.Objects.Add(o);
                    ch.ObjectModels[o] = m;
                }
                else sc.Warnings.Add("Could not load model " + name);
                sc.Chunks.Add(ch);
                sc.Warnings.AddRange(cache.Warnings);
                return sc;
            });
            ShowScene(scene, sw);
            FrameWorld(_renderer.SceneMin, _renderer.SceneMax);
            UpdateSpeedLabel();
            var it = _renderer.Items.FirstOrDefault();
            if (it != null) Select(it.Object, it);
            Redraw();
        }

        private void ShowScene(LevelScene scene, Stopwatch sw)
        {
            _scene = scene;
            if (_glReady) _gl.MakeCurrent();
            _renderer.Load(scene);
            _vs.ClearSceneVisibility();
            BuildSceneTree();
            _leftTabs.SelectedIndex = 1;
            int tris = _renderer.Items.Sum(i => i.Model.Parts.Sum(p => p.Triangles));
            int objs = scene.AllObjects.Count();
            SetStatus($"{scene.Def.Name}: {scene.Chunks.Count} chunks, {objs} objects, {_renderer.Items.Count} draw items, {tris:N0} triangles, {_renderer.Collisions.Count} collision meshes — {sw.ElapsedMilliseconds} ms");
            Log($"Loaded {scene.Def.Name} in {sw.ElapsedMilliseconds} ms");
            foreach (var w in scene.Warnings.Distinct()) Log("  warning: " + w);
            Select(null, null);
            ShowLevelInfo(_levels.Contains(scene.Def) ? scene.Def : null, _levels.Contains(scene.Def) ? scene : null);
            if (!_levels.Contains(scene.Def)) _levelInfo.Text = _props.Text;
            _rightTabs.SelectedIndex = 0;
        }

        // =====================================================================================
        // Scene tree
        // =====================================================================================

        /// <summary>Tag for Scene-tree nodes that belong to one chunk (groups, overlays, grounds, categories).</summary>
        private sealed class TreeTag
        {
            public string Chunk;
            public string Kind;      // "group", "overlay", "ground", "cat"
            public string Key;       // group key ("overlays", "objects", "grounds", "dome", "cat:X") or item name
            public object Payload;   // OverlayRef, Collision, ObjectCategory
        }

        private void BuildSceneTree()
        {
            if (_scene == null) return;
            string f = _sceneFilter.Text.Trim();
            _suppressTreeCheck = true;
            _sceneTree.BeginUpdate();
            _sceneTree.Nodes.Clear();
            foreach (var ch in _scene.Chunks)
            {
                string cname = ch.Def.Name;
                TreeNode Group(string text, string key) => new TreeNode(text)
                {
                    Tag = new TreeTag { Chunk = cname, Kind = "group", Key = key },
                    Checked = !_vs.GroupHidden(cname, key),
                };
                var cn = new TreeNode(cname + (ch.Def.IsEnvironment ? "  [env]" : "")) { Tag = ch, Checked = !_vs.HiddenChunks.Contains(cname) };
                if (f.Length == 0)
                {
                    if (ch.Overlays.Count > 0)
                    {
                        var on = Group($"Overlays ({ch.Overlays.Count})", "overlays");
                        foreach (var (ov, m) in ch.Overlays)
                            on.Nodes.Add(new TreeNode(m == null ? ov.Name + " (missing)" : $"{ov}  — {m.Model.TriangleCount:N0} tris")
                            {
                                Tag = new TreeTag { Chunk = cname, Kind = "overlay", Key = ov.Name, Payload = ov },
                                Checked = !_vs.HiddenItems.Contains((cname, ov.Name)),
                            });
                        cn.Nodes.Add(on);
                    }
                    if (ch.Grounds.Count > 0)
                    {
                        var gn = Group($"Ground collision ({ch.Grounds.Count})", "grounds");
                        foreach (var (n, c) in ch.Grounds)
                            gn.Nodes.Add(new TreeNode($"{n}  — {c.Triangles.Count} tris [{string.Join(", ", c.Materials)}]")
                            {
                                Tag = new TreeTag { Chunk = cname, Kind = "ground", Key = n, Payload = c },
                                Checked = !_vs.HiddenItems.Contains((cname, n)),
                            });
                        cn.Nodes.Add(gn);
                    }
                    if (ch.EnvDome != null) cn.Nodes.Add(Group("Sky dome: " + ch.EnvDome.Name, "dome"));
                }
                var objs = ch.Objects.Where(o => Matches(o, f)).ToList();
                if (objs.Count > 0)
                {
                    var objNode = Group($"Objects ({objs.Count})", "objects");
                    foreach (var grp in objs.GroupBy(o => o.Category).OrderBy(g => g.Key))
                    {
                        var gnode = new TreeNode($"{grp.Key} ({grp.Count()})")
                        {
                            Tag = new TreeTag { Chunk = cname, Kind = "cat", Key = grp.Key.ToString(), Payload = grp.Key },
                            Checked = !_vs.HiddenChunkCategories.Contains((cname, grp.Key)),
                        };
                        foreach (var o in grp)
                        {
                            string model = ch.ObjectModels.TryGetValue(o, out var lm) ? "  → " + lm.Name : "";
                            gnode.Nodes.Add(new TreeNode($"{o.DisplayName}{model}") { Tag = o, Checked = !_vs.HiddenObjects.Contains(o) });
                        }
                        objNode.Nodes.Add(gnode);
                    }
                    cn.Nodes.Add(objNode);
                    if (f.Length > 0) { cn.Expand(); objNode.ExpandAll(); }
                }
                _sceneTree.Nodes.Add(cn);
            }
            _sceneTree.EndUpdate();
            _suppressTreeCheck = false;
        }

        private static bool Matches(LevelObject o, string f)
        {
            if (f.Length == 0) return true;
            return o.ClassName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                || (o.IdName != null && o.IdName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                || o.Id.ToString() == f;
        }

        private void SceneTreeAfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_suppressTreeCheck || e.Action == TreeViewAction.Unknown) return;
            var n = e.Node;
            bool on = n.Checked;
            void Toggle<T>(HashSet<T> set, T key) { if (on) set.Remove(key); else set.Add(key); }
            bool linesDirty = false;
            switch (n.Tag)
            {
                case SceneChunk ch:
                    Toggle(_vs.HiddenChunks, ch.Def.Name);
                    break;
                case TreeTag t when t.Kind == "group":
                    // Only this chunk's group — other chunks are unaffected.
                    Toggle(_vs.HiddenGroups, (t.Chunk, t.Key));
                    break;
                case TreeTag t when t.Kind == "cat":
                    Toggle(_vs.HiddenChunkCategories, (t.Chunk, (ObjectCategory)t.Payload));
                    break;
                case TreeTag t when t.Kind == "overlay" || t.Kind == "ground":
                    Toggle(_vs.HiddenItems, (t.Chunk, t.Key));
                    break;
                case LevelObject o:
                    if (on) _vs.HiddenObjects.Remove(o); else _vs.HiddenObjects.Add(o);
                    linesDirty = true;
                    break;
            }
            if (linesDirty && _glReady) { _gl.MakeCurrent(); _renderer.RebuildLines(_vs); }
            Redraw();
        }

        private void SceneTreeSelect(TreeNode n, bool focus)
        {
            if (n?.Tag is LevelObject o)
            {
                Select(o, null);
                if (focus) FocusSelection();
            }
            else if (n?.Tag is TreeTag tt && tt.Payload is OverlayRef ov)
            {
                var it = _renderer.Items.FirstOrDefault(i => i.Kind == ItemKind.Overlay && i.Name == ov.Name && i.Chunk.Def.Name == tt.Chunk);
                if (it != null)
                {
                    Select(null, it);
                    if (focus) { FrameWorld(it.WorldMin, it.WorldMax); UpdateSpeedLabel(); Redraw(); }
                }
            }
            else if (n?.Tag is TreeTag tg && tg.Payload is Collision c)
            {
                ShowCollisionInfo(c);
                if (focus) { FrameWorld(c.BoundsMin, c.BoundsMax); UpdateSpeedLabel(); Redraw(); }
            }
        }

        // =====================================================================================
        // Selection / properties
        // =====================================================================================

        private void Select(LevelObject o, DrawItem item)
        {
            if (_glReady) _gl.MakeCurrent();
            _renderer.SetSelection(o, item);
            ShowProperties(o, item ?? _renderer.SelectedItem);
            Redraw();
        }

        /// <summary>Level overview shown in the "Level" tab: levels.ini entry, chunks and (when loaded) scene stats.</summary>
        private void ShowLevelInfo(LevelDef lv, LevelScene scene)
        {
            var sb = new StringBuilder();
            if (lv == null)
            {
                if (_game != null)
                {
                    sb.AppendLine("GAME DATA");
                    sb.AppendLine(new string('=', 9));
                    sb.AppendLine("folder   " + _game.Root);
                    sb.AppendLine($"levels   {_levels.Count}");
                    sb.AppendLine($"packs    {_packList.Items.Count}");
                    sb.AppendLine($"models   {_allModels.Count}");
                    sb.AppendLine();
                    foreach (var l in _levels)
                    {
                        int have = l.Chunks.Count(c => _game.ChunkPack(c.Name) != null);
                        sb.AppendLine($"{l.Name,-20} {l.Chunks.Count,3} chunks ({have} packs found)  start {l.StartChunk}");
                    }
                    sb.AppendLine();
                    sb.AppendLine("Click a level in the Levels tab to see its details, double-click to load it.");
                }
                _levelInfo.Text = sb.ToString().Replace("\n", "\r\n");
                return;
            }
            sb.AppendLine("LEVEL " + lv.Name + (scene != null ? "   (loaded)" : "   (not loaded — double-click to load)"));
            sb.AppendLine(new string('=', 6 + lv.Name.Length));
            sb.AppendLine($"start chunk   {lv.StartChunk}");
            sb.AppendLine($"env chunk     {lv.EnvChunk}");
            if (lv.FogFar > 0) sb.AppendLine($"fog planes    {lv.FogNear} .. {lv.FogFar}");
            if (lv.FogColor != null) sb.AppendLine($"fog colour    {string.Join(" ", lv.FogColor)}");
            sb.AppendLine();
            sb.AppendLine("levels.ini entry:");
            if (lv.Node != null) AppendNode(sb, lv.Node, 0, skipChildrenNamed: "chunk");
            sb.AppendLine();
            sb.AppendLine($"CHUNKS ({lv.Chunks.Count})");
            foreach (var c in lv.Chunks)
            {
                var pack = _game?.ChunkPack(c.Name);
                var sc = scene?.Chunks.FirstOrDefault(x => x.Def == c);
                sb.Append($"  {c.Name}{(c.IsEnvironment ? " [env]" : "")}  pack: {(pack == null ? "MISSING" : Path.GetFileName(pack.SourcePath))}");
                if (sc != null)
                {
                    sb.Append($"  objects: {sc.Objects.Count}  with models: {sc.ObjectModels.Count}");
                }
                sb.AppendLine();
                if (c.EnvDome != null) sb.AppendLine($"      sky dome  {c.EnvDome}");
                if (c.Overlays.Count > 0) sb.AppendLine($"      overlays  {string.Join(", ", c.Overlays)}");
                if (c.Grounds.Count > 0) sb.AppendLine($"      ground    {string.Join(", ", c.Grounds)}");
                if (sc != null && sc.Objects.Count > 0)
                    sb.AppendLine("      " + string.Join("  ", sc.Objects.GroupBy(o => o.Category).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")));
                foreach (var extra in c.Node.Children.Where(k => k.Key != null && !new[] { "overlay", "ground", "envdome" }.Contains(k.Key.ToLowerInvariant())))
                    sb.AppendLine($"      {extra}");
            }
            if (scene != null)
            {
                int tris = _renderer.Items.Sum(i => i.Model.Parts.Sum(p => p.Triangles));
                sb.AppendLine();
                sb.AppendLine($"SCENE  {scene.AllObjects.Count()} objects, {_renderer.Items.Count} draw items, {tris:N0} triangles, {_renderer.Collisions.Count} collision meshes");
                var warns = scene.Warnings.Distinct().ToList();
                if (warns.Count > 0)
                {
                    sb.AppendLine($"WARNINGS ({warns.Count})");
                    foreach (var w in warns.Take(80)) sb.AppendLine("  " + w);
                }
            }
            _levelInfo.Text = sb.ToString().Replace("\n", "\r\n");
        }

        private void ShowProperties(LevelObject o, DrawItem item)
        {
            var sb = new StringBuilder();
            if (o == null && item == null)
            {
                if (_scene != null)
                {
                    sb.AppendLine(_scene.Def.Name);
                    sb.AppendLine(new string('=', _scene.Def.Name.Length));
                    if (_scene.Def.Node != null) { AppendNode(sb, _scene.Def.Node, 0, skipChildrenNamed: "chunk"); }
                    sb.AppendLine();
                    foreach (var ch in _scene.Chunks)
                        sb.AppendLine($"{ch.Def.Name,-24} {ch.Overlays.Count,3} overlays  {ch.Objects.Count,5} objects  {ch.Grounds.Count,3} grounds  pack: {(ch.Pack == null ? "-" : Path.GetFileName(ch.Pack.SourcePath))}");
                }
                _props.Text = sb.ToString().Replace("\n", "\r\n");
                if (_rightTabs.SelectedIndex == 1) _rightTabs.SelectedIndex = 0;
                FillLinks(null);
                return;
            }
            if (_rightTabs.SelectedIndex == 0) _rightTabs.SelectedIndex = 1;
            if (o != null)
            {
                sb.AppendLine(o.DisplayName);
                sb.AppendLine(new string('=', Math.Min(o.DisplayName.Length, 80)));
                sb.AppendLine($"chunk     {o.ChunkName}   (entry #{o.Index} in {o.ChunkName}.lc3.bni)");
                sb.AppendLine($"category  {o.Category}");
                sb.AppendLine($"position  {V(o.Position)}");
                sb.AppendLine($"rotation  {V(o.Rotation)} rad  ({V(o.Rotation * (180f / MathF.PI))} deg)");
                sb.AppendLine($"scale     {V(o.Scale)}{(o.ScaleIsVolume ? "  (swarm volume, not model scale)" : "")}");
                if (o.Color.HasValue) sb.AppendLine($"color     {V(o.Color.Value)}");
                if (_db != null)
                {
                    var chain = _db.AliasChain(o.ClassName).ToList();
                    if (chain.Count > 1 || _db.Has(o.ClassName)) sb.AppendLine($"class     {string.Join(" → ", chain)}");
                    var cands = _db.ModelCandidates(o.ClassName);
                    sb.AppendLine($"model     candidates: {string.Join(", ", cands)}");
                }
            }
            if (item != null)
            {
                var lm = item.Model.Source;
                sb.AppendLine();
                sb.AppendLine($"MODEL {lm.Name}   ({lm.Source})");
                sb.AppendLine($"  {lm.Model.VertexCount:N0} verts, {lm.Model.TriangleCount:N0} tris, {lm.Model.Meshes.Count} meshes, {lm.Model.AnimNodePositions.Count} anim nodes");
                sb.AppendLine($"  bounds {V(lm.Model.BoundsMin)} .. {V(lm.Model.BoundsMax)}");
                sb.AppendLine("  components:");
                foreach (var c in lm.Model.Components)
                    sb.AppendLine($"    {c.Name}{(c.IsCollision ? "  [collision]" : c.IsFragment ? "  [fragment]" : "")}  meshes={c.Meshes.Count} bones={c.BoneCount}");
                sb.AppendLine("  materials:");
                foreach (var m in lm.Materials)
                {
                    var t = _assets?.GetTexture(m.TextureName);
                    sb.AppendLine($"    {m.Name}  tex={m.TextureName}{(t == null ? " (missing)" : $" {t.Width}x{t.Height} fmt{t.Format:X2}")}  blend={m.Blend} flags=0x{m.Flags:X8}{(m.DoubleSided ? " 2-sided" : "")}{(m.AlphaTest ? " alpha-test" : "")}{(m.Scrolls ? $" scroll({m.ScrollU},{m.ScrollV})" : "")}");
                }
                if (lm.Model.RefPoints.Count > 0)
                {
                    sb.AppendLine("  ref points:");
                    foreach (var r in lm.Model.RefPoints) sb.AppendLine($"    {r.Name}  {V(r.Position)}");
                }
                if (lm.Collisions.Count > 0)
                    sb.AppendLine($"  collision: {string.Join(", ", lm.Collisions.Select(c => $"{c.Name} ({c.Triangles.Count} tris: {string.Join("/", c.Materials)})"))}");
            }
            if (o != null && o.Section != null && o.Section.Fields.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("LC3 ENTRY");
                sb.AppendLine("name " + o.ClassName);
                foreach (var f in o.Section.Fields) AppendNode(sb, f, 1);
                var scripts = o.ScriptLines().ToList();
                if (scripts.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("SCRIPTS");
                    foreach (var s in scripts) sb.AppendLine("  " + s);
                }
            }
            _props.Text = sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
            FillLinks(o);
        }

        private sealed class RefLink
        {
            public LevelObject Target;
            public string Text;
            public bool Header;
            public override string ToString() => Text;
        }

        private static readonly char[] ScriptSeparators = { ' ', '(', ')', ';', ',', '\t' };

        private static IEnumerable<string> Tokens(BniNode line)
        {
            if (line.Key != null && line.Key.Equals("ScriptCommand", StringComparison.OrdinalIgnoreCase))
                return line.JoinedValues.Split(ScriptSeparators, StringSplitOptions.RemoveEmptyEntries);
            return line.Values;
        }

        private static bool IsPositional(BniNode line) =>
            line.Key != null && (line.Key.Equals("pos", StringComparison.OrdinalIgnoreCase) || line.Key.Equals("rot", StringComparison.OrdinalIgnoreCase)
                                 || line.Key.Equals("scale", StringComparison.OrdinalIgnoreCase) || line.Key.Equals("color", StringComparison.OrdinalIgnoreCase)
                                 || line.Key.Equals("waypoint", StringComparison.OrdinalIgnoreCase) || line.Key.Equals("ID", StringComparison.OrdinalIgnoreCase));

        /// <summary>Objects whose fields or scripts mention the target's ID number.</summary>
        private IEnumerable<(LevelObject obj, BniNode line)> FindReferences(LevelObject target)
        {
            string id = target.Id.ToString();
            foreach (var o in _scene.AllObjects)
            {
                if (o == target) continue;
                foreach (var f in o.Section.Fields)
                    foreach (var line in Flatten(f))
                        if (!IsPositional(line) && Tokens(line).Any(t => t == id))
                        {
                            yield return (o, line);
                            goto next;
                        }
            next:;
            }
        }

        /// <summary>Objects this object points at (ID numbers in its fields and ScriptCommands).</summary>
        private IEnumerable<(LevelObject obj, BniNode line)> FindOutgoing(LevelObject src)
        {
            var byId = _scene.AllObjects.Where(x => x.Id != 0).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.ToList());
            var seen = new HashSet<LevelObject>();
            foreach (var f in src.Section.Fields)
                foreach (var line in Flatten(f))
                {
                    if (IsPositional(line)) continue;
                    foreach (var t in Tokens(line))
                        if (int.TryParse(t, out int id) && id > 0 && byId.TryGetValue(id, out var targets))
                            foreach (var tgt in targets)
                                if (tgt != src && seen.Add(tgt)) yield return (tgt, line);
                }
        }

        private void FillLinks(LevelObject o)
        {
            _refList.BeginUpdate();
            _refList.Items.Clear();
            if (o != null && _scene != null && o.Section != null)
            {
                var outgoing = FindOutgoing(o).ToList();
                if (outgoing.Count > 0)
                {
                    _refList.Items.Add(new RefLink { Header = true, Text = $"REFERENCES ({outgoing.Count})" });
                    foreach (var (t, line) in outgoing) _refList.Items.Add(new RefLink { Target = t, Text = $"→ {t.DisplayName}  ({t.ChunkName})   via {line}" });
                }
                if (o.Id != 0)
                {
                    var incoming = FindReferences(o).ToList();
                    if (incoming.Count > 0)
                    {
                        _refList.Items.Add(new RefLink { Header = true, Text = $"REFERENCED BY ({incoming.Count})" });
                        foreach (var (src, line) in incoming) _refList.Items.Add(new RefLink { Target = src, Text = $"← {src.DisplayName}  ({src.ChunkName})   {line}" });
                    }
                }
            }
            if (_refList.Items.Count == 0) _refList.Items.Add(new RefLink { Header = true, Text = o == null ? "" : "No links to or from this object." });
            _refList.EndUpdate();
        }

        private void DrawLinkItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= _refList.Items.Count) return;
            var l = (RefLink)_refList.Items[e.Index];
            var font = l.Header ? new Font(_refList.Font, FontStyle.Bold) : new Font(_refList.Font, FontStyle.Underline);
            var col = l.Header ? Theme.Fore : (Theme.Dark ? Color.FromArgb(110, 170, 255) : Color.FromArgb(0, 90, 200));
            if ((e.State & DrawItemState.Selected) != 0 && !l.Header) col = SystemColors.HighlightText;
            TextRenderer.DrawText(e.Graphics, l.Text, font, e.Bounds, col, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            font.Dispose();
        }

        private void FollowLink(RefLink link)
        {
            if (link?.Target == null) return;
            _vs.HiddenObjects.Remove(link.Target);
            Select(link.Target, null);
            FocusSelection();
            SelectTreeNode(link.Target);
        }

        private static IEnumerable<BniNode> Flatten(BniNode n)
        {
            yield return n;
            foreach (var c in n.Children)
                foreach (var x in Flatten(c)) yield return x;
        }

        private static void AppendNode(StringBuilder sb, BniNode n, int depth, string skipChildrenNamed = null)
        {
            if (skipChildrenNamed != null && string.Equals(n.Key, skipChildrenNamed, StringComparison.OrdinalIgnoreCase)) return;
            if (depth > 0) sb.Append(' ', depth * 2).AppendLine(n.ToString());
            foreach (var c in n.Children) AppendNode(sb, c, depth + 1, skipChildrenNamed);
        }

        private void ShowCollisionInfo(Collision c)
        {
            var sb = new StringBuilder();
            sb.AppendLine("COLLISION " + c.Name);
            sb.AppendLine($"grid {c.CellsX}x{c.CellsY}x{c.CellsZ}, cell {V(c.CellSize)}");
            sb.AppendLine($"bounds {V(c.BoundsMin)} .. {V(c.BoundsMax)}");
            sb.AppendLine($"{c.Vertices.Count} vertices, {c.Triangles.Count} unique triangles");
            sb.AppendLine("materials: " + string.Join(", ", c.Materials));
            sb.AppendLine();
            sb.AppendLine("triangle flags (low byte = material index):");
            foreach (var g in c.Triangles.GroupBy(t => t.Flags).OrderBy(g => g.Key))
                sb.AppendLine($"  0x{g.Key:X4}  {c.MaterialName(g.First()),-16} {g.Count()}");
            _props.Text = sb.ToString().Replace("\n", "\r\n");
        }

        private static string V(Vec3 v) => $"{v.X:0.###} {v.Y:0.###} {v.Z:0.###}";

        /// <summary>Frame a box given in game (world) coordinates; the camera lives in display space, which is X-mirrored when MirrorX is on.</summary>
        private void FrameWorld(Vec3 min, Vec3 max)
        {
            if (_vs.MirrorX)
            {
                var a = new Vec3(-max.X, min.Y, min.Z);
                var b = new Vec3(-min.X, max.Y, max.Z);
                min = a; max = b;
            }
            _cam.Frame(min, max);
        }

        private Vec3 CamWorldPosition => _vs.MirrorX ? _cam.Position * new Vec3(-1, 1, 1) : _cam.Position;

        private void SetMirror(bool on)
        {
            if (on == _vs.MirrorX) return;
            // keep looking at the same place when toggling
            _cam.Position *= new Vec3(-1, 1, 1);
            _cam.Yaw = -_cam.Yaw;
            _vs.MirrorX = on;
        }

        private void FocusSelection()
        {
            Vec3 min, max;
            if (_renderer.SelectedItem != null) { min = _renderer.SelectedItem.WorldMin; max = _renderer.SelectedItem.WorldMax; }
            else if (_renderer.Selected != null)
            {
                var o = _renderer.Selected;
                min = o.Position - new Vec3(300); max = o.Position + new Vec3(300);
                foreach (var s in o.Shapes)
                {
                    foreach (var p in s.Points) { min = Vec3.Min(min, p); max = Vec3.Max(max, p); }
                    if (s.Kind == ShapeKind.Sphere) { min = Vec3.Min(min, s.Center - new Vec3(s.Radius)); max = Vec3.Max(max, s.Center + new Vec3(s.Radius)); }
                    if (s.Kind == ShapeKind.Box) { var h = Vec3.Abs(s.Size) * 0.6f; min = Vec3.Min(min, s.Center - h); max = Vec3.Max(max, s.Center + h); }
                }
            }
            else return;
            var spd = _cam.Speed;
            FrameWorld(min, max);
            _cam.Speed = Math.Min(spd, _cam.Speed * 2);
            UpdateSpeedLabel();
            Redraw();
        }

        // =====================================================================================
        // Rendering / input
        // =====================================================================================

        private bool _dirty = true;

        /// <summary>F11: viewport only, borderless, covering the screen. F11 or Esc to return.</summary>
        private void ToggleFullscreen()
        {
            _fullscreen = !_fullscreen;
            SuspendLayout();
            if (_fullscreen)
            {
                _prevState = WindowState;
                _prevBorder = FormBorderStyle;
                _menu.Visible = _tools.Visible = _statusStrip.Visible = false;
                _mainSplit.Panel1Collapsed = true;
                _rightSplit.Panel2Collapsed = true;
                FormBorderStyle = FormBorderStyle.None;
                if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;   // re-maximise to cover the taskbar
                WindowState = FormWindowState.Maximized;
            }
            else
            {
                _menu.Visible = _tools.Visible = _statusStrip.Visible = true;
                _mainSplit.Panel1Collapsed = false;
                _rightSplit.Panel2Collapsed = false;
                FormBorderStyle = _prevBorder;
                WindowState = FormWindowState.Normal;
                WindowState = _prevState;
            }
            ResumeLayout(true);
            _gl.Focus();
            Redraw();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F11) { ToggleFullscreen(); return true; }
            if (keyData == Keys.Escape && _fullscreen) { ToggleFullscreen(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void Redraw()
        {
            _dirty = true;
            _gl?.Invalidate();
        }

        // ---- smooth render loop: render continuously from Application.Idle while the camera moves
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativeMessage { public IntPtr Handle; public uint Msg; public IntPtr WParam; public IntPtr LParam; public uint Time; public System.Drawing.Point Location; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int PeekMessage(out NativeMessage msg, IntPtr hWnd, uint min, uint max, uint remove);

        private static bool AppIdle => PeekMessage(out _, IntPtr.Zero, 0, 0, 0) == 0;

        private bool NeedsContinuous =>
            _keys.Count > 0 || _rmb || _mmb || _animate || _renderer.SelectedItem != null || _renderer.Selected != null;

        private void OnIdle(object sender, EventArgs e)
        {
            while (_glReady && AppIdle)
            {
                if (WindowState == FormWindowState.Minimized) return;
                bool cont = NeedsContinuous;
                if (!cont && !_dirty) { _lastTick = _clock.Elapsed.TotalSeconds; return; }
                UpdateMovement();
                RenderFrame();
                if (!cont) return;
                if (!_vsync) System.Threading.Thread.Sleep(1);
            }
        }

        private void RenderFrame()
        {
            if (!_glReady) return;
            if (WindowState == FormWindowState.Minimized || _gl.ClientSize.Width < 1 || _gl.ClientSize.Height < 1) return;
            _gl.MakeCurrent();
            try
            {
                _renderer.Render(_cam, _gl.ClientSize.Width, _gl.ClientSize.Height, _vs, _clear, _fogColor, _fogRange);
            }
            catch (Exception ex)
            {
                _glReady = false;
                Log("Render error: " + ex);
                MessageBox.Show(this, ex.Message, "Render error");
            }
            _gl.SwapBuffers();
            _dirty = false;
            _frames++;
            double now = _clock.Elapsed.TotalSeconds;
            if (now - _statusTime > 0.25)   // updating the status strip every frame causes layout hitches
            {
                double fps = _frames / (now - _statusTime);
                _frames = 0; _statusTime = now;
                var p = CamWorldPosition;
                _camStatus.Text = $"cam {p.X:0} {p.Y:0} {p.Z:0}   {_renderer.LastDrawnTriangles:N0} tris   {fps:0} fps";
            }
        }

        private int _frames;
        private double _statusTime;
        private bool _vsync;

        private void UpdateMovement()
        {
            double now = _clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.05, now - _lastTick);
            _lastTick = now;
            if (!_gl.Focused && _keys.Count > 0) _keys.Clear();
            var move = Vec3.Zero;
            if (_keys.Contains(Keys.W)) move += _cam.Forward;
            if (_keys.Contains(Keys.S)) move -= _cam.Forward;
            if (_keys.Contains(Keys.D)) move += _cam.Right;
            if (_keys.Contains(Keys.A)) move -= _cam.Right;
            if (_keys.Contains(Keys.E) || _keys.Contains(Keys.Space)) move += Vec3.UnitY;
            if (_keys.Contains(Keys.Q)) move -= Vec3.UnitY;
            if (move != Vec3.Zero)
            {
                float speed = _cam.Speed;
                if ((ModifierKeys & Keys.Shift) != 0) speed *= 4;
                if ((ModifierKeys & Keys.Control) != 0) speed *= 0.25f;
                _cam.Position += Vec3.Normalize(move) * speed * dt;
            }
        }

        private void GlMouseDown(object sender, MouseEventArgs e)
        {
            _gl.Focus();
            _lastMouse = _downMouse = e.Location;
            if (e.Button == MouseButtons.Right) _rmb = true;
            if (e.Button == MouseButtons.Middle) _mmb = true;
            if (e.Button == MouseButtons.Left) _lmb = true;
        }

        private void GlMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) _rmb = false;
            if (e.Button == MouseButtons.Middle) _mmb = false;
            if (e.Button == MouseButtons.Left)
            {
                _lmb = false;
                if (Math.Abs(e.X - _downMouse.X) + Math.Abs(e.Y - _downMouse.Y) < 4) PickAt(e.X, e.Y);
            }
        }

        private void GlMouseMove(object sender, MouseEventArgs e)
        {
            int dx = e.X - _lastMouse.X, dy = e.Y - _lastMouse.Y;
            _lastMouse = e.Location;
            if (_rmb || (_lmb && (ModifierKeys & Keys.Alt) != 0))
            {
                _cam.Rotate(dx * 0.005f, -dy * 0.005f);
                _dirty = true;
            }
            else if (_mmb)
            {
                float k = _cam.Speed * 0.002f;
                _cam.Position += (-_cam.Right * dx + _cam.Up * dy) * k;
                _dirty = true;
            }
        }

        private void GlMouseWheel(object sender, MouseEventArgs e)
        {
            _cam.Speed = Math.Clamp(_cam.Speed * (e.Delta > 0 ? 1.25f : 0.8f), 50f, 200000f);
            UpdateSpeedLabel();
        }

        private void UpdateSpeedLabel() => _speedLabel.Text = $"Speed: {_cam.Speed:0} (wheel)";

        private void PickAt(int x, int y)
        {
            if (_scene == null) return;
            _cam.Ray(x, y, _gl.ClientSize.Width, _gl.ClientSize.Height, out var o, out var d);
            if (_vs.MirrorX) { /* ray is in display space; renderer converts */ }
            var hit = _renderer.Pick(o, d, _vs);
            if (hit == null) { Select(null, null); SetStatus("Nothing under cursor"); return; }
            Select(hit.Object, hit.Item);
            SetStatus($"Picked {hit.Description} at {V(hit.Point)} (distance {hit.Distance:0})");
            if (hit.Object != null) SelectTreeNode(hit.Object);
        }

        private void SelectTreeNode(LevelObject o)
        {
            foreach (TreeNode c in _sceneTree.Nodes)
                foreach (TreeNode sub in c.Nodes)
                    foreach (TreeNode g in sub.Nodes)
                        foreach (TreeNode n in g.Nodes)
                            if (n.Tag == o)
                            {
                                _suppressTreeCheck = true;
                                _sceneTree.SelectedNode = n;
                                n.EnsureVisible();
                                _suppressTreeCheck = false;
                                return;
                            }
        }

        private void PickBackground()
        {
            using (var dlg = new ColorDialog { Color = Color.FromArgb((int)(_clear.X * 255), (int)(_clear.Y * 255), (int)(_clear.Z * 255)) })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _clear = new Vec3(dlg.Color.R / 255f, dlg.Color.G / 255f, dlg.Color.B / 255f);
                Redraw();
            }
        }

        // =====================================================================================
        // Export
        // =====================================================================================

        private void ExportScene()
        {
            if (_scene == null) return;
            using (var dlg = new SaveFileDialog { Filter = "glTF binary (*.glb)|*.glb", FileName = _scene.Def.Name + ".glb" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var ex = new GlbExporter(_scene.Assets)
                {
                    IncludeGroundCollision = _vs.GroundCollision,
                    IncludeCollisionComponents = _vs.ModelCollisionComponents,
                    IncludeFragments = _vs.Fragments,
                };
                var visible = new LevelScene { Def = _scene.Def, Assets = _scene.Assets };
                visible.Chunks.AddRange(_scene.Chunks.Where(c => !_vs.HiddenChunks.Contains(c.Def.Name)));
                ex.AddScene(visible, _vs.Objects);
                ex.Save(dlg.FileName);
                SetStatus("Exported " + dlg.FileName);
            }
        }

        private void ExportSelectedModel()
        {
            var it = _renderer.SelectedItem;
            if (it == null) { MessageBox.Show(this, "Select an object or overlay first (left click)."); return; }
            using (var dlg = new SaveFileDialog { Filter = "glTF binary (*.glb)|*.glb", FileName = it.Model.Source.Name + ".glb" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var ex = new GlbExporter(_scene.Assets) { IncludeCollisionComponents = _vs.ModelCollisionComponents, IncludeFragments = _vs.Fragments };
                ex.AddModel(it.Model.Source, Mat4.Identity, it.Model.Source.Name);
                ex.Save(dlg.FileName);
                SetStatus("Exported " + dlg.FileName);
            }
        }

        private void ExportTextures()
        {
            if (_scene == null) return;
            using (var dlg = new FolderBrowserDialog { Description = "Folder for PNG textures" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                int n = 0;
                var names = _renderer.Items.SelectMany(i => i.Model.Source.Materials).Select(m => m.TextureName).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var name in names)
                {
                    var t = _scene.Assets.GetTexture(name);
                    if (t == null) continue;
                    Png.Save(Path.Combine(dlg.SelectedPath, name + ".png"), t.Rgba, t.Width, t.Height);
                    n++;
                }
                SetStatus($"Wrote {n} textures to {dlg.SelectedPath}");
            }
        }

        private void ExportLayouts()
        {
            if (_scene == null) return;
            using (var dlg = new FolderBrowserDialog { Description = "Folder for decompiled .lc3 text" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                int n = 0;
                foreach (var ch in _scene.Chunks.Where(c => c.Layout != null))
                {
                    File.WriteAllText(Path.Combine(dlg.SelectedPath, ch.Def.Name + ".lc3.txt"), ch.Layout.ToText());
                    n++;
                }
                SetStatus($"Wrote {n} layout files to {dlg.SelectedPath}");
            }
        }

        private void SaveScreenshot()
        {
            if (!_glReady) return;
            using (var dlg = new SaveFileDialog { Filter = "PNG image (*.png)|*.png", FileName = (_scene?.Def.Name ?? "screenshot") + ".png" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _gl.MakeCurrent();
                int w = _gl.ClientSize.Width, h = _gl.ClientSize.Height;
                _renderer.Render(_cam, w, h, _vs, _clear, _fogColor, _fogRange);
                var px = SceneRenderer.ReadPixels(w, h);
                Png.Save(dlg.FileName, px, w, h);
                SetStatus("Saved " + dlg.FileName);
                Redraw();
            }
        }

        // =====================================================================================
        // misc
        // =====================================================================================

        private void SetStatus(string s) { if (_status != null) _status.Text = s; }

        private void Log(string s)
        {
            if (_log == null) return;
            _log.AppendText(s.Replace("\n", "\r\n") + "\r\n");
        }

        private static string LoadSetting(string key)
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0 && line.Substring(0, i) == key) return line.Substring(i + 1);
                }
            }
            catch { }
            return null;
        }

        private static void SaveSetting(string key, string value)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                var lines = File.Exists(SettingsPath) ? File.ReadAllLines(SettingsPath).Where(l => !l.StartsWith(key + "=")).ToList() : new List<string>();
                lines.Add(key + "=" + value);
                File.WriteAllLines(SettingsPath, lines);
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Application.Idle -= OnIdle;
            if (_glReady)
            {
                _gl.MakeCurrent();
                _renderer.Dispose();
            }
            base.OnFormClosed(e);
        }
    }
}
