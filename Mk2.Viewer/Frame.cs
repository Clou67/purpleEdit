using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using Mk2.Viewer.Frames;

namespace Mk2.Viewer.ImGuiUi
{
    /// <summary>
    /// Base class for a top-level ImGui window ("frame"). Same shape as HexaSamples/Frame.cs:
    /// each frame owns a stable ###id so retitling doesn't spawn a new window, and the host
    /// just loops over open frames and calls RenderAsWindow on each.
    /// </summary>
    public abstract class Frame
    {
        protected readonly MainFrame owner;
        protected abstract string frameName { get; set; }
        public string WindowTitle => frameName;

        public bool isOpen = true;
        private readonly string _frameId;
        private static readonly HashSet<string> ClaimedIds = new HashSet<string>();

        protected Frame(MainFrame owner, string title)
        {
            this.owner = owner;
            _frameId = ClaimId(GetType());
            SetWindowTitle(title);
        }

        private static string ClaimId(Type type)
        {
            for (uint i = 0; ; i++)
            {
                string candidate = type.Name + "#" + i;
                if (ClaimedIds.Add(candidate)) return candidate;
            }
        }

        protected void SetWindowTitle(string title)
        {
            // "###id" pins the ImGui window ID so changing the title doesn't create a new window.
            frameName = title + " ###" + _frameId;
        }

        public abstract void Render(float deltaTime);

        public virtual void RenderAsWindow(float deltaTime)
        {
            if (ImGui.Begin(frameName, ref isOpen))
            {
                Render(deltaTime);
            }
            ImGui.End();
        }

        public virtual void Dispose()
        {
            ClaimedIds.Remove(_frameId);
        }
    }
}
