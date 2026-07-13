using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PowerShellPlus
{
    public static class ShellLocator
    {
        public static string FindBestShell()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pwshRoot = Path.Combine(programFiles, "PowerShell");
            if (Directory.Exists(pwshRoot))
            {
                try
                {
                    string[] candidates = Directory.GetFiles(pwshRoot, "pwsh.exe", SearchOption.AllDirectories);
                    if (candidates.Length > 0) return candidates[candidates.Length - 1];
                }
                catch { }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        }
    }

    public static class WorkspaceStore
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 10 * 1024 * 1024 };

        public static string AppDataDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerShellPlus"); }
        }

        public static string DefaultPath { get { return Path.Combine(AppDataDirectory, "workspace.json"); } }
        public static string LogPath { get { return Path.Combine(AppDataDirectory, "PowerShellPlus.log"); } }

        public static WorkspaceState Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return WorkspaceState.CreateDefault();
                WorkspaceState state = Serializer.Deserialize<WorkspaceState>(File.ReadAllText(path));
                if (state == null) return WorkspaceState.CreateDefault();
                state.Normalize();
                return state;
            }
            catch (Exception ex)
            {
                Log("Could not load workspace: " + ex);
                return WorkspaceState.CreateDefault();
            }
        }

        public static void Save(WorkspaceState state, string path)
        {
            state.Normalize();
            string directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            string temp = path + ".tmp";
            File.WriteAllText(temp, Serializer.Serialize(state));
            if (File.Exists(path))
            {
                string backup = path + ".bak";
                try { File.Replace(temp, path, backup, true); }
                catch { File.Copy(temp, path, true); File.Delete(temp); }
            }
            else File.Move(temp, path);
        }

        public static WorkspaceState Clone(WorkspaceState state)
        {
            return Serializer.Deserialize<WorkspaceState>(Serializer.Serialize(state));
        }

        public static void Log(string message)
        {
            try
            {
                if (!Directory.Exists(AppDataDirectory)) Directory.CreateDirectory(AppDataDirectory);
                File.AppendAllText(LogPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine);
            }
            catch { }
        }
    }

    public static class LayoutEngine
    {
        public static Rectangle[] Calculate(string layout, Rectangle area, int count)
        {
            if (count <= 0) return new Rectangle[0];
            Rectangle[] result = new Rectangle[count];
            const int gap = 6;
            area = new Rectangle(area.X + gap, area.Y + gap, Math.Max(1, area.Width - gap * 2), Math.Max(1, area.Height - gap * 2));

            if (layout == "Rows")
            {
                for (int i = 0; i < count; i++) result[i] = Slice(area, 1, count, 0, i, gap);
                return result;
            }
            if (layout == "Columns")
            {
                for (int i = 0; i < count; i++) result[i] = Slice(area, count, 1, i, 0, gap);
                return result;
            }
            if (layout == "Cascade")
            {
                int offset = Math.Min(32, Math.Max(10, area.Width / Math.Max(4, count + 2)));
                int width = Math.Max(260, area.Width - offset * Math.Min(count - 1, 6));
                int height = Math.Max(180, area.Height - offset * Math.Min(count - 1, 6));
                for (int i = 0; i < count; i++)
                {
                    int step = i % 7;
                    result[i] = new Rectangle(area.X + step * offset, area.Y + step * offset, Math.Min(width, area.Width), Math.Min(height, area.Height));
                }
                return result;
            }

            int columns = (int)Math.Ceiling(Math.Sqrt(count));
            int rows = (int)Math.Ceiling((double)count / columns);
            for (int i = 0; i < count; i++) result[i] = Slice(area, columns, rows, i % columns, i / columns, gap);
            return result;
        }

        private static Rectangle Slice(Rectangle area, int columns, int rows, int column, int row, int gap)
        {
            int cellWidth = area.Width / columns;
            int cellHeight = area.Height / rows;
            int x = area.X + column * cellWidth;
            int y = area.Y + row * cellHeight;
            int width = column == columns - 1 ? area.Right - x : cellWidth;
            int height = row == rows - 1 ? area.Bottom - y : cellHeight;
            return new Rectangle(x, y, Math.Max(1, width - gap), Math.Max(1, height - gap));
        }
    }

    public static class NativeConsoleWindowManager
    {
        private const int SW_RESTORE = 9;
        [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);

        public static List<IntPtr> FindWindows()
        {
            List<IntPtr> handles = new List<IntPtr>();
            string[] names = { "powershell", "pwsh", "WindowsTerminal" };
            foreach (string name in names)
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (process.MainWindowHandle != IntPtr.Zero && !handles.Contains(process.MainWindowHandle)) handles.Add(process.MainWindowHandle);
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
            return handles;
        }

        public static int Arrange(string layout)
        {
            List<IntPtr> windows = FindWindows();
            Rectangle[] bounds = LayoutEngine.Calculate(layout, Screen.PrimaryScreen.WorkingArea, windows.Count);
            for (int i = 0; i < windows.Count; i++)
            {
                ShowWindow(windows[i], SW_RESTORE);
                MoveWindow(windows[i], bounds[i].X, bounds[i].Y, bounds[i].Width, bounds[i].Height, true);
            }
            return windows.Count;
        }
    }
}
