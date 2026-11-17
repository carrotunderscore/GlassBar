using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GlassBar
{
    /// The glass Start menu, opened from the Start button or the Windows key.
    public partial class BarWindow
    {
        StartFlyout startFly;

        public IReadOnlyList<RecentFile> RecentFiles => recent;
        public static string GlassExplorerPath => GlassExplorerExe;

        void InitStart()
        {
            StartApps.LoadCache();
            _ = StartApps.Refresh(force: true);
            if (snapshotPath == null) InstallKeyHook();
        }

        void Start_Click(object s, RoutedEventArgs e) => ToggleStart();

        public void ToggleStart()
        {
            startFly ??= Register(new StartFlyout());
            if (startFly.IsVisible) { startFly.Hide(); return; }
            startFly.ShowAbove(StartBtn);
            ForceForeground(new WindowInteropHelper(startFly).Handle);
        }

        void ToggleGlassStart()
        {
            Settings.GlassStart = !Settings.GlassStart;
            Settings.Save();
            ShowToast(Settings.GlassStart ? "The Windows key opens the glass Start menu" : "The Windows key opens the normal Windows Start menu");
        }

        // ---------------------------------------------------------------
        // Windows key: a lone press (no Win+E etc.) opens our menu instead of Windows Start
        // ---------------------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
        delegate IntPtr KeyboardProc(int code, IntPtr msg, ref KBDLLHOOKSTRUCT k);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, KeyboardProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr msg, ref KBDLLHOOKSTRUCT k);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        IntPtr keyHook;
        KeyboardProc keyProc;
        bool winDown, winUsed;
        uint winVk;

        void InstallKeyHook()
        {
            keyProc = KeyHook;
            keyThread = StartHookThread("GlassBar key hook",
                () => keyHook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, keyProc, Native.GetModuleHandle(null), 0),
                h => Native.UnhookWindowsHookEx(h));
        }
        uint keyThread;

        void RemoveKeyHook()
        {
            StopHookThread(keyThread); keyThread = 0; keyHook = IntPtr.Zero;
        }

        IntPtr KeyHook(int code, IntPtr msg, ref KBDLLHOOKSTRUCT k)
        {
            const uint INJECTED = 0x10;
            if (code >= 0 && Settings.GlassStart && (k.flags & INJECTED) == 0)
            {
                int m = (int)msg;
                bool down = m == 0x100 || m == 0x104, up = m == 0x101 || m == 0x105;
                bool isWin = k.vkCode == 0x5B || k.vkCode == 0x5C;
                if (isWin && down)
                {
                    if (!winDown) { winDown = true; winUsed = false; winVk = k.vkCode; }
                }
                else if (isWin && up && winDown)
                {
                    winDown = false;
                    if (!winUsed)
                    {
                        // Swallow this key-up and send "mask key, then Win up" ourselves: Windows then sees
                        // Win as part of a shortcut and doesn't open its Start menu.
                        keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                        keybd_event(0xE8, 0, 2, UIntPtr.Zero);
                        keybd_event((byte)winVk, 0, 2, UIntPtr.Zero);
                        Dispatcher.BeginInvoke(WinKeyPress);
                        return (IntPtr)1;
                    }
                }
                else if (winDown && down) winUsed = true;   // Win+E, Win+D, ... stay as they are
            }
            return CallNextHookEx(keyHook, code, msg, ref k);
        }

        // ---------------------------------------------------------------
        // focus: a window opened from a key press may not get the foreground on its own
        // ---------------------------------------------------------------
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);

        static void ForceForeground(IntPtr h)
        {
            if (h == IntPtr.Zero || GetForegroundWindow() == h) return;
            uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
            bool attached = fg != me && AttachThreadInput(me, fg, true);
            SetForegroundWindow(h);
            BringWindowToTop(h);
            if (attached) AttachThreadInput(me, fg, false);
        }
    }

    public class StartFlyout : Flyout
    {
        readonly StartPanel panel;

        public StartFlyout() : base("Start", 640, 740)
        {
            panel = new StartPanel(this);
            Body.Children.Add(panel);
            Title2 = Greeting();
        }

        static string Greeting()
        {
            int h = DateTime.Now.Hour;
            var part = h < 5 ? "Good night" : h < 12 ? "Good morning" : h < 18 ? "Good afternoon" : "Good evening";
            return $"{part}, {Environment.UserName}";
        }

        protected override void OnShown()
        {
            Title2 = Greeting();
            panel.Opened();
        }
    }
}
