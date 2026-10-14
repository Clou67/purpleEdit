using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Mk2.Viewer.Utils
{
    public static class CrossFileDialog
    {
        public class NoImplementationException : Exception { }

        public static string OpenFile(string title = "Choose a file", string filter = "")
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Win32Dialog.OpenFile(title, filter);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return PrefersKdialog() ? KDialog.OpenFile(title, filter) : Zenity.OpenFile(title, filter);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return MacDialog.OpenFile(title);

            throw new NoImplementationException();
        }

        public static List<string> OpenMultipleFiles(string title = "Choose one or more files")
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Win32Dialog.OpenMultipleFiles(title);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return PrefersKdialog() ? KDialog.OpenMultipleFiles(title) : Zenity.OpenMultipleFiles(title);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return MacDialog.OpenMultipleFiles(title);

            throw new NoImplementationException();
        }

        public static string SaveFile(string title = "Enter the name of the file to save to", string filter = "")
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Win32Dialog.SaveFile(title, filter);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return PrefersKdialog() ? KDialog.SaveFile(title) : Zenity.SaveFile(title);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return MacDialog.SaveFile(title);

            throw new NoImplementationException();
        }

        public static string OpenFolder(string title = "Choose a folder")
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Win32Dialog.OpenFolder(title);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return PrefersKdialog() ? KDialog.OpenFolder(title) : Zenity.OpenFolder(title);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return MacDialog.OpenFolder(title);

            throw new NoImplementationException();
        }

        private static bool ExecutableInPath(string fileName)
        {
            if (File.Exists(fileName))
                return true;

            string values = Environment.GetEnvironmentVariable("PATH");
            if (values != null)
            {
                foreach (var path in values.Split(Path.PathSeparator))
                {
                    var fullPath = Path.Combine(path, fileName);
                    if (File.Exists(fullPath))
                        return true;
                }
            }

            return false;
        }

        private static bool PrefersKdialog()
        {
            string desktopSession = Environment.GetEnvironmentVariable("DESKTOP_SESSION");
            string xdgCurrentDesktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");

            bool probablyKde = (desktopSession != null && desktopSession.ToLower() == "kde") ||
                                (xdgCurrentDesktop != null && xdgCurrentDesktop.ToLower() == "kde");

            bool hasKdialogBinary = ExecutableInPath("kdialog");
            bool hasZenityBinary = ExecutableInPath("zenity");

            if (hasKdialogBinary && (probablyKde || !hasZenityBinary))
                return true;

            if (hasZenityBinary)
                return false;

            throw new NoImplementationException();
        }

        private static string RunProcess(string filename, List<string> args)
        {
            using var process = new Process();
            process.StartInfo.FileName = filename;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            foreach (string argument in args)
                process.StartInfo.ArgumentList.Add(argument);

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            return output.Replace("\n", "").Replace("\r", "");
        }

        // ---------------------------------------------------------------- Linux: kdialog

        private static class KDialog
        {
            private static string RunKDialog(List<string> args = null, Dictionary<string, string> kwargs = null)
            {
                var zArgs = new List<string>();
                if (args != null)
                    foreach (string argument in args)
                        zArgs.Add("--" + argument);

                if (kwargs != null)
                    foreach (var entry in kwargs)
                    {
                        zArgs.Add("--" + entry.Key);
                        zArgs.Add(entry.Value);
                    }

                return RunProcess("kdialog", zArgs);
            }

            public static string OpenFile(string title, string filter = "") =>
                RunKDialog(new List<string> { "getopenfilename" }, new Dictionary<string, string> { { "title", title } });

            public static List<string> OpenMultipleFiles(string title)
            {
                var result = RunKDialog(new List<string> { "getopenfilename", "multiple" },
                    new Dictionary<string, string> { { "title", title } });
                return result.Split(' ').Where(path => path.Length > 0).ToList();
            }

            public static string SaveFile(string title) =>
                RunKDialog(new List<string> { "getsavefilename" }, new Dictionary<string, string> { { "title", title } });

            public static string OpenFolder(string title) =>
                RunKDialog(new List<string> { "getexistingdirectory" }, new Dictionary<string, string> { { "title", title } });
        }

        // ---------------------------------------------------------------- Linux: zenity

        private static class Zenity
        {
            private static string RunZenity(List<string> args = null, Dictionary<string, string> kwargs = null)
            {
                var zArgs = new List<string>();
                if (args != null)
                    foreach (string argument in args)
                        zArgs.Add("--" + argument);

                if (kwargs != null)
                    foreach (var entry in kwargs)
                        zArgs.Add("--" + entry.Key + "=" + entry.Value);

                return RunProcess("zenity", zArgs);
            }

            public static string OpenFile(string title, string filter = "") =>
                RunZenity(new List<string> { "file-selection" }, new Dictionary<string, string> { { "title", title } });

            public static List<string> OpenMultipleFiles(string title)
            {
                var result = RunZenity(new List<string> { "file-selection", "multiple" },
                    new Dictionary<string, string> { { "title", title } });
                return result.Split("|").Where(path => path.Length > 0).ToList();
            }

            public static string SaveFile(string title) =>
                RunZenity(new List<string> { "file-selection", "save", "confirm-overwrite" },
                    new Dictionary<string, string> { { "title", title } });

            public static string OpenFolder(string title) =>
                RunZenity(new List<string> { "file-selection", "directory" },
                    new Dictionary<string, string> { { "title", title } });
        }

        // ---------------------------------------------------------------- macOS: osascript

        private static class MacDialog
        {
            private static string RunOsaScript(string script)
            {
                var result = RunProcess("osascript", new List<string> { "-e", script });
                return result;
            }

            public static string OpenFile(string title) =>
                PosixPath(RunOsaScript($"POSIX path of (choose file with prompt \"{Escape(title)}\")"));

            public static List<string> OpenMultipleFiles(string title)
            {
                var raw = RunOsaScript(
                    $"set theFiles to choose file with prompt \"{Escape(title)}\" with multiple selections allowed\n" +
                    "set out to \"\"\n" +
                    "repeat with f in theFiles\n" +
                    "  set out to out & POSIX path of f & \"\\n\"\n" +
                    "end repeat\n" +
                    "return out");
                return raw.Split('\n').Where(path => path.Length > 0).ToList();
            }

            public static string SaveFile(string title) =>
                PosixPath(RunOsaScript($"POSIX path of (choose file name with prompt \"{Escape(title)}\")"));

            public static string OpenFolder(string title) =>
                PosixPath(RunOsaScript($"POSIX path of (choose folder with prompt \"{Escape(title)}\")"));

            private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

            private static string PosixPath(string path) => path.TrimEnd('/');
        }

        // ---------------------------------------------------------------- Windows: native IFileDialog (no WinForms)

        private static class Win32Dialog
        {
            // IFileOpenDialog / IFileSaveDialog via COM, so this works without a
            // System.Windows.Forms / WinForms dependency.

            private const string CLSID_FileOpenDialog = "DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7";
            private const string CLSID_FileSaveDialog = "C0B4E2F3-BA21-4773-8DBA-335EC946EB8B";
            private const string IID_IFileOpenDialog = "D57C7288-D4AD-4768-BE02-9D969532D960";
            private const string IID_IFileSaveDialog = "84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB";

            private const uint FOS_PICKFOLDERS = 0x00000020;
            private const uint FOS_FORCEFILESYSTEM = 0x00000040;
            private const uint FOS_ALLOWMULTISELECT = 0x00000200;
            private const uint FOS_OVERWRITEPROMPT = 0x00000002;

            [ComImport, Guid(IID_IFileOpenDialog), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IFileOpenDialog
            {
                [PreserveSig] int Show(IntPtr parent);
                void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
                void SetFileTypeIndex(uint iFileType);
                void GetFileTypeIndex(out uint piFileType);
                void Advise(IntPtr pfde, out uint pdwCookie);
                void Unadvise(uint dwCookie);
                void SetOptions(uint fos);
                void GetOptions(out uint pfos);
                void SetDefaultFolder(IShellItem psi);
                void SetFolder(IShellItem psi);
                void GetFolder(out IShellItem ppsi);
                void GetCurrentSelection(out IShellItem ppsi);
                void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
                void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
                void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
                void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
                void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
                void GetResult(out IShellItem ppsi);
                void AddPlace(IShellItem psi, uint fdap);
                void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
                void Close(int result);
                void SetClientGuid(ref Guid guid);
                void ClearClientData();
                void SetFilter(IntPtr pFilter);
                void GetResults(out IShellItemArray ppenum);
                void GetSelectedItems(out IShellItemArray ppsai);
            }

            [ComImport, Guid(IID_IFileSaveDialog), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IFileSaveDialog
            {
                [PreserveSig] int Show(IntPtr parent);
                void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
                void SetFileTypeIndex(uint iFileType);
                void GetFileTypeIndex(out uint piFileType);
                void Advise(IntPtr pfde, out uint pdwCookie);
                void Unadvise(uint dwCookie);
                void SetOptions(uint fos);
                void GetOptions(out uint pfos);
                void SetDefaultFolder(IShellItem psi);
                void SetFolder(IShellItem psi);
                void GetFolder(out IShellItem ppsi);
                void GetCurrentSelection(out IShellItem ppsi);
                void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
                void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
                void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
                void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
                void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
                void GetResult(out IShellItem ppsi);
            }

            [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IShellItem
            {
                void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
                void GetParent(out IShellItem ppsi);
                void GetDisplayName(uint sigdnName, out IntPtr ppszName);
                void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
                void Compare(IShellItem psi, uint hint, out int piOrder);
            }

            [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IShellItemArray
            {
                void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
                void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
                void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
                void GetAttributes(int AttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
                void GetCount(out uint pdwNumItems);
                void GetItemAt(uint dwIndex, out IShellItem ppsi);
                void EnumItems(out IntPtr ppenumShellItems);
            }

            private const uint SIGDN_FILESYSPATH = 0x80058000;

            private static string GetPath(IShellItem item)
            {
                item.GetDisplayName(SIGDN_FILESYSPATH, out var pathPointer);
                var path = Marshal.PtrToStringUni(pathPointer);
                Marshal.FreeCoTaskMem(pathPointer);
                return path;
            }

            public static string OpenFile(string title, string filter = "")
            {
                var type = Type.GetTypeFromCLSID(new Guid(CLSID_FileOpenDialog));
                var dialog = (IFileOpenDialog)Activator.CreateInstance(type);
                try
                {
                    dialog.SetOptions(FOS_FORCEFILESYSTEM);
                    dialog.SetTitle(title);
                    if (dialog.Show(IntPtr.Zero) != 0) return string.Empty;
                    dialog.GetResult(out var item);
                    return GetPath(item);
                }
                catch (COMException)
                {
                    return string.Empty; // user cancelled (HRESULT_FROM_WIN32(ERROR_CANCELLED))
                }
                finally
                {
                    Marshal.ReleaseComObject(dialog);
                }
            }

            public static List<string> OpenMultipleFiles(string title)
            {
                var type = Type.GetTypeFromCLSID(new Guid(CLSID_FileOpenDialog));
                var dialog = (IFileOpenDialog)Activator.CreateInstance(type);
                try
                {
                    dialog.SetOptions(FOS_FORCEFILESYSTEM | FOS_ALLOWMULTISELECT);
                    dialog.SetTitle(title);
                    if (dialog.Show(IntPtr.Zero) != 0) return new List<string>();
                    dialog.GetResults(out var items);
                    items.GetCount(out var count);
                    var result = new List<string>();
                    for (uint index = 0; index < count; index++)
                    {
                        items.GetItemAt(index, out var item);
                        result.Add(GetPath(item));
                    }
                    return result;
                }
                catch (COMException)
                {
                    return new List<string>();
                }
                finally
                {
                    Marshal.ReleaseComObject(dialog);
                }
            }

            public static string SaveFile(string title, string filter = "")
            {
                var type = Type.GetTypeFromCLSID(new Guid(CLSID_FileSaveDialog));
                var dialog = (IFileSaveDialog)Activator.CreateInstance(type);
                try
                {
                    dialog.SetOptions(FOS_FORCEFILESYSTEM | FOS_OVERWRITEPROMPT);
                    dialog.SetTitle(title);
                    if (dialog.Show(IntPtr.Zero) != 0) return string.Empty;
                    dialog.GetResult(out var item);
                    return GetPath(item);
                }
                catch (COMException)
                {
                    return string.Empty;
                }
                finally
                {
                    Marshal.ReleaseComObject(dialog);
                }
            }

            public static string OpenFolder(string title)
            {
                var type = Type.GetTypeFromCLSID(new Guid(CLSID_FileOpenDialog));
                var dialog = (IFileOpenDialog)Activator.CreateInstance(type);
                try
                {
                    dialog.SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
                    dialog.SetTitle(title);
                    if (dialog.Show(IntPtr.Zero) != 0) return string.Empty;
                    dialog.GetResult(out var item);
                    return GetPath(item);
                }
                catch (COMException)
                {
                    return string.Empty; // user cancelled
                }
                finally
                {
                    Marshal.ReleaseComObject(dialog);
                }
            }
        }
    }
}
