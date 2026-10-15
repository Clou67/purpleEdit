using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Hexa.NET.ImGui;
using Mk2.Formats;
using Mk2.Game;
using Mk2.Viewer.Frames;
using Mk2.Viewer.Rendering;
using OpenTK.Graphics.OpenGL4;
using Vec2 = System.Numerics.Vector2;
using Vec3 = System.Numerics.Vector3;

namespace Mk2.Viewer.ImGuiUi.Frames {
    public sealed class ModelViewerFrame : Frame {
        protected override string frameName { get; set; }

        private const float FramesPerSecond = 30f;

        private readonly SceneRenderer renderer = new SceneRenderer();
        private readonly Camera camera = new Camera();
        private readonly ViewSettings settings = new ViewSettings { Fog = false, EnvDome = false };
        private readonly RenderTarget renderTarget = new RenderTarget();
        private readonly Vec3 clearColor = new Vec3(0.14f, 0.15f, 0.18f);
        private readonly Stopwatch clock = Stopwatch.StartNew();

        private GameData game;
        private AssetCache assets;
        private string modelFilter = "";
        private string currentModel;
        private string status = "";
        private bool loading;

        private Dictionary<string, List<string>> animationsByModel;
        private bool animationIndexBuilding;
        private List<string> currentAnimations = new List<string>();
        private string currentAnimationName;

        private Animation animation;
        private AnimSet clips;
        private AnimClip clip;
        private float animationFrame = -1;
        private bool playing;
        private bool loop = true;
        private double lastAnimationTick;

        private bool rightDragging, middleDragging;
        private Vec2 lastMouse;
        private double lastTick;

        public ModelViewerFrame(MainFrame owner) : base(owner, "Model Viewer") { }

        public override void Render(float deltaTime) {
            RefreshGame();

            ImGui.BeginChild("##modelViewerLeft", new Vec2(260, 0), ImGuiChildFlags.Borders | ImGuiChildFlags.ResizeX);
            RenderModelList();
            ImGui.EndChild();

            ImGui.SameLine();

            ImGui.BeginChild("##modelViewerRight", Vec2.Zero, ImGuiChildFlags.None);
            RenderControls();
            RenderTextureStrip();
            ImGui.Separator();
            RenderViewport();
            ImGui.EndChild();
        }

        private void RefreshGame() {
            var viewerGame = owner.Viewer.Game;
            if (ReferenceEquals(viewerGame, game)) return;
            game = viewerGame;
            assets = null;
            animationsByModel = null;
            animationIndexBuilding = false;
            currentModel = null;
            currentAnimations = new List<string>();
            ClearAnimation();
            status = "";
        }

        private void RenderModelList() {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##modelFilter", "Filter", ref modelFilter, 128);
            ImGui.Separator();
            if (game == null) return;

            bool showBones = animation != null && settings.Skeleton;
            float listHeight = showBones ? ImGui.GetContentRegionAvail().Y * 0.5f : 0;
            if (ImGui.BeginChild("##modelList", new Vec2(0, listHeight))) {
                foreach (var model in owner.Viewer.Models) {
                    if (modelFilter.Length > 0 && !model.Contains(modelFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    bool selected = string.Equals(model, currentModel, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(model, selected) && !selected) _ = LoadModel(model);
                }
            }
            ImGui.EndChild();

            if (showBones) {
                ImGui.SeparatorText("Bones");
                if (ImGui.BeginChild("##boneTree")) RenderBoneTree();
                ImGui.EndChild();
            }
        }

        private void RenderBoneTree() {
            var shownAnimation = animation;
            if (shownAnimation == null) return;
            foreach (var bone in shownAnimation.Bones.Where(candidate => candidate.Parent < 0)) RenderBone(shownAnimation, bone);
        }

        private void RenderBone(Animation shownAnimation, AnimBone bone) {
            var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.DefaultOpen;
            if (bone.Children.Count == 0) flags |= ImGuiTreeNodeFlags.Leaf;
            if (renderer.Anim?.SelectedBone == bone.Index) flags |= ImGuiTreeNodeFlags.Selected;
            if (!bone.IsAnimated) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            bool open = ImGui.TreeNodeEx($"{bone.Name}  [{bone.Index}]###bone{bone.Index}", flags);
            if (!bone.IsAnimated) ImGui.PopStyleColor();
            if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen()) SelectBone(bone.Index);
            if (!open) return;
            foreach (int child in bone.Children) RenderBone(shownAnimation, shownAnimation.Bones[child]);
            ImGui.TreePop();
        }

        private void SelectBone(int boneIndex) {
            renderer.SetSelectedBone(boneIndex);
            owner.Viewer.ShowProperties(PropertiesView.ForBone(animation, boneIndex, renderer.Anim));
        }

        private const float ThumbnailSize = 48f;

        /// <summary>The current model's textures as thumbnails; clicking one opens it in the Texture Viewer.</summary>
        private void RenderTextureStrip() {
            if (currentModel == null) return;
            var textures = renderer.Items.SelectMany(item => item.Model.Parts)
                                   .Where(part => part.Texture != 0 && !part.IsCollisionComponent)
                                   .GroupBy(part => part.Material.TextureName, StringComparer.OrdinalIgnoreCase)
                                   .Select(group => (name: group.Key, texture: group.First().Texture)).ToList();
            if (textures.Count == 0 || !ImGui.CollapsingHeader($"Textures ({textures.Count})")) return;
            float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
            for (int index = 0; index < textures.Count; index++) {
                var (name, texture) = textures[index];
                ImGui.PushID(index);
                bool clicked;
                unsafe {
                    clicked = ImGui.ImageButton("##thumbnail", new ImTextureRef(default, (ulong)(uint)texture), new Vec2(ThumbnailSize), new Vec2(0, 0), new Vec2(1, 1));
                }
                if (clicked) owner.ShowTexture(name);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(name);
                ImGui.PopID();
                float nextRight = ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + ThumbnailSize;
                if (index + 1 < textures.Count && nextRight < right) ImGui.SameLine();
            }
        }

        private void RenderControls() {
            if (currentModel == null) return;

            ImGui.TextUnformatted(currentModel);
            ImGui.SameLine();
            ImGui.TextDisabled(loading ? "loading…" : status);

            if (currentAnimations.Count == 0) return;

            ImGui.SetNextItemWidth(320);
            if (ImGui.BeginCombo("Animation", currentAnimationName ?? "Bind pose")) {
                if (ImGui.Selectable("Bind pose", currentAnimationName == null)) _ = ApplyAnimation(null);
                foreach (var name in currentAnimations) {
                    bool selected = name == currentAnimationName;
                    if (ImGui.Selectable(name, selected)) _ = ApplyAnimation(name);
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            ImGui.Checkbox("Skeleton", ref settings.Skeleton);

            if (clips == null || clips.Clips.Count == 0) return;

            ImGui.SetNextItemWidth(220);
            if (ImGui.BeginCombo("Clip", clip?.Name ?? "")) {
                foreach (var candidate in clips.Clips) {
                    bool selected = candidate == clip;
                    if (ImGui.Selectable($"{candidate.Name}##{candidate.Start}", selected)) SelectClip(candidate);
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            if (clip == null) return;

            ImGui.SameLine();
            if (ImGui.Button(playing ? "Pause" : "Play")) TogglePlay();
            ImGui.SameLine();
            ImGui.Checkbox("Loop", ref loop);

            float frame = animationFrame;
            ImGui.SetNextItemWidth(320);
            if (ImGui.SliderFloat("Frame", ref frame, clip.Start, Math.Max(clip.Start + 1, clip.End), "%.0f")) {
                playing = false;
                animationFrame = frame;
            }
            int wholeFrame = (int)Math.Floor(animationFrame);
            var events = clip.Events.Where(animationEvent => animationEvent.Frame == wholeFrame).Select(animationEvent => animationEvent.Name).ToList();
            if (events.Count > 0) {
                ImGui.SameLine();
                ImGui.TextDisabled(string.Join(", ", events));
            }
        }

        private void SelectClip(AnimClip selectedClip) {
            if (animation == null || selectedClip == null) return;
            clip = selectedClip;
            animationFrame = selectedClip.Start;
            playing = selectedClip.End > selectedClip.Start;
            lastAnimationTick = clock.Elapsed.TotalSeconds;
        }

        private void TogglePlay() {
            if (clip == null) return;
            if (!playing && animationFrame >= clip.End && !loop) animationFrame = clip.Start;
            playing = !playing && clip.End > clip.Start;
            lastAnimationTick = clock.Elapsed.TotalSeconds;
        }

        private void RenderViewport() {
            var available = ImGui.GetContentRegionAvail();
            int width = (int)available.X;
            int height = (int)available.Y - 4;
            if (width <= 1 || height <= 1) return;

            int[] previousViewport = new int[4];
            GL.GetInteger(GetPName.Viewport, previousViewport);
            renderTarget.RenderToTexture(width, height, () => {
                AdvanceAnimation();
                renderer.Render(camera, width, height, settings, clearColor, clearColor, new Vec2(100000, 200000));
            });
            GL.Viewport(previousViewport[0], previousViewport[1], previousViewport[2], previousViewport[3]);

            unsafe {
                ImGui.Image(new ImTextureRef(default, (ulong)(uint)renderTarget.ColorTexture), new Vec2(width, height), new Vec2(0, 1), new Vec2(1, 0));
            }
            HandleInput(ImGui.IsItemHovered());
        }

        private void HandleInput(bool hovered) {
            double now = clock.Elapsed.TotalSeconds;
            float elapsed = (float)Math.Min(0.05, now - lastTick);
            lastTick = now;

            var io = ImGui.GetIO();
            var mouse = io.MousePos;
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) { rightDragging = true; MainViewFrame.LockCursor(owner.Viewer, true); }
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle)) middleDragging = true;
            if (rightDragging && ImGui.IsMouseReleased(ImGuiMouseButton.Right)) { rightDragging = false; MainViewFrame.LockCursor(owner.Viewer, false); }
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Middle)) middleDragging = false;

            float deltaX = mouse.X - lastMouse.X, deltaY = mouse.Y - lastMouse.Y;
            if (rightDragging) camera.Rotate(deltaX * 0.005f, -deltaY * 0.005f);
            else if (middleDragging) camera.Position += (-camera.Right * deltaX + camera.Up * deltaY) * (camera.Speed * 0.002f);
            lastMouse = mouse;

            if (hovered && io.MouseWheel != 0)
                camera.Speed = Math.Clamp(camera.Speed * (io.MouseWheel > 0 ? 1.25f : 0.8f), 10f, 200000f);
            if (!hovered && !rightDragging && !middleDragging) return;
            MainViewFrame.Fly(camera, elapsed);
            if (ImGui.IsKeyPressed(ImGuiKey.F)) FrameModel();
            if (ImGui.IsKeyPressed(ImGuiKey.Space)) TogglePlay();
        }

        private void AdvanceAnimation() {
            if (renderer.Anim == null) return;
            double now = clock.Elapsed.TotalSeconds;
            if (playing && clip != null) {
                float elapsed = (float)Math.Min(0.1, now - lastAnimationTick);
                animationFrame += elapsed * FramesPerSecond;
                float length = clip.End - clip.Start;
                if (animationFrame > clip.End) {
                    if (loop && length > 0) animationFrame = clip.Start + (animationFrame - clip.Start) % length;
                    else { animationFrame = clip.End; playing = false; }
                }
            }
            lastAnimationTick = now;
            float frame = clip == null ? -1 : Math.Min(animationFrame, Math.Max(0, renderer.Anim.Anim.FrameCount - 1));
            renderer.SetPose(frame);
        }

        private void ClearAnimation() {
            animation = null;
            clips = null;
            clip = null;
            playing = false;
            animationFrame = -1;
            currentAnimationName = null;
        }

        private async Task LoadModel(string name) {
            if (game == null || loading) return;
            loading = true;
            currentModel = name;
            currentAnimations = new List<string>();
            ClearAnimation();
            var stopwatch = Stopwatch.StartNew();
            try {
                var cache = new AssetCache(game);
                assets = cache;
                var scene = await Task.Run(() => {
                    var model = cache.GetModel(name, null);
                    var level = new LevelDef { Name = name };
                    var chunkDef = new ChunkDef { Name = name };
                    level.Chunks.Add(chunkDef);
                    var modelScene = new LevelScene { Def = level, Assets = cache };
                    var chunk = new SceneChunk { Def = chunkDef };
                    if (model != null) {
                        var levelObject = new LevelObject { ClassName = name, ChunkName = name, Section = new BniSection { Name = name }, Category = ObjectCategory.Prop };
                        chunk.Objects.Add(levelObject);
                        chunk.ObjectModels[levelObject] = model;
                    }
                    else modelScene.Warnings.Add("Could not load model " + name);
                    modelScene.Chunks.Add(chunk);
                    modelScene.Warnings.AddRange(cache.Warnings);
                    return modelScene;
                });

                owner.Viewer.RunOnMainThread(() => {
                    renderer.Load(scene);
                    settings.ClearSceneVisibility();
                    FrameModel();
                    int triangles = renderer.Items.Sum(item => item.Model.Parts.Sum(part => part.Triangles));
                    status = scene.Warnings.Count > 0 ? string.Join("; ", scene.Warnings.Distinct()) : $"{triangles:N0} triangles — {stopwatch.ElapsedMilliseconds} ms";
                    _ = ShowAnimationsFor();
                });
            }
            catch (Exception exception) {
                status = "Load failed: " + exception.Message;
            }
            finally { loading = false; }
        }

        private void FrameModel() {
            var min = renderer.SceneMin;
            var max = renderer.SceneMax;
            if (settings.MirrorX) {
                var mirroredMin = new Vec3(-max.X, min.Y, min.Z);
                var mirroredMax = new Vec3(-min.X, max.Y, max.Z);
                min = mirroredMin;
                max = mirroredMax;
            }
            camera.Frame(min, max, fromFront: true);
        }

        private async Task ShowAnimationsFor() {
            if (animationsByModel == null) {
                if (animationIndexBuilding) return;
                animationIndexBuilding = true;
                var indexedGame = game;
                var cache = new AssetCache(indexedGame);
                try {
                    var index = await Task.Run(() => BuildAnimationIndex(indexedGame, cache));
                    if (!ReferenceEquals(indexedGame, game)) return;
                    animationsByModel = index;
                }
                finally { animationIndexBuilding = false; }
            }
            if (currentModel == null) return;
            currentAnimations = animationsByModel.TryGetValue(currentModel, out var list) ? list : new List<string>();
        }

        private static Dictionary<string, List<string>> BuildAnimationIndex(GameData indexedGame, AssetCache cache) {
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in AnimationLookup.AllAnimations(indexedGame)) {
                Animation candidate;
                try { candidate = cache.GetAnimation(name); }
                catch (Exception) { candidate = null; }
                string model = candidate == null ? null : AnimationLookup.FindModelFor(indexedGame, name, candidate);
                if (model == null) continue;
                if (!index.TryGetValue(model, out var list)) index[model] = list = new List<string>();
                list.Add(name);
            }
            foreach (var list in index.Values) list.Sort(StringComparer.OrdinalIgnoreCase);
            return index;
        }

        private async Task ApplyAnimation(string name) {
            if (assets == null || currentModel == null) return;
            if (name == currentAnimationName) return;
            if (name == null) {
                ClearAnimation();
                renderer.SetAnimation(renderer.Items.FirstOrDefault(candidate => candidate.Kind == ItemKind.Object), null);
                return;
            }

            var cache = assets;
            try {
                var (loaded, loadedClips) = await Task.Run(() => {
                    var loadedAnimation = cache.GetAnimation(name);
                    return (loadedAnimation, loadedAnimation == null ? null : cache.GetAnimSet(name, loadedAnimation));
                });
                owner.Viewer.RunOnMainThread(() => {
                    animation = loaded;
                    clips = loadedClips;
                    currentAnimationName = name;
                    var item = renderer.Items.FirstOrDefault(candidate => candidate.Kind == ItemKind.Object);
                    renderer.SetAnimation(item, loaded);
                    string warning = AnimationLookup.CompatibilityWarning(item?.Model.Source.Model, loaded);
                    if (warning != null) status = warning;
                    owner.Viewer.ShowProperties(PropertiesView.ForAnimation(name, loaded, loadedClips, currentModel));

                    var firstClip = clips?.Clips.FirstOrDefault(candidate => candidate.End > candidate.Start);
                    if (firstClip != null) SelectClip(firstClip);
                    else {
                        clip = null;
                        playing = false;
                        animationFrame = -1;
                    }
                });
            }
            catch (Exception exception) {
                status = $"{name} failed to load: {exception.Message}";
            }
        }

        public override void Dispose() {
            base.Dispose();
            renderer.Dispose();
            renderTarget.Dispose();
        }
    }
}
