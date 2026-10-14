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

        private readonly RenderTarget renderTarget = new RenderTarget();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private bool rightDragging, middleDragging, leftPressed;
        private Vec2 lastMouse, pressedMouse;
        private double lastTick;

        public MainViewFrame(MainFrame owner) : base(owner, "Main View") { }

        public override void Render(float deltaTime) {
            var available = ImGui.GetContentRegionAvail();
            int width = (int)available.X;
            int height = (int)available.Y;
            if (width <= 1 || height <= 1) return;

            RenderSceneOffscreen(width, height);

            var origin = ImGui.GetCursorScreenPos();
            unsafe {
                ImGui.Image(new ImTextureRef(default, (ulong)(uint)renderTarget.ColorTexture), new Vec2(width, height), new Vec2(0, 1), new Vec2(1, 0));
            }
            bool hovered = ImGui.IsItemHovered();
            DrawCameraReadout(origin);
            HandleInput(hovered, origin, width, height);
        }

        private void DrawCameraReadout(Vec2 origin) {
            var viewer = owner.Viewer;
            var position = viewer.CameraWorldPosition;
            var angles = viewer.CameraWorldAngles;
            string text = $"{position.X:0} {position.Y:0} {position.Z:0}   yaw {angles.X:0.0}   pitch {angles.Y:0.0}";
            var drawList = ImGui.GetWindowDrawList();
            var textPosition = origin + new Vec2(8, 6);
            drawList.AddRectFilled(textPosition - new Vec2(4, 2), textPosition + ImGui.CalcTextSize(text) + new Vec2(4, 2), 0x99000000);
            drawList.AddText(textPosition, 0xFFFFFFFF, text);
        }

        private void RenderSceneOffscreen(int width, int height) {
            var viewer = owner.Viewer;
            int[] previousViewport = new int[4];
            GL.GetInteger(GetPName.Viewport, previousViewport);
            renderTarget.RenderToTexture(width, height, () => {
                viewer.Renderer.Render(viewer.Camera, width, height, viewer.ViewSettings, viewer.ClearColor, viewer.FogColor, viewer.FogRange);
                viewer.SavePendingScreenshot(width, height);
            });
            GL.Viewport(previousViewport[0], previousViewport[1], previousViewport[2], previousViewport[3]);
        }

        private void HandleInput(bool hovered, Vec2 origin, int width, int height) {
            var viewer = owner.Viewer;
            var camera = viewer.Camera;
            double now = clock.Elapsed.TotalSeconds;
            float elapsed = (float)Math.Min(0.05, now - lastTick);
            lastTick = now;

            var io = ImGui.GetIO();
            var mouse = io.MousePos;

            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) rightDragging = true;
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle)) middleDragging = true;
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) { leftPressed = true; pressedMouse = mouse; }
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Right)) rightDragging = false;
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Middle)) middleDragging = false;
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) {
                bool wasPressed = leftPressed;
                leftPressed = false;
                float travel = Math.Abs(mouse.X - pressedMouse.X) + Math.Abs(mouse.Y - pressedMouse.Y);
                if (wasPressed && hovered && travel < 4) {
                    camera.Ray(mouse.X - origin.X, mouse.Y - origin.Y, width, height, out var rayOrigin, out var rayDirection);
                    viewer.PickRay(rayOrigin, rayDirection);
                }
            }

            float deltaX = mouse.X - lastMouse.X, deltaY = mouse.Y - lastMouse.Y;
            if (rightDragging) camera.Rotate(deltaX * 0.005f, -deltaY * 0.005f);
            else if (middleDragging) camera.Position += (-camera.Right * deltaX + camera.Up * deltaY) * (camera.Speed * 0.002f);
            lastMouse = mouse;

            if (hovered && io.MouseWheel != 0)
                camera.Speed = Math.Clamp(camera.Speed * (io.MouseWheel > 0 ? 1.25f : 0.8f), 50f, 200000f);

            if (hovered || rightDragging || middleDragging) Fly(camera, elapsed);
            if (hovered && ImGui.IsKeyPressed(ImGuiKey.F)) viewer.FocusSelection();
            if (hovered && ImGui.IsKeyPressed(ImGuiKey.Escape)) viewer.Select(null, null);
        }

        public static void Fly(Camera camera, float elapsed) {
            var io = ImGui.GetIO();
            var move = Vec3.Zero;
            if (ImGui.IsKeyDown(ImGuiKey.W)) move += camera.Forward;
            if (ImGui.IsKeyDown(ImGuiKey.S)) move -= camera.Forward;
            if (ImGui.IsKeyDown(ImGuiKey.D)) move += camera.Right;
            if (ImGui.IsKeyDown(ImGuiKey.A)) move -= camera.Right;
            if (ImGui.IsKeyDown(ImGuiKey.E)) move += Vec3.UnitY;
            if (ImGui.IsKeyDown(ImGuiKey.Q)) move -= Vec3.UnitY;
            if (move == Vec3.Zero) return;
            float speed = camera.Speed;
            if (io.KeyShift) speed *= 4;
            if (io.KeyCtrl) speed *= 0.25f;
            camera.Position += Vec3.Normalize(move) * speed * elapsed;
        }

        public override void Dispose() {
            base.Dispose();
            renderTarget.Dispose();
        }
    }
}
