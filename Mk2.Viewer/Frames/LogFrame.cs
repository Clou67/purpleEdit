using Hexa.NET.ImGui;
using Mk2.Viewer.Frames;

namespace Mk2.Viewer.ImGuiUi.Frames
{
    public sealed class LogFrame : Frame
    {
        protected override string frameName { get; set; }

        public LogFrame(MainFrame owner) : base(owner, "Log") { }

        public override void Render(float deltaTime)
        {
            string text = owner.GetLogText() ?? "";
            ImGui.InputTextMultiline("##log", ref text, 65536, new System.Numerics.Vector2(-1, -1),
                ImGuiInputTextFlags.ReadOnly);
        }
    }
}
