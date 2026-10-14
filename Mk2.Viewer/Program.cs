using System;

namespace Mk2.Viewer
{
    internal static class Program
    {
        [System.STAThread]
        static void Main(string[] args) {
            Window window = new Window(args);
            window.Run();
        }
    }
}
