using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace GlassBar
{
    /// Low-level mouse/keyboard hooks run on their own thread with their own message loop.
    /// On the UI thread a busy moment (WebView2 starting, a big layout) makes the hook time out,
    /// and Windows then silently removes it: the dock stopped revealing and the Windows key stopped working.
    public partial class BarWindow
    {
        [StructLayout(LayoutKind.Sequential)] struct HMSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }
        [DllImport("user32.dll")] static extern int GetMessage(out HMSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);

        static uint StartHookThread(string name, Func<IntPtr> install, Action<IntPtr> remove)
        {
            uint id = 0;
            using var ready = new ManualResetEventSlim();
            var t = new Thread(() =>
            {
                id = GetCurrentThreadId();
                var h = install();
                ready.Set();
                while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
                if (h != IntPtr.Zero) remove(h);
            }) { IsBackground = true, Name = name, Priority = ThreadPriority.AboveNormal };
            t.Start();
            ready.Wait(2000);
            return id;
        }

        static void StopHookThread(uint id)
        {
            if (id != 0) PostThreadMessage(id, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
