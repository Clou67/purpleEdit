using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Mk2.Viewer
{
    /// <summary>Light / dark colour schemes applied recursively to the WinForms UI.</summary>
    public static class Theme
    {
        public static bool Dark { get; private set; }

        public static Color Back => Dark ? Color.FromArgb(30, 31, 34) : SystemColors.Control;
        public static Color Panel => Dark ? Color.FromArgb(37, 38, 42) : SystemColors.Window;
        public static Color Fore => Dark ? Color.FromArgb(222, 224, 228) : SystemColors.ControlText;
        public static Color Dim => Dark ? Color.FromArgb(140, 144, 152) : SystemColors.GrayText;
        public static Color Border => Dark ? Color.FromArgb(58, 60, 66) : SystemColors.ControlDark;
        public static Color Accent => Dark ? Color.FromArgb(70, 110, 170) : SystemColors.Highlight;
        public static Color Hover => Dark ? Color.FromArgb(55, 58, 66) : Color.FromArgb(220, 230, 245);

        public static void Apply(Form form, bool dark)
        {
            Dark = dark;
            form.BackColor = Back;
            form.ForeColor = Fore;
            ApplyRecursive(form);
            SetTitleBar(form, dark);
            form.Invalidate(true);
        }

        private static void ApplyRecursive(Control c)
        {
            switch (c)
            {
                case MenuStrip ms: Strip(ms); break;
                case StatusStrip ss: Strip(ss); break;
                case ToolStrip ts: Strip(ts); break;
                case TreeView tv:
                    tv.BackColor = Panel; tv.ForeColor = Fore;
                    tv.LineColor = Dim;
                    // Grey-out colours set per node for missing packs must stay readable.
                    foreach (TreeNode n in tv.Nodes) FixNode(n);
                    ScrollBars(tv);
                    break;
                case ListBox lb: lb.BackColor = Panel; lb.ForeColor = Fore; lb.BorderStyle = BorderStyle.None; ScrollBars(lb); break;
                case TextBox tb: tb.BackColor = Panel; tb.ForeColor = Fore; tb.BorderStyle = Dark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D; ScrollBars(tb); break;
                case TabControl tab: Tabs(tab); break;
                case TabPage tp: tp.BackColor = Back; tp.ForeColor = Fore; break;
                case SplitContainer sc: sc.BackColor = Border; sc.Panel1.BackColor = Back; sc.Panel2.BackColor = Back; break;
                case Label l: l.BackColor = Back; l.ForeColor = Dim; break;
                default:
                    if (c.GetType().Name != "GLControl") { c.BackColor = Back; c.ForeColor = Fore; }
                    break;
            }
            foreach (Control child in c.Controls) ApplyRecursive(child);
        }

        public static void FixNode(TreeNode n)
        {
            if (n.ForeColor != Color.Empty)
            {
                bool greyed = n.ForeColor == SystemColors.GrayText || n.ForeColor == Color.FromArgb(140, 144, 152);
                n.ForeColor = greyed ? Dim : Fore;
            }
            foreach (TreeNode c in n.Nodes) FixNode(c);
        }

        private static void Strip(ToolStrip s)
        {
            s.Renderer = Dark ? new ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false } : new ToolStripProfessionalRenderer();
            s.BackColor = Back;
            s.ForeColor = Fore;
            foreach (ToolStripItem i in s.Items) Item(i);
        }

        private static void Item(ToolStripItem i)
        {
            // Category buttons keep their own colours (they match the line colours in the viewport).
            if (i.Tag is Color tc) i.ForeColor = Dark ? tc : Color.FromArgb(tc.R * 160 / 255, tc.G * 160 / 255, tc.B * 160 / 255);
            else i.ForeColor = Fore;
            i.BackColor = Back;
            if (i is ToolStripDropDownItem dd)
            {
                dd.DropDown.BackColor = Panel;
                dd.DropDown.ForeColor = Fore;
                foreach (ToolStripItem c in dd.DropDownItems) Item(c);
            }
        }

        private static void Tabs(TabControl tab)
        {
            tab.DrawMode = TabDrawMode.OwnerDrawFixed;
            tab.DrawItem -= DrawTab;
            tab.DrawItem += DrawTab;
            tab.BackColor = Back;
            tab.Invalidate();
        }

        private static void DrawTab(object sender, DrawItemEventArgs e)
        {
            var tab = (TabControl)sender;
            var page = tab.TabPages[e.Index];
            bool sel = tab.SelectedIndex == e.Index;
            var r = tab.GetTabRect(e.Index);
            using (var b = new SolidBrush(sel ? Panel : Back)) e.Graphics.FillRectangle(b, r);
            if (sel) using (var p = new Pen(Accent, 2)) e.Graphics.DrawLine(p, r.Left + 1, r.Bottom - 1, r.Right - 1, r.Bottom - 1);
            TextRenderer.DrawText(e.Graphics, page.Text, tab.Font, r, sel ? Fore : Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ---- native bits (dark scrollbars / title bar); harmless no-ops on older Windows
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private static void ScrollBars(Control c)
        {
            void Do()
            {
                try { SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
            }
            if (c.IsHandleCreated) Do(); else c.HandleCreated += (s, e) => Do();
        }

        private static void SetTitleBar(Form f, bool dark)
        {
            void Do()
            {
                try
                {
                    int v = dark ? 1 : 0;
                    if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref v, 4);
                }
                catch { }
            }
            if (f.IsHandleCreated) Do(); else f.HandleCreated += (s, e) => Do();
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            private static readonly Color B = Color.FromArgb(30, 31, 34);
            private static readonly Color P = Color.FromArgb(37, 38, 42);
            private static readonly Color H = Color.FromArgb(55, 58, 66);
            private static readonly Color C = Color.FromArgb(62, 80, 110);
            private static readonly Color L = Color.FromArgb(58, 60, 66);
            public override Color ToolStripGradientBegin => B;
            public override Color ToolStripGradientMiddle => B;
            public override Color ToolStripGradientEnd => B;
            public override Color ToolStripBorder => B;
            public override Color MenuStripGradientBegin => B;
            public override Color MenuStripGradientEnd => B;
            public override Color StatusStripGradientBegin => B;
            public override Color StatusStripGradientEnd => B;
            public override Color ToolStripDropDownBackground => P;
            public override Color ImageMarginGradientBegin => P;
            public override Color ImageMarginGradientMiddle => P;
            public override Color ImageMarginGradientEnd => P;
            public override Color MenuBorder => L;
            public override Color MenuItemBorder => L;
            public override Color MenuItemSelected => H;
            public override Color MenuItemSelectedGradientBegin => H;
            public override Color MenuItemSelectedGradientEnd => H;
            public override Color MenuItemPressedGradientBegin => P;
            public override Color MenuItemPressedGradientMiddle => P;
            public override Color MenuItemPressedGradientEnd => P;
            public override Color ButtonSelectedHighlight => H;
            public override Color ButtonSelectedGradientBegin => H;
            public override Color ButtonSelectedGradientMiddle => H;
            public override Color ButtonSelectedGradientEnd => H;
            public override Color ButtonSelectedBorder => L;
            public override Color ButtonCheckedGradientBegin => C;
            public override Color ButtonCheckedGradientMiddle => C;
            public override Color ButtonCheckedGradientEnd => C;
            public override Color ButtonCheckedHighlight => C;
            public override Color ButtonPressedGradientBegin => C;
            public override Color ButtonPressedGradientMiddle => C;
            public override Color ButtonPressedGradientEnd => C;
            public override Color CheckBackground => C;
            public override Color CheckSelectedBackground => C;
            public override Color CheckPressedBackground => C;
            public override Color SeparatorDark => L;
            public override Color SeparatorLight => B;
            public override Color GripDark => L;
            public override Color GripLight => B;
        }
    }
}
