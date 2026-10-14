using System;
using System.Linq;
using Hexa.NET.ImGui;
using Mk2.Game;
using Mk2.Viewer.Frames;
using Mk2.Viewer.Rendering;

namespace Mk2.Viewer.ImGuiUi.Frames {
    public sealed class BrowserFrame : Frame {
        protected override string frameName { get; set; }

        private string filter = "";

        public BrowserFrame(MainFrame owner) : base(owner, "Browser") { }

        public override void Render(float deltaTime) {
            var viewer = owner.Viewer;
            var scene = viewer.Scene;
            if (scene == null) return;

            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##sceneFilter", "Filter", ref filter, 128);
            ImGui.Separator();

            var settings = viewer.ViewSettings;
            string activeFilter = filter.Trim();

            if (ImGui.BeginChild("SceneTree")) {
                foreach (var chunk in scene.Chunks) RenderChunk(chunk, settings, activeFilter);
            }
            ImGui.EndChild();
        }

        private void RenderChunk(SceneChunk chunk, ViewSettings settings, string activeFilter) {
            var viewer = owner.Viewer;
            string chunkName = chunk.Def.Name;
            ImGui.PushID(chunkName);

            bool chunkVisible = !settings.HiddenChunks.Contains(chunkName);
            if (ImGui.Checkbox("##chunkVisible", ref chunkVisible)) {
                if (chunkVisible) settings.HiddenChunks.Remove(chunkName); else settings.HiddenChunks.Add(chunkName);
            }
            ImGui.SameLine();
            if (ImGui.TreeNodeEx(chunkName + (chunk.Def.IsEnvironment ? "  [env]" : ""), ImGuiTreeNodeFlags.SpanAvailWidth)) {
                if (activeFilter.Length == 0) {
                    if (chunk.Overlays.Count > 0) RenderGroup(settings, chunkName, "overlays", $"Overlays ({chunk.Overlays.Count})", () => {
                        foreach (var (overlay, model) in chunk.Overlays) {
                            string label = model == null ? overlay.Name + " (missing)" : $"{overlay}  — {model.Model.TriangleCount:N0} tris";
                            if (ItemRow(settings, chunkName, overlay.Name, label)) {
                                var item = viewer.Renderer.Items.FirstOrDefault(candidate =>
                                    candidate.Kind == ItemKind.Overlay && candidate.Name == overlay.Name && candidate.Chunk == chunk);
                                if (item != null) viewer.SelectAndFocus(null, item);
                            }
                        }
                    });

                    if (chunk.Grounds.Count > 0) RenderGroup(settings, chunkName, "grounds", $"Ground collision ({chunk.Grounds.Count})", () => {
                        foreach (var (name, collision) in chunk.Grounds) {
                            if (ItemRow(settings, chunkName, name, $"{name}  — {collision.Triangles.Count} tris"))
                                viewer.ShowCollision(collision);
                        }
                    });

                    if (chunk.EnvDome != null) {
                        bool domeVisible = !settings.GroupHidden(chunkName, "dome");
                        if (ImGui.Checkbox("##dome", ref domeVisible)) SetGroupVisible(settings, chunkName, "dome", domeVisible);
                        ImGui.SameLine();
                        ImGui.TextUnformatted("Sky dome: " + chunk.EnvDome.Name);
                    }
                }

                var objects = chunk.Objects.Where(levelObject => Matches(levelObject, activeFilter)).ToList();
                if (objects.Count > 0) RenderGroup(settings, chunkName, "objects", $"Objects ({objects.Count})", () => {
                    foreach (var categoryGroup in objects.GroupBy(levelObject => levelObject.Category).OrderBy(group => group.Key))
                        RenderCategory(chunk, categoryGroup.Key, categoryGroup.ToList(), settings, activeFilter.Length > 0);
                });

                ImGui.TreePop();
            }
            ImGui.PopID();
        }

        private void RenderCategory(SceneChunk chunk, ObjectCategory category, System.Collections.Generic.List<LevelObject> objects, ViewSettings settings, bool expand) {
            var viewer = owner.Viewer;
            string chunkName = chunk.Def.Name;
            ImGui.PushID(category.ToString());
            bool categoryVisible = !settings.HiddenChunkCategories.Contains((chunkName, category));
            if (ImGui.Checkbox("##categoryVisible", ref categoryVisible)) {
                if (categoryVisible) settings.HiddenChunkCategories.Remove((chunkName, category));
                else settings.HiddenChunkCategories.Add((chunkName, category));
            }
            ImGui.SameLine();
            var flags = ImGuiTreeNodeFlags.SpanAvailWidth | (expand ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
            if (ImGui.TreeNodeEx($"{category} ({objects.Count})", flags)) {
                foreach (var levelObject in objects) {
                    string model = chunk.ObjectModels.TryGetValue(levelObject, out var loadedModel) ? "  → " + loadedModel.Name : "";
                    ImGui.PushID(levelObject.Index);
                    bool objectVisible = !settings.HiddenObjects.Contains(levelObject);
                    if (ImGui.Checkbox("##objectVisible", ref objectVisible)) {
                        if (objectVisible) settings.HiddenObjects.Remove(levelObject); else settings.HiddenObjects.Add(levelObject);
                        viewer.VisibilityChanged();
                    }
                    ImGui.SameLine();
                    bool selected = viewer.Renderer.Selected == levelObject;
                    if (ImGui.Selectable(levelObject.DisplayName + model, selected)) viewer.SelectAndFocus(levelObject, null);
                    ImGui.PopID();
                }
                ImGui.TreePop();
            }
            ImGui.PopID();
        }

        /// <summary>Visibility checkbox plus tree node for one of a chunk's groups.</summary>
        private static void RenderGroup(ViewSettings settings, string chunk, string key, string label, Action renderChildren) {
            ImGui.PushID(key);
            bool visible = !settings.GroupHidden(chunk, key);
            if (ImGui.Checkbox("##groupVisible", ref visible)) SetGroupVisible(settings, chunk, key, visible);
            ImGui.SameLine();
            if (ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.SpanAvailWidth)) {
                renderChildren();
                ImGui.TreePop();
            }
            ImGui.PopID();
        }

        private static void SetGroupVisible(ViewSettings settings, string chunk, string key, bool visible) {
            if (visible) settings.HiddenGroups.Remove((chunk, key)); else settings.HiddenGroups.Add((chunk, key));
        }

        /// <summary>Visibility checkbox plus a clickable row. Returns true when the row was clicked.</summary>
        private static bool ItemRow(ViewSettings settings, string chunk, string key, string label) {
            ImGui.PushID(key);
            bool visible = !settings.HiddenItems.Contains((chunk, key));
            if (ImGui.Checkbox("##itemVisible", ref visible)) {
                if (visible) settings.HiddenItems.Remove((chunk, key)); else settings.HiddenItems.Add((chunk, key));
            }
            ImGui.SameLine();
            bool clicked = ImGui.Selectable(label);
            ImGui.PopID();
            return clicked;
        }

        private static bool Matches(LevelObject levelObject, string text) {
            if (text.Length == 0) return true;
            return levelObject.ClassName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || (levelObject.IdName != null && levelObject.IdName.Contains(text, StringComparison.OrdinalIgnoreCase))
                || levelObject.Id.ToString() == text;
        }
    }
}
