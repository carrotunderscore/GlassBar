using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GlassBar
{
    public class TrayItem
    {
        public Tray.Icon Data;
        public ImageSource Icon { get; set; }
        public string Tip { get; set; }
    }

    public class PinItem
    {
        public string Path;
        public ImageSource Icon { get; set; }
        public string Name { get; set; }
    }

    /// Battery, tray icons (notification area of the hidden Windows taskbar) and pinned taskbar apps.
    public partial class BarWindow
    {
        // ---------------------------------------------------------------
        // battery
        // ---------------------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

        void InitBattery()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            t.Tick += (s, e) => UpdateBattery();
            t.Start();
            UpdateBattery();
        }

        void UpdateBattery()
        {
            if (!GetSystemPowerStatus(out var p) || (p.BatteryFlag & 128) != 0 || p.BatteryLifePercent > 100)
            {
                BatteryBox.Visibility = Visibility.Collapsed;   // desktop PC: no battery
                return;
            }
            BatteryBox.Visibility = Visibility.Visible;
            int pct = p.BatteryLifePercent;
            bool charging = p.ACLineStatus == 1;
            BatteryPct.Text = pct + "%";
            BatteryFill.Width = Math.Max(1.5, 20 * pct / 100.0);
            BatteryFill.Background = new SolidColorBrush(
                charging ? Color.FromRgb(0x5B, 0xD6, 0x8A)
                : pct <= 10 ? Color.FromRgb(0xFF, 0x4D, 0x5E)
                : pct <= 20 ? Color.FromRgb(0xF5, 0xC4, 0x51)
                : Color.FromRgb(0xEE, 0xF3, 0xFB));
            BatteryBolt.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
            string tip = $"Battery {pct}%";
            if (charging) tip += pct >= 100 ? " – fully charged" : " – charging";
            else if (p.BatteryLifeTime > 0) tip += $" – about {TimeSpan.FromSeconds(p.BatteryLifeTime):h\\:mm} left";
            if ((p.SystemStatusFlag & 1) != 0) tip += " (battery saver on)";
            BatteryBox.ToolTip = tip;
        }

        void Battery_Click(object s, RoutedEventArgs e) => Launch("ms-settings:batterysaver");

        // ---------------------------------------------------------------
        // tray icons
        // ---------------------------------------------------------------
        readonly ObservableCollection<TrayItem> trayItems = new();

        void InitTray()
        {
            TrayList.ItemsSource = trayItems;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (s, e) => RefreshTray();
            t.Start();
            Dispatcher.BeginInvoke(RefreshTray, DispatcherPriority.Background);
        }

        void RefreshTray()
        {
            List<Tray.Icon> icons;
            try { icons = Tray.Read(); } catch { icons = new(); }
            // rebuild only when something changed (icons, tooltips)
            var sig = string.Join("|", icons.Select(i => $"{i.Hwnd}:{i.Id}:{i.HIcon}:{i.Tip}"));
            if (sig == traySig) return;
            traySig = sig;
            trayItems.Clear();
            foreach (var i in icons)
            {
                ImageSource img = null;
                try
                {
                    if (i.HIcon != IntPtr.Zero)
                    {
                        img = Imaging.CreateBitmapSourceFromHIcon(i.HIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        img.Freeze();
                    }
                }
                catch { }
                if (img == null) continue;
                trayItems.Add(new TrayItem { Data = i, Icon = img, Tip = string.IsNullOrWhiteSpace(i.Tip) ? null : i.Tip });
            }
        }
        string traySig;

        static TrayItem TrayOf(object s) => (s as FrameworkElement)?.DataContext as TrayItem;

        bool skipTrayUp;

        void Tray_MouseUp(object s, MouseButtonEventArgs e)
        {
            if (TrayOf(s) is not TrayItem t) return;
            e.Handled = true;
            if (skipTrayUp) { skipTrayUp = false; return; }
            if (e.ChangedButton == MouseButton.Right) Tray.Click(t.Data, right: true, dbl: false);
            else if (e.ChangedButton == MouseButton.Left) Tray.Click(t.Data, right: false, dbl: false);
            else if (e.ChangedButton == MouseButton.Middle) Tray.Click(t.Data, right: false, dbl: true);
        }

        void Tray_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (TrayOf(s) is not TrayItem t || e.ChangedButton != MouseButton.Left || e.ClickCount != 2) return;
            e.Handled = true;
            skipTrayUp = true;
            Tray.Click(t.Data, right: false, dbl: true);
        }

        void TrayMore_Click(object s, RoutedEventArgs e) => Native.PressKeys(0x5B, 0x42);   // Win+B: focus the real notification area

        // ---------------------------------------------------------------
        // pinned taskbar apps
        // ---------------------------------------------------------------
        readonly ObservableCollection<PinItem> pins = new();
        FileSystemWatcher pinWatch;

        static string PinDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

        void InitPins()
        {
            PinList.ItemsSource = pins;
            LoadPins();
            try
            {
                if (Directory.Exists(PinDir))
                {
                    pinWatch = new FileSystemWatcher(PinDir) { EnableRaisingEvents = true };
                    FileSystemEventHandler h = (s, e) => Dispatcher.BeginInvoke(LoadPins, DispatcherPriority.Background);
                    pinWatch.Created += h; pinWatch.Deleted += h; pinWatch.Renamed += (s, e) => Dispatcher.BeginInvoke(LoadPins);
                }
            }
            catch { }
        }

        void LoadPins()
        {
            pins.Clear();
            try
            {
                if (!Directory.Exists(PinDir)) return;
                foreach (var f in new DirectoryInfo(PinDir).GetFiles("*.lnk").OrderBy(f => f.CreationTime))
                    pins.Add(new PinItem { Path = f.FullName, Name = Path.GetFileNameWithoutExtension(f.Name), Icon = Native.ShellImage(f.FullName, 48) });
            }
            catch { }
            PinList.Visibility = pins.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PinSep.Visibility = pins.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Pin_Click(object s, RoutedEventArgs e)
        {
            if ((s as FrameworkElement)?.DataContext is PinItem p) Launch(p.Path);
        }

        void Pin_RightClick(object s, MouseButtonEventArgs e)
        {
            if ((s as FrameworkElement)?.DataContext is not PinItem p) return;
            e.Handled = true;
            var m = new ContextMenu();
            m.Items.Add(MI("Open", () => Launch(p.Path)));
            m.Items.Add(MI("Run as administrator", () =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p.Path) { UseShellExecute = true, Verb = "runas" }); } catch { }
            }));
            m.Items.Add(MI("Open file location", () => Launch("explorer.exe", $"/select,\"{p.Path}\"")));
            m.PlacementTarget = (UIElement)s;
            m.IsOpen = true;
        }
    }

    /// Reads the notification-area icons out of explorer's tray toolbars and forwards clicks to their apps.
    public static class Tray
    {
        public record Icon(IntPtr Hwnd, uint Id, uint CallbackMsg, IntPtr HIcon, string Tip);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr addr, IntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr p, IntPtr addr, IntPtr size, uint type);
        [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr p, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);

        const uint TB_GETBUTTON = 0x417, TB_BUTTONCOUNT = 0x418, TB_GETBUTTONTEXTW = 0x44B;

        public static List<Icon> Read()
        {
            var list = new List<Icon>();
            var tray = FindWindow("Shell_TrayWnd", null);
            var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            var pager = FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
            ReadToolbar(FindWindowEx(pager, IntPtr.Zero, "ToolbarWindow32", null), list);
            var overflow = FindWindow("NotifyIconOverflowWindow", null);
            ReadToolbar(FindWindowEx(overflow, IntPtr.Zero, "ToolbarWindow32", null), list);
            return list;
        }

        static void ReadToolbar(IntPtr tb, List<Icon> list)
        {
            if (tb == IntPtr.Zero) return;
            int count = (int)SendMessage(tb, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
            if (count <= 0) return;
            GetWindowThreadProcessId(tb, out uint pid);
            var proc = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x0400, false, pid);
            if (proc == IntPtr.Zero) return;
            var mem = VirtualAllocEx(proc, IntPtr.Zero, (IntPtr)4096, 0x1000, 0x04);
            try
            {
                if (mem == IntPtr.Zero) return;
                var btn = new byte[32];
                var data = new byte[32];
                for (int i = 0; i < count; i++)
                {
                    if (SendMessage(tb, TB_GETBUTTON, (IntPtr)i, mem) == IntPtr.Zero) continue;
                    if (!ReadProcessMemory(proc, mem, btn, (IntPtr)32, out _)) continue;
                    // TBBUTTON (x64): iBitmap, idCommand, fsState, fsStyle, 6 bytes pad, dwData, iString
                    int idCommand = BitConverter.ToInt32(btn, 4);
                    byte state = btn[8];
                    if ((state & 0x08) != 0) continue;   // TBSTATE_HIDDEN
                    var dwData = (IntPtr)BitConverter.ToInt64(btn, 16);
                    if (dwData == IntPtr.Zero || !ReadProcessMemory(proc, dwData, data, (IntPtr)32, out _)) continue;
                    // TRAYDATA: hwnd, uID, uCallbackMessage, reserved[2], hIcon
                    var hwnd = (IntPtr)BitConverter.ToInt64(data, 0);
                    uint id = BitConverter.ToUInt32(data, 8);
                    uint cb = BitConverter.ToUInt32(data, 12);
                    var hIcon = (IntPtr)BitConverter.ToInt64(data, 24);
                    if (!IsWindow(hwnd)) continue;

                    string tip = null;
                    int len = (int)SendMessage(tb, TB_GETBUTTONTEXTW, (IntPtr)idCommand, mem);
                    if (len > 0 && len < 2000)
                    {
                        var tbuf = new byte[(len + 1) * 2];
                        if (ReadProcessMemory(proc, mem, tbuf, (IntPtr)tbuf.Length, out _))
                            tip = System.Text.Encoding.Unicode.GetString(tbuf, 0, len * 2);
                    }
                    list.Add(new Icon(hwnd, id, cb, hIcon, tip));
                }
            }
            finally
            {
                if (mem != IntPtr.Zero) VirtualFreeEx(proc, mem, IntPtr.Zero, 0x8000);
                CloseHandle(proc);
            }
        }

        /// Sends the same mouse messages the real notification area would send to the icon's app.
        public static void Click(Icon t, bool right, bool dbl)
        {
            GetWindowThreadProcessId(t.Hwnd, out uint pid);
            AllowSetForegroundWindow((int)pid);
            void Post(uint m) => Native.PostMessage(t.Hwnd, t.CallbackMsg, (IntPtr)t.Id, (IntPtr)m);
            if (right)
            {
                Post(0x204); Post(0x205);   // WM_RBUTTONDOWN/UP
                Post(0x7B);                 // WM_CONTEXTMENU (apps on NOTIFYICON_VERSION_4)
            }
            else if (dbl)
            {
                Post(0x203); Post(0x202);   // WM_LBUTTONDBLCLK + up
            }
            else
            {
                Post(0x201); Post(0x202);   // WM_LBUTTONDOWN/UP
                Post(0x400);                // NIN_SELECT
            }
        }
    }
}
