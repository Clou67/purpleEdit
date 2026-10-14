using Hexa.NET.ImGui;
using Mk2.Viewer.Frames;

namespace Mk2.Viewer.ImGuiUi.Frames
{
    /// <summary>Properties of the current selection — its own frame, same as the old right-panel "Properties" tab.</summary>
    public sealed class PropertiesFrame : Frame
    {
        protected override string frameName { get; set; }

        public PropertiesFrame(MainFrame owner) : base(owner, "Properties") { }

        public override void Render(float deltaTime)
        {
            string text = owner.GetPropertiesText() ?? "";
            if (text.Length == 0)
            {
                ImGui.TextDisabled("Nothing selected.");
                return;
            }

            ImGui.InputTextMultiline("##properties", ref text, 65536, new System.Numerics.Vector2(-1, -1),
                ImGuiInputTextFlags.ReadOnly);

            // TODO: the WinForms version also has a clickable "Links" list under Properties for
            // jumping between referenced objects — not ported yet, keeping this basic for now.
        }
    }
}
