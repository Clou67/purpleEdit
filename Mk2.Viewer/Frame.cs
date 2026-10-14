using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using Mk2.Viewer.Frames;

namespace Mk2.Viewer.ImGuiUi
{
    /// <summary>A top-level ImGui window with a stable ###id, so changing its title keeps the same window.</summary>
    public abstract class Frame
    {
        protected readonly MainFrame owner;
        protected abstract string frameName { get; set; }
        public string WindowTitle => frameName;

        public bool isOpen = true;
        private readonly string frameId;
        private static readonly HashSet<string> ClaimedIds = new HashSet<string>();

        protected Frame(MainFrame owner, string title)
        {
            this.owner = owner;
            frameId = ClaimId(GetType());
            SetWindowTitle(title);
        }

        private static string ClaimId(Type type)
        {
            for (uint number = 0; ; number++)
            {
                string candidate = type.Name + "#" + number;
                if (ClaimedIds.Add(candidate)) return candidate;
            }
        }

        protected void SetWindowTitle(string title)
        {
            // "###id" pins the ImGui window ID so changing the title doesn't create a new window.
            frameName = title + " ###" + frameId;
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
            ClaimedIds.Remove(frameId);
        }
    }
}
