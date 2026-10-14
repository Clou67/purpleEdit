using System;
using System.Collections.Generic;
using System.Linq;
using Hexa.NET.ImGui;
using Mk2.Game;
using Mk2.Viewer.Rendering;
using Mk2.Viewer.ImGuiUi.Frames;
using Mk2.Viewer.ImGuiUi;
using Mk2.Viewer.Utils;
using Mk2.Formats;

namespace Mk2.Viewer.Frames {
    public sealed class MainFrame : IDisposable {
        private readonly Window _window;
        private readonly ImGuiController _controller;
        private readonly List<Frame> _openFrames = new List<Frame>();

        public ViewSettings ViewSettings;
        public Camera Camera;
        public SceneRenderer Renderer;
        public Action Redraw = () => { };

        public System.Numerics.Vector3 ClearColor;
        public System.Numerics.Vector3 FogColor;
        public System.Numerics.Vector2 FogRange;

        public Func<IEnumerable<LevelDef>> GetLevels = () => Enumerable.Empty<LevelDef>();
        public Func<IEnumerable<string>> GetPacks = () => Enumerable.Empty<string>();
        public Func<IEnumerable<string>> GetModels = () => Enumerable.Empty<string>();
        public Func<IEnumerable<string>> GetAnimations = () => Enumerable.Empty<string>();
        public Func<IEnumerable<string>> GetSkeletons = () => Enumerable.Empty<string>();
        public Action<LevelDef> OnLoadLevel = _ => { };
        public Action<string> OnLoadPack = _ => { };
        public Action<string> OnLoadModel = _ => { };
        public Action<string> OnLoadAnimation = _ => { };
        public Action<string> OnLoadSkeleton = _ => { };
        public Action<string> OnOpenGameFolder = _ => { };
        public Func<string> GetPropertiesText = () => "";
        public Func<string> GetLogText = () => "";
        public Func<string> GetStatusText = () => "";

        public Func<GameData> GetGameData = () => null;
        public Func<string, Animation> GetAnimationForIndexing = _ => null;

        public Func<LevelScene> GetScene = () => null;
        public Action<LevelObject, DrawItem> OnSelect = (_, _) => { };
        public Action OnFocusSelection = () => { };
        public Action OnVisibilityChanged = () => { };


        public Action<System.Numerics.Vector3, System.Numerics.Vector3> OnPickRay = (_, _) => { };

        public Action<Action> RunOnMainThread = a => a();

        private readonly RenderModesFrame _renderModes;
        private readonly BrowserFrame _browser;
        private readonly PropertiesFrame _properties;
        private readonly LogFrame _log;
        private readonly MainViewFrame _mainView;
        private readonly ModelViewerFrame _modelViewer;

        private uint _dockspaceId;
        private bool _dockLayoutBuilt;

        private string _levelFilter = "";

        private static bool MatchesLevel(string name, string filter) =>
            string.IsNullOrEmpty(filter) || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        public MainFrame(Window window) {
            _window = window;
            _controller = new ImGuiController(window.ClientSize.X, window.ClientSize.Y);

            _renderModes = new RenderModesFrame(this);
            _browser = new BrowserFrame(this);
            _properties = new PropertiesFrame(this);
            _log = new LogFrame(this) { isOpen = false };
            _modelViewer = new ModelViewerFrame(this);
            _mainView = new MainViewFrame(this);


            AddFrame(_renderModes);
            AddFrame(_browser);
            AddFrame(_properties);
            AddFrame(_log);
            AddFrame(_modelViewer);
            AddFrame(_mainView);


            window.Resize += e => _controller.WindowResized(window.ClientSize.X, window.ClientSize.Y);
            window.MouseWheel += e => _controller.MouseScroll(new OpenTK.Mathematics.Vector2(e.OffsetX, e.OffsetY));
            window.TextInput += e => _controller.PressChar((char)e.Unicode);
        }

        public void AddFrame(Frame frame) => _openFrames.Add(frame);

        public void RenderUi() {
            _controller.Update(_window, 1f / 60f);

            RenderMenuBar();
            RenderDockSpace();

            foreach (var frame in _openFrames.Where(f => f.isOpen))
                frame.RenderAsWindow(1f / 60f);

            _controller.Render();
        }
        private unsafe void RenderDockSpace() {
            ImGuiWindowFlags hostFlags = ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus |
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.MenuBar;

            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport.WorkPos);
            ImGui.SetNextWindowSize(viewport.WorkSize);
            ImGui.SetNextWindowViewport(viewport.ID);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new System.Numerics.Vector2(0, 0));

            ImGui.Begin("##DockSpaceHost", hostFlags);
            ImGui.PopStyleVar(3);

            _dockspaceId = ImGui.GetID("MainDockSpace");
            ImGui.DockSpace(_dockspaceId, System.Numerics.Vector2.Zero, ImGuiDockNodeFlags.None, null);

            if (!_dockLayoutBuilt) {
                BuildDefaultLayout(_dockspaceId);
                _dockLayoutBuilt = true;
            }

            ImGui.End();
        }

        private unsafe void BuildDefaultLayout(uint dockspaceId) {
            ImGuiP.DockBuilderSetNodeSize(dockspaceId, ImGui.GetMainViewport().WorkSize);

            uint center = dockspaceId;
            uint left, right, bottom, rightBottom;

            ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Left, 0.22f, &left, &center);
            ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Right, 0.25f, &right, &center);
            ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Down, 0.22f, &bottom, &center);
            ImGuiP.DockBuilderSplitNode(right, ImGuiDir.Down, 0.5f, &rightBottom, &right);

            ImGuiP.DockBuilderDockWindow(_modelViewer.WindowTitle, center);
            ImGuiP.DockBuilderDockWindow(_mainView.WindowTitle, center);
            ImGuiP.DockBuilderDockWindow(_browser.WindowTitle, left);
            ImGuiP.DockBuilderDockWindow(_renderModes.WindowTitle, right);
            ImGuiP.DockBuilderDockWindow(_properties.WindowTitle, rightBottom);
            ImGuiP.DockBuilderDockWindow(_log.WindowTitle, bottom);

            ImGuiP.DockBuilderFinish(dockspaceId);
        }

        private void RenderMenuBar() {
            if (ImGui.BeginMainMenuBar()) {
                if (ImGui.BeginMenu("File")) {
                    if (ImGui.MenuItem("Open game folder…")) {
                        string picked = CrossFileDialog.OpenFolder("Choose a game folder");
                        if (picked.Length > 0) OnOpenGameFolder(picked);
                    }
                    ImGui.EndMenu();
                }

                if (ImGui.BeginMenu("Levels")) {
                    ImGui.SetNextItemWidth(220);
                    ImGui.InputTextWithHint("##levelFilter", "Filter levels…", ref _levelFilter, 128);
                    ImGui.Separator();
                    if (ImGui.BeginChild("##levelMenuList", new System.Numerics.Vector2(260, 360))) {
                        foreach (var lv in GetLevels().Where(l => MatchesLevel(l.Name, _levelFilter))) {
                            if (ImGui.MenuItem(lv.Name)) OnLoadLevel(lv);
                        }
                    }
                    ImGui.EndChild();
                    ImGui.EndMenu();
                }

                if (ImGui.BeginMenu("Windows")) {
                    MenuToggle("Main View", _mainView);
                    MenuToggle("Render modes", _renderModes);
                    MenuToggle("Browser (Scene)", _browser);
                    MenuToggle("Model Viewer", _modelViewer);
                    MenuToggle("Properties", _properties);
                    MenuToggle("Log", _log);
                    if (ImGui.MenuItem("Reset layout")) _dockLayoutBuilt = false;
                    ImGui.EndMenu();
                }

                ImGui.SameLine(ImGui.GetWindowWidth() - 420);
                ImGui.TextDisabled(GetStatusText());

                ImGui.EndMainMenuBar();
            }
        }

        private void MenuToggle(string label, Frame frame) {
            bool open = frame.isOpen;
            if (ImGui.MenuItem(label, "", open)) frame.isOpen = !open;
        }

        public void Dispose() {
            foreach (var frame in _openFrames) frame.Dispose();
            _openFrames.Clear();
            _controller.Dispose();
        }
    }
}