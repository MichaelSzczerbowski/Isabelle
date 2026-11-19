using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace IslandPlayer
{
    /// <summary>Wywołania WinAPI: styl okna, pełny ekran, okienko głośności Windows, skróty, autostart.</summary>
    internal static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void MakeToolWindow(IntPtr hwnd)
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        public static void KeepTopmost(IntPtr hwnd)
        {
            // HWND_TOPMOST, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        public static void DarkTitleBar(IntPtr hwnd)
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }

        /// <summary>True, gdy aktywne okno (gra, film) zajmuje cały monitor, na którym jest wyspa.</summary>
        public static bool IsForegroundFullscreen(IntPtr own)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == own) return false;
            var sb = new StringBuilder(256);
            GetClassName(fg, sb, 256);
            string c = sb.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return false;
            IntPtr mon = MonitorFromWindow(fg, 2);
            if (mon != MonitorFromWindow(own, 2)) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;
            if (!GetWindowRect(fg, out RECT r)) return false;
            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                   r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }

        /// <summary>Chowa (albo przywraca) systemowe okienko głośności Windows 10.</summary>
        public static bool SetVolumeOsdVisible(bool visible)
        {
            IntPtr h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "NativeHWNDHost", null);
            while (h != IntPtr.Zero)
            {
                if (FindWindowEx(h, IntPtr.Zero, "DirectUIHWND", null) != IntPtr.Zero)
                {
                    ShowWindow(h, visible ? 9 : 6); // SW_RESTORE / SW_MINIMIZE
                    return true;
                }
                h = FindWindowEx(IntPtr.Zero, h, "NativeHWNDHost", null);
            }
            return false;
        }
    }

    /// <summary>Uruchamianie razem z Windows (klucz Run w rejestrze użytkownika).</summary>
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Name = "Isabelle";

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                        return k?.GetValue(Name) != null || k?.GetValue("IslandPlayer") != null; // także stara nazwa
                }
                catch { return false; }
            }
        }

        public static void Set(bool on) => SetPath(on ? Environment.ProcessPath : null);

        /// <summary>exe = ścieżka programu albo null (wyłącz autostart).</summary>
        public static void SetPath(string exe)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (!string.IsNullOrEmpty(exe)) k.SetValue(Name, "\"" + exe + "\"");
                    else k.DeleteValue(Name, false);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }
    }
}
