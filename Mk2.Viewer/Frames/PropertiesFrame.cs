using System.Collections.Generic;
using Hexa.NET.ImGui;
using Mk2.Viewer.Frames;

namespace Mk2.Viewer.ImGuiUi.Frames
{
    public sealed class PropertiesFrame : Frame
    {
        protected override string frameName { get; set; }

        public PropertiesFrame(MainFrame owner) : base(owner, "Properties") { }

        public override void Render(float deltaTime)
        {
            var view = owner.Viewer.Properties;
            if (view.Title.Length == 0 && view.Sections.Count == 0) return;

            if (view.Title.Length > 0) ImGui.SeparatorText(view.Title);

            foreach (var section in view.Sections)
            {
                if (!ImGui.CollapsingHeader(section.Heading, ImGuiTreeNodeFlags.DefaultOpen)) continue;
                ImGui.PushID(section.Heading);
                ImGui.TextUnformatted(section.Body);
                if (ImGui.BeginPopupContextItem("##copy"))
                {
                    if (ImGui.MenuItem("Copy")) ImGui.SetClipboardText(section.Body);
                    ImGui.EndPopup();
                }
                ImGui.PopID();
            }

            if (view.Scripts.Count > 0 && ImGui.CollapsingHeader($"Scripts ({view.Scripts.Count})", ImGuiTreeNodeFlags.DefaultOpen))
            {
                foreach (var script in view.Scripts) ImGui.TextWrapped(script);
            }

            if (view.ShowsLinks)
            {
                RenderLinks($"References ({view.References.Count})", "references", view.References);
                RenderLinks($"Referenced by ({view.ReferencedBy.Count})", "referencedBy", view.ReferencedBy);
            }
        }

        private void RenderLinks(string heading, string id, List<PropertyLink> links)
        {
            ImGui.PushID(id);
            if (ImGui.CollapsingHeader(heading, ImGuiTreeNodeFlags.DefaultOpen))
            {
                for (int index = 0; index < links.Count; index++)
                {
                    var link = links[index];
                    ImGui.PushID(index);
                    if (ImGui.Selectable(link.Label)) owner.Viewer.FollowLink(link.Target);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(link.Via);
                    ImGui.PopID();
                }
            }
            ImGui.PopID();
        }
    }
}
