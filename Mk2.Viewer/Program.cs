using System;
using System.Windows.Forms;

namespace Mk2.Viewer
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
                MessageBox.Show(e.Exception.ToString(), "Unhandled error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Run(new MainForm(args));
        }
    }
}
