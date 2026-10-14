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
            string text = owner.Viewer.LogText;
            ImGui.InputTextMultiline("##log", ref text, (nuint)(text.Length + 1), new System.Numerics.Vector2(-1, -1),
                ImGuiInputTextFlags.ReadOnly);
        }
    }
}
