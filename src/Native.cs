using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassBar
{
    static class Native
    {
        // ---------------- blur behind (Windows 10) ----------------
        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }
        [StructLayout(LayoutKind.Sequential)]
        struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }
        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        public static void EnableBlur(IntPtr hwnd)
        {
            var accent = new AccentPolicy { AccentState = 3 };
            int size = Marshal.SizeOf(accent);
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, p, false);
                var data = new WindowCompositionAttributeData { Attribute = 19, Data = p, SizeOfData = size };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        // ---------------- window styles ----------------
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;

        public static void MakeToolWindow(IntPtr h, bool noActivate = false)
        {
            long ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
            ex = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
            if (noActivate) ex |= WS_EX_NOACTIVATE;
            SetWindowLongPtr(h, GWL_EXSTYLE, (IntPtr)ex);
        }

        // ---------------- app bar (reserve screen space) ----------------
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }
        [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_SETSTATE = 10, ABE_BOTTOM = 3;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);

        public static void AppBarRegister(IntPtr h, uint callback)
        {
            var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = h, uCallbackMessage = callback };
            SHAppBarMessage(ABM_NEW, ref d);
        }

        public static void AppBarRemove(IntPtr h)
        {
            var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = h };
            SHAppBarMessage(ABM_REMOVE, ref d);
        }

        /// Docks the bar at an edge (0 left, 1 top, 2 right, 3 bottom) of the monitor; returns the final rect in physical pixels.
        public static RECT AppBarSet(IntPtr h, RECT monitor, uint edge, int px)
        {
            var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = h, uEdge = edge, rc = monitor };
            void Fit()
            {
                switch (edge)
                {
                    case 0: d.rc.Right = d.rc.Left + px; break;
                    case 1: d.rc.Bottom = d.rc.Top + px; break;
                    case 2: d.rc.Left = d.rc.Right - px; break;
                    default: d.rc.Top = d.rc.Bottom - px; break;
                }
            }
            Fit();
            SHAppBarMessage(ABM_QUERYPOS, ref d);   // Windows may move it off other bars (e.g. the taskbar)
            Fit();
            SHAppBarMessage(ABM_SETPOS, ref d);
            return d.rc;
        }

        // ---------------- Windows taskbar auto-hide ----------------
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        const int ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;

        public static bool TaskbarAutoHide
        {
            get
            {
                var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindow("Shell_TrayWnd", null) };
                return ((int)SHAppBarMessage(ABM_GETSTATE, ref d) & ABS_AUTOHIDE) != 0;
            }
            set
            {
                var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindow("Shell_TrayWnd", null), lParam = (IntPtr)(value ? ABS_AUTOHIDE : ABS_ALWAYSONTOP) };
                SHAppBarMessage(ABM_SETSTATE, ref d);
            }
        }

        // ---------------- monitors ----------------
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);

        public static RECT PrimaryMonitor()
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(MonitorFromPoint(new POINT(), 1), ref mi);
            return mi.rcMonitor;
        }

        public static RECT MonitorOf(IntPtr h, bool work = false)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(MonitorFromWindow(h, 2), ref mi);
            return work ? mi.rcWork : mi.rcMonitor;
        }

        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        public static readonly IntPtr HWND_TOPMOST = new(-1);
        public const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_NOMOVE = 2, SWP_NOSIZE = 1;

        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);

        /// Rounded window shape so the blur doesn't show square corners.
        public static void SetRoundRegion(IntPtr h, int radiusPx)
        {
            if (!GetWindowRect(h, out var r)) return;
            SetWindowRgn(h, CreateRoundRectRgn(0, 0, r.Right - r.Left + 1, r.Bottom - r.Top + 1, radiusPx * 2, radiusPx * 2), true);
        }

        public static void ClearRegion(IntPtr h) => SetWindowRgn(h, IntPtr.Zero, true);

        // ---------------- top-level windows ----------------
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern IntPtr GetClassLongPtr(IntPtr h, int i);
        public const int SW_MINIMIZE = 6, SW_RESTORE = 9;
        public const uint WM_CLOSE = 0x10;

        public static string WindowTitle(IntPtr h)
        {
            int n = GetWindowTextLength(h);
            if (n <= 0) return "";
            var sb = new StringBuilder(n + 1);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string ClassName(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        static bool IsCloaked(IntPtr h) => DwmGetWindowAttribute(h, 14, out int c, 4) == 0 && c != 0;

        /// Windows that the regular taskbar would show.
        public static List<IntPtr> TaskbarWindows(IntPtr self)
        {
            var list = new List<IntPtr>();
            EnumWindows((h, l) =>
            {
                if (h == self || !IsWindowVisible(h) || IsCloaked(h)) return true;
                long ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
                bool owned = GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero;
                if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return true;
                if (owned && (ex & WS_EX_APPWINDOW) == 0) return true;
                if (GetWindowTextLength(h) == 0) return true;
                var cls = ClassName(h);
                if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
                list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static void Activate(IntPtr h)
        {
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            SetForegroundWindow(h);
        }

        public static ImageSource WindowIcon(IntPtr h)
        {
            IntPtr icon = IntPtr.Zero;
            SendMessageTimeout(h, 0x7F /* WM_GETICON */, (IntPtr)1 /* ICON_BIG */, IntPtr.Zero, 2, 80, out icon);
            if (icon == IntPtr.Zero) SendMessageTimeout(h, 0x7F, (IntPtr)2, IntPtr.Zero, 2, 80, out icon);
            if (icon == IntPtr.Zero) icon = GetClassLongPtr(h, -14);
            if (icon == IntPtr.Zero) icon = GetClassLongPtr(h, -34);
            if (icon == IntPtr.Zero) return null;
            try
            {
                var s = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                s.Freeze();
                return s;
            }
            catch { return null; }
        }

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);

        public static string ProcessPath(uint pid)
        {
            IntPtr hp = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (hp == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024); int n = sb.Capacity;
                return QueryFullProcessImageName(hp, 0, sb, ref n) ? sb.ToString() : null;
            }
            finally { CloseHandle(hp); }
        }

        // ---------------- DWM live thumbnails ----------------
        [StructLayout(LayoutKind.Sequential)]
        struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags; public RECT rcDestination; public RECT rcSource; public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }
        [StructLayout(LayoutKind.Sequential)] struct PSIZE { public int x, y; }
        [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES p);
        [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out PSIZE size);

        public static IntPtr RegisterThumb(IntPtr dest, IntPtr src) => DwmRegisterThumbnail(dest, src, out var t) == 0 ? t : IntPtr.Zero;

        public static Size ThumbSourceSize(IntPtr thumb) => DwmQueryThumbnailSourceSize(thumb, out var s) == 0 ? new Size(s.x, s.y) : Size.Empty;

        public static void PlaceThumb(IntPtr thumb, RECT dest)
        {
            var p = new DWM_THUMBNAIL_PROPERTIES { dwFlags = 0x1 | 0x4 | 0x8 | 0x10, rcDestination = dest, opacity = 255, fVisible = true, fSourceClientAreaOnly = false };
            DwmUpdateThumbnailProperties(thumb, ref p);
        }

        // ---------------- low-level mouse hook ----------------
        [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINTPUB pt; public uint mouseData, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct POINTPUB { public int X, Y; }
        public delegate IntPtr LowLevelMouseProc(int code, IntPtr msg, ref MSLLHOOKSTRUCT info);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr msg, ref MSLLHOOKSTRUCT info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);

        // ---------------- input ----------------
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public KEYBDINPUT ki; public long pad; }
        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);

        public static void PressKeys(params byte[] vks)
        {
            foreach (var k in vks) keybd_event(k, 0, 0, UIntPtr.Zero);
            for (int i = vks.Length - 1; i >= 0; i--) keybd_event(vks[i], 0, 2, UIntPtr.Zero);
        }

        public static void TypeText(string text)
        {
            var list = new List<INPUT>();
            foreach (var c in text)
            {
                list.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wScan = c, dwFlags = 4 } });
                list.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wScan = c, dwFlags = 4 | 2 } });
            }
            SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
        }

        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);

        // ---------------- performance counters ----------------
        [StructLayout(LayoutKind.Sequential)] public struct FILETIME { public uint Low, High; public ulong Value => ((ulong)High << 32) | Low; }
        [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        // ---------------- shell images ----------------
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
        [StructLayout(LayoutKind.Sequential)]
        struct DIBSECTION { public BITMAP dsBm; public BITMAPINFOHEADER dsBmih; public uint b0, b1, b2; public IntPtr dshSection; public uint dsOffset; }
        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int c, out DIBSECTION ds);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        public static BitmapSource ShellImage(string path, int size, bool iconOnly = true)
        {
            IShellItemImageFactory f = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f);
                if (f.GetImage(new SIZE { cx = size, cy = size }, iconOnly ? 0x4 : 0x0, out hbm) != 0 || hbm == IntPtr.Zero) return null;
                return ToSource(hbm);
            }
            catch { return null; }
            finally
            {
                if (hbm != IntPtr.Zero) DeleteObject(hbm);
                if (f != null) Marshal.ReleaseComObject(f);
            }
        }

        static BitmapSource ToSource(IntPtr hbm)
        {
            if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), out var ds) == 0 || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32)
            {
                var s = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                s.Freeze();
                return s;
            }
            int w = ds.dsBm.bmWidth, h = Math.Abs(ds.dsBm.bmHeight), stride = w * 4;
            var buf = new byte[stride * h];
            var bi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            IntPtr dc = GetDC(IntPtr.Zero);
            try { if (GetDIBits(dc, hbm, 0, (uint)h, buf, ref bi, 0) == 0) return null; }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            bool anyAlpha = false;
            for (int i = 3; i < buf.Length; i += 4) if (buf[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buf, stride);
            bmp.Freeze();
            return bmp;
        }
    }
}
