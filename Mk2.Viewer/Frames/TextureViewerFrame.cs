using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hexa.NET.ImGui;
using Mk2.Export;
using Mk2.Formats;
using Mk2.Game;
using Mk2.Viewer.Frames;
using Mk2.Viewer.Rendering;
using Mk2.Viewer.Utils;
using OpenTK.Graphics.OpenGL4;
using Vec2 = System.Numerics.Vector2;

namespace Mk2.Viewer.ImGuiUi.Frames {
    public sealed class TextureViewerFrame : Frame {
        protected override string frameName { get; set; }

        private const int CheckerSize = 16;

        private GameData game;
        private AssetCache assets;
        private List<string> textureNames = new List<string>();
        private string filter = "";
        private string currentName;
        private Texture currentTexture;
        private int glTexture;
        private int opaqueGlTexture;
        private bool showAlpha = true;
        private bool fit = true;
        private float zoom = 1f;
        private bool scrollToCurrent;

        public TextureViewerFrame(MainFrame owner) : base(owner, "Texture Viewer") { }

        /// <summary>Opens a texture by name, from anywhere in the UI.</summary>
        public void Show(string name) {
            isOpen = true;
            scrollToCurrent = true;
            ImGui.SetWindowFocus(WindowTitle);
            _ = Load(name);
        }

        public override void Render(float deltaTime) {
            RefreshGame();

            ImGui.BeginChild("##textureList", new Vec2(260, 0), ImGuiChildFlags.Borders | ImGuiChildFlags.ResizeX);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##textureFilter", "Filter", ref filter, 128);
            ImGui.Separator();
            if (ImGui.BeginChild("##textureNames")) {
                foreach (var name in textureNames) {
                    if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    bool selected = string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(name, selected) && !selected) _ = Load(name);
                    if (selected && scrollToCurrent) { ImGui.SetScrollHereY(); scrollToCurrent = false; }
                }
            }
            ImGui.EndChild();
            ImGui.EndChild();

            ImGui.SameLine();
            ImGui.BeginChild("##texturePreview", Vec2.Zero);
            RenderPreview();
            ImGui.EndChild();
        }

        private void RefreshGame() {
            var viewerGame = owner.Viewer.Game;
            if (ReferenceEquals(viewerGame, game)) return;
            game = viewerGame;
            assets = game == null ? null : new AssetCache(game);
            textureNames = game == null ? new List<string>()
                : game.AllNames(".tex").Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase)
                      .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
            ReleaseTexture();
            currentName = null;
            currentTexture = null;
        }

        private async Task Load(string name) {
            RefreshGame();
            if (assets == null) return;
            currentName = name;
            var cache = assets;
            var texture = await Task.Run(() => cache.GetTexture(name));
            owner.Viewer.RunOnMainThread(() => {
                if (!string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase)) return;
                ReleaseTexture();
                currentTexture = texture;
                if (texture == null) return;
                glTexture = TextureCache.Upload(texture).Id;
                var opaquePixels = (byte[])texture.Rgba.Clone();
                for (int offset = 3; offset < opaquePixels.Length; offset += 4) opaquePixels[offset] = 255;
                opaqueGlTexture = TextureCache.Upload(new Texture { Width = texture.Width, Height = texture.Height, Rgba = opaquePixels }).Id;
            });
        }

        private void RenderPreview() {
            if (currentName == null) return;
            ImGui.TextUnformatted(currentName);
            if (currentTexture == null) return;

            ImGui.SameLine();
            ImGui.TextDisabled($"{currentTexture.Width} x {currentTexture.Height}   format 0x{currentTexture.Format:X2}   {AlphaKind(currentTexture)}");
            ImGui.Checkbox("Alpha", ref showAlpha);
            ImGui.SameLine();
            ImGui.Checkbox("Fit", ref fit);
            if (!fit) {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(160);
                ImGui.SliderFloat("Zoom", ref zoom, 0.25f, 8f, "%.2fx", ImGuiSliderFlags.Logarithmic);
            }
            ImGui.SameLine();
            if (ImGui.Button("Save PNG…")) {
                string path = CrossFileDialog.SaveFile("Save texture", "*.png");
                if (path.Length > 0) {
                    if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";
                    Png.Save(path, currentTexture.Rgba, currentTexture.Width, currentTexture.Height);
                    owner.Viewer.SetStatus("Saved " + path);
                }
            }
            ImGui.Separator();

            if (ImGui.BeginChild("##textureCanvas", Vec2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar)) {
                var available = ImGui.GetContentRegionAvail();
                float scale = fit ? Math.Min(available.X / currentTexture.Width, available.Y / currentTexture.Height) : zoom;
                var size = new Vec2(currentTexture.Width, currentTexture.Height) * Math.Max(scale, 0.01f);
                if (showAlpha) DrawChecker(ImGui.GetCursorScreenPos(), size);
                Image(showAlpha ? glTexture : opaqueGlTexture, size);
            }
            ImGui.EndChild();
        }

        private static unsafe void Image(int texture, Vec2 size) {
            ImGui.Image(new ImTextureRef(default, (ulong)(uint)texture), size, Vec2.Zero, Vec2.One);
        }

        private static void DrawChecker(Vec2 origin, Vec2 size) {
            var drawList = ImGui.GetWindowDrawList();
            for (float top = 0; top < size.Y; top += CheckerSize)
                for (float left = 0; left < size.X; left += CheckerSize) {
                    bool dark = ((int)(left / CheckerSize) + (int)(top / CheckerSize)) % 2 == 0;
                    var cellMax = Vec2.Min(origin + new Vec2(left + CheckerSize, top + CheckerSize), origin + size);
                    drawList.AddRectFilled(origin + new Vec2(left, top), cellMax, dark ? 0xFF3A3A3A : 0xFF5A5A5A);
                }
        }

        private static string AlphaKind(Texture texture) =>
            !texture.HasAlpha ? "opaque" : texture.HasCutout ? "cut-out alpha" : "blended alpha";

        private void ReleaseTexture() {
            if (glTexture != 0) GL.DeleteTexture(glTexture);
            if (opaqueGlTexture != 0) GL.DeleteTexture(opaqueGlTexture);
            glTexture = 0;
            opaqueGlTexture = 0;
        }

        public override void Dispose() {
            base.Dispose();
            ReleaseTexture();
        }
    }
}
