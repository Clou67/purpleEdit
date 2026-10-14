using System;
using System.Diagnostics;
using Hexa.NET.ImGui;
using Mk2.Viewer.Frames;
using Mk2.Viewer.Rendering;
using OpenTK.Graphics.OpenGL4;
using Vec2 = System.Numerics.Vector2;
using Vec3 = System.Numerics.Vector3;

namespace Mk2.Viewer.ImGuiUi.Frames {
    public sealed class MainViewFrame : Frame {
        protected override string frameName { get; set; }

        private readonly RenderTarget _rt = new RenderTarget();
        private bool _glInit;
        private bool _rmb, _mmb, _lmb;
        private Vec2 _lastMouse, _downMouse;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastTick;

        public MainViewFrame(MainFrame owner) : base(owner, "Main View") { }

        public override void Render(float deltaTime) {
            var avail = ImGui.GetContentRegionAvail();
            int w = Math.Max(1, (int)avail.X);
            int h = Math.Max(1, (int)avail.Y);
            if (w <= 1 || h <= 1) return;

            RenderSceneOffscreen(w, h);

            var cursor = ImGui.GetCursorScreenPos();
            unsafe {
                ImGui.Image(new ImTextureRef(default, (ulong)(uint)_rt.ColorTexture), new Vec2(w, h), new Vec2(0, 1), new Vec2(1, 0));
            }
            bool hovered = ImGui.IsItemHovered();
            HandleViewportInput(hovered, cursor, w, h);
        }

        private void RenderSceneOffscreen(int w, int h) {
            var renderer = owner.Renderer;
            var cam = owner.Camera;
            var vs = owner.ViewSettings;
            if (renderer == null || cam == null || vs == null) return;

            if (!_glInit) { renderer.InitGl(); _glInit = true; }

            int[] prevViewport = new int[4];
            GL.GetInteger(GetPName.Viewport, prevViewport);

            _rt.RenderToTexture(w, h, () => {
                renderer.Render(cam, w, h, vs, owner.ClearColor, owner.FogColor, owner.FogRange);
            });

            GL.Viewport(prevViewport[0], prevViewport[1], prevViewport[2], prevViewport[3]);
        }

        private void HandleViewportInput(bool hovered, Vec2 origin, int w, int h) {
            var cam = owner.Camera;
            if (cam == null) return;

            double now = _clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.05, now - _lastTick);
            _lastTick = now;

            var io = ImGui.GetIO();
            var mouse = io.MousePos;

            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) _rmb = true;
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle)) _mmb = true;
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) { _lmb = true; _downMouse = mouse; }
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Right)) _rmb = false;
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Middle)) _mmb = false;
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) {
                bool wasLmb = _lmb;
                _lmb = false;
                if (wasLmb && hovered) {
                    float dist = Math.Abs(mouse.X - _downMouse.X) + Math.Abs(mouse.Y - _downMouse.Y);
                    if (dist < 4) PickAt(mouse, origin, w, h);
                }
            }

            if (hovered || _rmb || _mmb) {
                float dx = mouse.X - _lastMouse.X, dy = mouse.Y - _lastMouse.Y;
                if (_rmb) cam.Rotate(dx * 0.005f, -dy * 0.005f);
                else if (_mmb) {
                    float k = cam.Speed * 0.002f;
                    cam.Position += (-cam.Right * dx + cam.Up * dy) * k;
                }
            }
            _lastMouse = mouse;

            if (hovered) {
                float wheel = io.MouseWheel;
                if (wheel != 0) cam.Speed = Math.Clamp(cam.Speed * (wheel > 0 ? 1.25f : 0.8f), 50f, 200000f);
            }

            if (hovered || _rmb || _mmb) UpdateFlyMovement(cam, dt);
            if (hovered && ImGui.IsKeyPressed(ImGuiKey.F)) owner.OnFocusSelection();
            if (hovered && ImGui.IsKeyPressed(ImGuiKey.Escape)) owner.OnSelect(null, null);
        }

        private static void UpdateFlyMovement(Rendering.Camera cam, float dt) {
            var io = ImGui.GetIO();
            var move = Vec3.Zero;
            if (ImGui.IsKeyDown(ImGuiKey.W)) move += cam.Forward;
            if (ImGui.IsKeyDown(ImGuiKey.S)) move -= cam.Forward;
            if (ImGui.IsKeyDown(ImGuiKey.D)) move += cam.Right;
            if (ImGui.IsKeyDown(ImGuiKey.A)) move -= cam.Right;
            if (ImGui.IsKeyDown(ImGuiKey.E) || ImGui.IsKeyDown(ImGuiKey.Space)) move += Vec3.UnitY;
            if (ImGui.IsKeyDown(ImGuiKey.Q)) move -= Vec3.UnitY;
            if (move == Vec3.Zero) return;

            float speed = cam.Speed;
            if (io.KeyShift) speed *= 4;
            if (io.KeyCtrl) speed *= 0.25f;
            cam.Position += Vec3.Normalize(move) * speed * dt;
        }

        private void PickAt(Vec2 mouse, Vec2 origin, int w, int h) {
            var cam = owner.Camera;
            if (cam == null) return;

            float localX = mouse.X - origin.X, localY = mouse.Y - origin.Y;
            cam.Ray(localX, localY, w, h, out var o, out var d);
            owner.OnPickRay(o, d);
        }

        public override void Dispose() {
            base.Dispose();
            _rt.Dispose();
        }
    }
}
