using System.Linq;
using Hexa.NET.ImGui;
using Mk2.Game;
using Mk2.Viewer.Frames;
using Mk2.Viewer.Rendering;
using Mk2.Viewer.Utils;

namespace Mk2.Viewer.ImGuiUi.Frames {
    public sealed class BrowserFrame : Frame {
        protected override string frameName { get; set; }

        public BrowserFrame(MainFrame owner) : base(owner, "Browser") { }

        public override void Render(float deltaTime) {
            RenderSceneTree();
        }

        private string _sceneFilter = "";

        private void RenderSceneTree() {
            var scene = owner.GetScene();
            if (scene == null) {
                ImGui.TextDisabled("No scene loaded.");
                return;
            }
            ImGui.TextDisabled("Tick/untick to show or hide. Click to select, double-click to fly to it.");
            ImGui.InputTextWithHint("##sceneFilter", "Filter objects (class, ID name, ID number)…", ref _sceneFilter, 128);
            ImGui.Separator();

            var vs = owner.ViewSettings;
            string filter = _sceneFilter.Trim();

            if (!ImGui.BeginChild("SceneTree")) { ImGui.EndChild(); return; }
            foreach (var ch in scene.Chunks) {
                string cname = ch.Def.Name;
                ImGui.PushID(cname);

                bool chunkVisible = !vs.HiddenChunks.Contains(cname);
                if (ImGui.Checkbox("##chunkVisible", ref chunkVisible)) {
                    if (chunkVisible) vs.HiddenChunks.Remove(cname); else vs.HiddenChunks.Add(cname);
                    owner.OnVisibilityChanged();
                }
                ImGui.SameLine();
                bool chunkOpen = ImGui.TreeNodeEx(cname + (ch.Def.IsEnvironment ? "  [env]" : ""), ImGuiTreeNodeFlags.SpanAvailWidth);

                if (chunkOpen) {
                    if (filter.Length == 0) {
                        if (ch.Overlays.Count > 0) RenderGroup(vs, cname, "overlays", $"Overlays ({ch.Overlays.Count})", () => {
                            foreach (var (ov, m) in ch.Overlays) {
                                string label = m == null ? ov.Name + " (missing)" : $"{ov}  — {m.Model.TriangleCount:N0} tris";
                                RenderItemCheckbox(vs, cname, ov.Name, label, () => {
                                    var it = owner.Renderer.Items.FirstOrDefault(i => i.Kind == ItemKind.Overlay && i.Name == ov.Name && i.Chunk.Def.Name == cname);
                                    SelectAndMaybeFocus(null, it);
                                });
                            }
                        });

                        if (ch.Grounds.Count > 0) RenderGroup(vs, cname, "grounds", $"Ground collision ({ch.Grounds.Count})", () => {
                            foreach (var (n, c) in ch.Grounds) {
                                string label = $"{n}  — {c.Triangles.Count} tris [{string.Join(", ", c.Materials)}]";
                                RenderItemCheckbox(vs, cname, n, label, () => { /* ground collision has no DrawItem to select/focus on */ });
                            }
                        });

                        if (ch.EnvDome != null) {
                            bool domeVisible = !vs.HiddenGroups.Contains((cname, "dome"));
                            if (ImGui.Checkbox("##dome", ref domeVisible)) { ToggleGroup(vs, cname, "dome", domeVisible); owner.OnVisibilityChanged(); }
                            ImGui.SameLine();
                            ImGui.TextUnformatted("Sky dome: " + ch.EnvDome.Name);
                        }
                    }

                    var objs = ch.Objects.Where(o => Matches(o, filter)).ToList();
                    if (objs.Count > 0) {
                        RenderGroup(vs, cname, "objects", $"Objects ({objs.Count})", () => {
                            foreach (var grp in objs.GroupBy(o => o.Category).OrderBy(g => g.Key)) {
                                var cat = grp.Key;
                                ImGui.PushID(cat.ToString());
                                bool catVisible = !vs.HiddenChunkCategories.Contains((cname, cat));
                                if (ImGui.Checkbox("##catVisible", ref catVisible)) {
                                    if (catVisible) vs.HiddenChunkCategories.Remove((cname, cat)); else vs.HiddenChunkCategories.Add((cname, cat));
                                    owner.OnVisibilityChanged();
                                }
                                ImGui.SameLine();
                                bool catOpen = ImGui.TreeNodeEx($"{cat} ({grp.Count()})", filter.Length > 0 ? ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.SpanAvailWidth);
                                if (catOpen) {
                                    foreach (var o in grp) {
                                        string model = ch.ObjectModels.TryGetValue(o, out var lm) ? "  → " + lm.Name : "";
                                        bool objVisible = !vs.HiddenObjects.Contains(o);
                                        ImGui.PushID(o.GetHashCode());
                                        if (ImGui.Checkbox("##objVisible", ref objVisible)) {
                                            if (objVisible) vs.HiddenObjects.Remove(o); else vs.HiddenObjects.Add(o);
                                            owner.OnVisibilityChanged();
                                        }
                                        ImGui.SameLine();
                                        if (ImGui.Selectable($"{o.DisplayName}{model}", false, ImGuiSelectableFlags.AllowDoubleClick))
                                            SelectAndMaybeFocus(o, null, ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left));
                                        ImGui.PopID();
                                    }
                                    ImGui.TreePop();
                                }
                                ImGui.PopID();
                            }
                        });
                    }

                    ImGui.TreePop();
                }
                ImGui.PopID();
            }
            ImGui.EndChild();
        }

        /// <summary>Checkbox + collapsible header for one of a chunk's groups (overlays/grounds/objects), same shape repeated three times.</summary>
        private static void RenderGroup(ViewSettings vs, string chunk, string key, string label, System.Action renderChildren) {
            ImGui.PushID(key);
            bool visible = !vs.GroupHidden(chunk, key);
            if (ImGui.Checkbox("##groupVisible", ref visible)) ToggleGroup(vs, chunk, key, visible);
            ImGui.SameLine();
            bool open = ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.SpanAvailWidth);
            if (open) {
                renderChildren();
                ImGui.TreePop();
            }
            ImGui.PopID();
        }

        private static void ToggleGroup(ViewSettings vs, string chunk, string key, bool visible) {
            if (visible) vs.HiddenGroups.Remove((chunk, key)); else vs.HiddenGroups.Add((chunk, key));
        }

        /// <summary>Checkbox + clickable row for one overlay / ground collision mesh.</summary>
        private void RenderItemCheckbox(ViewSettings vs, string chunk, string key, string label, System.Action onClick) {
            ImGui.PushID(key);
            bool visible = !vs.HiddenItems.Contains((chunk, key));
            if (ImGui.Checkbox("##itemVisible", ref visible)) {
                if (visible) vs.HiddenItems.Remove((chunk, key)); else vs.HiddenItems.Add((chunk, key));
                owner.OnVisibilityChanged();
            }
            ImGui.SameLine();
            if (ImGui.Selectable(label)) onClick();
            ImGui.PopID();
        }

        private void SelectAndMaybeFocus(LevelObject o, DrawItem item, bool focus = false) {
            owner.OnSelect(o, item);
            if (focus) owner.OnFocusSelection();
        }

        private static bool Matches(LevelObject o, string f) {
            if (f.Length == 0) return true;
            return o.ClassName.IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0
                || (o.IdName != null && o.IdName.IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0)
                || o.Id.ToString() == f;
        }
    }
}