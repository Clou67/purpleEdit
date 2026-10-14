using Hexa.NET.ImGui;
using Mk2.Game;
using Mk2.Viewer.Frames;

namespace Mk2.Viewer.ImGuiUi.Frames
{
    public sealed class RenderModesFrame : Frame
    {
        protected override string frameName { get; set; }

        public RenderModesFrame(MainFrame owner) : base(owner, "Render") { }

        public override void Render(float deltaTime)
        {
            var vs = owner.ViewSettings;
            if (vs == null) { ImGui.TextDisabled("No scene loaded."); return; }

            if (ImGui.BeginChild("##renderModesScroll"))
            {
                ImGui.SeparatorText("Geometry");
                Checkbox("Level geometry (overlays)", ref vs.Overlays);
                Checkbox("Objects", ref vs.Objects);
                Checkbox("Sky dome", ref vs.EnvDome);

                ImGui.SeparatorText("Shading");
                Checkbox("Textures", ref vs.Textures);
                Checkbox("Vertex colours (prelighting)", ref vs.VertexColors);
                Checkbox("Directional lighting", ref vs.Lighting);
                Checkbox("Transparency / blending", ref vs.Transparency);
                Checkbox("Wireframe", ref vs.Wireframe);
                Checkbox("Cull back faces", ref vs.CullBackfaces);
                Checkbox("Fog (from levels.ini)", ref vs.Fog);
                ImGui.SliderFloat("Brightness", ref vs.Brightness, 1.0f, 2.0f);

                ImGui.SeparatorText("Collision");
                Checkbox("Ground collision", ref vs.GroundCollision);
                Checkbox("Object collision (.cgr)", ref vs.ObjectCollision);
                Checkbox("Collision as translucent overlay", ref vs.CollisionOverlay);
                Checkbox("Model collision components (c_*)", ref vs.ModelCollisionComponents);
                Checkbox("Shatter fragments (f_*)", ref vs.Fragments);

                ImGui.SeparatorText("Animation");
                Checkbox("Skeleton", ref vs.Skeleton);
                Checkbox("Animated mesh", ref vs.AnimatedMesh);

                ImGui.SeparatorText("Misc");
                Checkbox("Trigger / volume / path shapes", ref vs.Shapes);
                Checkbox("Markers for objects without models", ref vs.Markers);
                Checkbox("Mirror X (match in-game handedness)", ref vs.MirrorX);

                ImGui.SeparatorText("Object categories");
                foreach (ObjectCategory c in System.Enum.GetValues(typeof(ObjectCategory)))
                {
                    bool visible = vs.CategoryVisible(c);
                    if (ImGui.Checkbox(c.ToString(), ref visible))
                    {
                        if (visible) vs.HiddenCategories.Remove(c); else vs.HiddenCategories.Add(c);
                        owner.Redraw();
                    }
                }
            }
            ImGui.EndChild();
        }

        private void Checkbox(string label, ref bool value)
        {
            bool before = value;
            ImGui.Checkbox(label, ref value);
            if (value != before) owner.Redraw();
        }
    }
}
