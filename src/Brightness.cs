using System;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace GlassBar
{
    /// Screen brightness: the built-in laptop panel through WMI, external monitors through DDC/CI.
    /// All calls are slow-ish (10–80 ms), so they run on a worker thread and only the latest value is sent.
    public static class Brightness
    {
        enum Kind { Unknown, None, Wmi, Ddc }
        static Kind kind = Kind.Unknown;
        static readonly object gate = new();
        static int pending = -1;
        static bool worker;

        /// Current brightness 0–100, or null if this screen can't be dimmed from Windows.
        public static int? Get()
        {
            lock (gate)
            {
                if (kind is Kind.Unknown or Kind.Wmi)
                {
                    var v = WmiGet();
                    if (v != null) { kind = Kind.Wmi; return v; }
                    if (kind == Kind.Wmi) return null;
                }
                if (kind is Kind.Unknown or Kind.Ddc)
                {
                    var v = DdcGet();
                    if (v != null) { kind = Kind.Ddc; return v; }
                }
                if (kind == Kind.Unknown) kind = Kind.None;
                return null;
            }
        }

        public static void Set(int value)
        {
            value = Math.Clamp(value, 0, 100);
            lock (gate)
            {
                pending = value;
                if (worker) return;
                worker = true;
            }
            new Thread(() =>
            {
                while (true)
                {
                    int v;
                    lock (gate)
                    {
                        if (pending < 0) { worker = false; return; }
                        v = pending; pending = -1;
                    }
                    try { if (kind == Kind.Wmi) WmiSet(v); else if (kind == Kind.Ddc) DdcSet(v); } catch { }
                }
            }) { IsBackground = true, Name = "brightness" }.Start();
        }

        // ---------------- WMI (laptop panel) ----------------
        static int? WmiGet()
        {
            try
            {
                using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
                foreach (ManagementObject o in s.Get()) using (o) return Convert.ToInt32(o["CurrentBrightness"]);
            }
            catch { }
            return null;
        }

        static void WmiSet(int v)
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE");
            foreach (ManagementObject o in s.Get())
                using (o) o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)v });
        }

        // ---------------- DDC/CI (external monitor) ----------------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR { public IntPtr h; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string desc; }
        [StructLayout(LayoutKind.Sequential)] struct PT { public int x, y; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(PT pt, uint flags);
        [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr mon, out uint n);
        [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr mon, uint n, [Out] PHYSICAL_MONITOR[] arr);
        [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint n, PHYSICAL_MONITOR[] arr);
        [DllImport("dxva2.dll")] static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
        [DllImport("dxva2.dll")] static extern bool SetMonitorBrightness(IntPtr h, uint v);

        static T WithMonitors<T>(Func<PHYSICAL_MONITOR[], T> f)
        {
            var mon = MonitorFromPoint(new PT(), 1 /* MONITOR_DEFAULTTOPRIMARY */);
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(mon, out uint n) || n == 0) return default;
            var arr = new PHYSICAL_MONITOR[n];
            if (!GetPhysicalMonitorsFromHMONITOR(mon, n, arr)) return default;
            try { return f(arr); }
            finally { DestroyPhysicalMonitors(n, arr); }
        }

        static int? DdcGet()
        {
            try
            {
                return WithMonitors<int?>(arr =>
                {
                    foreach (var m in arr)
                        if (GetMonitorBrightness(m.h, out var min, out var cur, out var max) && max > min)
                            return (int)Math.Round(100.0 * (cur - min) / (max - min));
                    return null;
                });
            }
            catch { return null; }
        }

        static void DdcSet(int v)
        {
            WithMonitors(arr =>
            {
                foreach (var m in arr)
                    if (GetMonitorBrightness(m.h, out var min, out _, out var max) && max > min)
                        SetMonitorBrightness(m.h, (uint)Math.Round(min + (max - min) * v / 100.0));
                return true;
            });
        }
    }
}
