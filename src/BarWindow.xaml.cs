using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GlassBar
{
    public class BarSettings
    {
        public bool ReplaceTaskbar { get; set; }
        public bool? OriginalAutoHide { get; set; }
        public bool AskedTaskbar { get; set; }
        public string RecordMic { get; set; } = "";
        public int RecordFps { get; set; } = 30;
        public int Edge { get; set; } = 3;          // 0 left, 1 top, 2 right, 3 bottom
        public double Scale { get; set; } = 1.0;
        public bool LocalSendAutoAccept { get; set; }
        public bool GlassStart { get; set; } = true;
        public string OmenMonitorPath { get; set; }
        public List<string> StartPins { get; set; }

        static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassBar");
        static string FilePath => Path.Combine(Dir, "settings.json");
        public static string DataDir { get { Directory.CreateDirectory(Dir); return Dir; } }

        public static BarSettings Load()
        {
            try { return JsonSerializer.Deserialize<BarSettings>(File.ReadAllText(FilePath)) ?? new BarSettings(); }
            catch { return new BarSettings(); }
        }

        public void Save()
        {
            try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
            catch { }
        }
    }

    public partial class BarWindow : Window
    {
        public static BarWindow Instance { get; private set; }
        public IntPtr Hwnd { get; private set; }
        public readonly BarSettings Settings = BarSettings.Load();
        readonly string snapshotPath;
        readonly List<Flyout> flyouts = new();
        uint appBarMsg;
        bool appBarOn;

        public BarWindow(string snapshot)
        {
            Instance = this;
            snapshotPath = snapshot;
            InitializeComponent();

            SourceInitialized += OnSourceInitialized;
            Closed += (s, e) => Shutdown();
            StripScroll.ScrollChanged += (s, e) => UpdateFades();
            SearchBox.TextChanged += (s, e) => SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            TermBox.TextChanged += (s, e) => TermHint.Visibility = TermBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Root.MouseRightButtonUp += (s, e) => { if (IsEmptySpot(e.OriginalSource)) Menu_Click(null, null); };
            if (snapshot != null && int.TryParse(Environment.GetEnvironmentVariable("GB_EDGE"), out int testEdge)) Settings.Edge = testEdge;
            InitLayout();

            var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clock.Tick += (s, e) => UpdateClock();
            clock.Start();
            UpdateClock();

            var glassExe = GlassExplorerExe;
            if (File.Exists(glassExe) && Native.ShellImage(glassExe, 48) is ImageSource ico)
                ExplorerBtn.Content = new Image { Source = ico, Width = 30, Height = 30 };

            InitPins();
            InitWindows();
            InitTray();
            InitBattery();
            InitMedia();
            InitVolume();
            InitVolumeSound();
            InitPerf();
            InitOmen();
            InitRecent();
            InitRecorder();
            InitStart();

            Loaded += async (s, e) =>
            {
                UpdateFades();
                if (snapshotPath != null)
                {
                    await Task.Delay(2500);
                    await SnapshotFlyouts();
                    if (Environment.GetEnvironmentVariable("GB_SCROLL") != null) { StripScrollTo(StripScrollable); UpdateLayout(); await Task.Delay(300); }
                    SaveSnapshot(this, (FrameworkElement)Content, snapshotPath);
                    Shutdown();
                    return;
                }
                await Task.Delay(3000);
                InitWeb();   // load chats/mail in the background so unread badges work
                InitLocalSend();
            };
        }

        void OnSourceInitialized(object s, EventArgs e)
        {
            Hwnd = new WindowInteropHelper(this).Handle;
            var src = HwndSource.FromHwnd(Hwnd);
            src.CompositionTarget.BackgroundColor = Colors.Transparent;
            Native.EnableBlur(Hwnd);
            Native.MakeToolWindow(Hwnd);
            src.AddHook(WndProc);

            if (snapshotPath != null)
            {
                Left = 0; Top = 0;
                Width = Vertical ? BarThickness : 1720;
                Height = Vertical ? 1000 : BarThickness;
                return;
            }

            appBarMsg = Native.RegisterWindowMessage("GlassBarAppBarMessage");
            Native.AppBarRegister(Hwnd, appBarMsg);
            appBarOn = true;
            if (Settings.ReplaceTaskbar) ApplyTaskbarReplace(true, save: false);
            Dock();
            RegisterHotkeys();
            InstallWheelHook();
            if (!Settings.ReplaceTaskbar && !Settings.AskedTaskbar) TaskbarAsk.Visibility = Visibility.Visible;
        }

        /// Reserve an edge of the primary monitor (like the real taskbar) and place the bar there.
        void Dock()
        {
            if (!appBarOn) return;
            var mon = Native.PrimaryMonitor();
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int px = (int)Math.Round(BarThickness * scale);
            var r = Native.AppBarSet(Hwnd, mon, (uint)Edge, px);
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (appBarOn && msg == appBarMsg)
            {
                switch ((int)w)
                {
                    case 1: Dispatcher.BeginInvoke(Dock); break;                // ABN_POSCHANGED
                    case 2:                                                      // ABN_FULLSCREENAPP
                        bool full = l != IntPtr.Zero;
                        Topmost = !full;
                        if (full) Native.SetWindowPos(Hwnd, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                        break;
                }
            }
            else if (msg == 0x007E /* WM_DISPLAYCHANGE */ || msg == 0x02E0 /* WM_DPICHANGED */)
                Dispatcher.BeginInvoke(Dock, DispatcherPriority.Background);
            else if (msg == 0x0312 /* WM_HOTKEY */)
            {
                if ((int)w == 1) Record_Click(null, null);
                handled = true;
            }
            return IntPtr.Zero;
        }

        void RegisterHotkeys()
        {
            // Ctrl+Alt+R = start/stop screen recording
            Native.RegisterHotKey(Hwnd, 1, 0x0002 | 0x0001 | 0x4000, 0x52);
        }

        public void Shutdown()
        {
            if (appBarOn) { Native.AppBarRemove(Hwnd); appBarOn = false; }
            Native.UnregisterHotKey(Hwnd, 1);
            if (wheelHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(wheelHook); wheelHook = IntPtr.Zero; }
            RemoveKeyHook();
            if (Settings.ReplaceTaskbar && Settings.OriginalAutoHide is bool orig) Native.TaskbarAutoHide = orig;
            StopRecordingForExit();
            Terminal?.Dispose();
            ls?.Dispose();
            Application.Current.Shutdown();
        }

        // ---------------------------------------------------------------
        // flyouts
        // ---------------------------------------------------------------
        public T Register<T>(T f) where T : Flyout { flyouts.Add(f); return f; }

        public void CloseFlyoutsExcept(Flyout keep)
        {
            foreach (var f in flyouts) if (f != keep && f.IsVisible) f.Hide();
        }

        // ---------------------------------------------------------------
        // sideways scrolling (smooth, frame-synced)
        // ---------------------------------------------------------------
        double scrollTarget;
        bool scrolling;
        TimeSpan lastFrame;

        // A low-level mouse hook catches the wheel over the bar no matter which window has focus,
        // and also the sideways swipe of a touchpad (WM_MOUSEHWHEEL), which WPF ignores.
        IntPtr wheelHook;
        Native.LowLevelMouseProc wheelProc;

        void InstallWheelHook()
        {
            wheelProc = WheelHook;
            wheelHook = Native.SetWindowsHookEx(14 /* WH_MOUSE_LL */, wheelProc, Native.GetModuleHandle(null), 0);
        }

        IntPtr WheelHook(int code, IntPtr msg, ref Native.MSLLHOOKSTRUCT info)
        {
            if (code >= 0 && ((int)msg == 0x020A || (int)msg == 0x020E))
            {
                Native.GetWindowRect(Hwnd, out var r);
                int x = info.pt.X, y = info.pt.Y;
                if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom && IsVisible)
                {
                    int delta = (short)(info.mouseData >> 16);
                    bool horizontal = (int)msg == 0x020E;
                    Dispatcher.BeginInvoke(() => OnWheel(new Point(x, y), delta, horizontal));
                    return (IntPtr)1;   // handled: don't scroll the window behind
                }
            }
            return Native.CallNextHookEx(wheelHook, code, msg, ref info);
        }

        void OnWheel(Point screenPx, int delta, bool horizontal)
        {
            // over the volume card the wheel changes the volume
            if (!horizontal && VolumeCard.IsVisible)
            {
                var tl = VolumeCard.PointToScreen(new Point(0, 0));
                var br = VolumeCard.PointToScreen(new Point(VolumeCard.ActualWidth, VolumeCard.ActualHeight));
                if (screenPx.X >= tl.X && screenPx.X <= br.X && screenPx.Y >= tl.Y && screenPx.Y <= br.Y)
                {
                    VolSlider.Value = Math.Clamp(VolSlider.Value + Math.Sign(delta) * 2, 0, 100);
                    VolumeBeep(250);
                    return;
                }
            }
            // wheel down / swipe right = move right
            ScrollBy(horizontal ? delta / 120.0 * 160 : -delta / 120.0 * 160);
        }

        public void ScrollBy(double px)
        {
            if (StripScrollable <= 0) return;
            if (!scrolling) scrollTarget = StripOffset;
            scrollTarget = Math.Clamp(scrollTarget + px, 0, StripScrollable);
            if (!scrolling)
            {
                scrolling = true;
                lastFrame = TimeSpan.Zero;
                CompositionTarget.Rendering += ScrollFrame;
            }
        }

        void ScrollLeft_Click(object s, RoutedEventArgs e) => ScrollBy(-StripViewport * 0.7);
        void ScrollRight_Click(object s, RoutedEventArgs e) => ScrollBy(StripViewport * 0.7);

        void ScrollFrame(object s, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            double dt = lastFrame == TimeSpan.Zero ? 1.0 / 165 : (now - lastFrame).TotalSeconds;
            if (dt <= 0) return;
            lastFrame = now;
            double cur = StripOffset, diff = scrollTarget - cur;
            if (Math.Abs(diff) < 0.5)
            {
                StripScrollTo(scrollTarget);
                scrolling = false;
                CompositionTarget.Rendering -= ScrollFrame;
                return;
            }
            StripScrollTo(cur + diff * (1 - Math.Exp(-dt * 20)));
        }

        void UpdateFades()
        {
            FadeLeft.Visibility = StripOffset > 1 ? Visibility.Visible : Visibility.Collapsed;
            FadeRight.Visibility = StripOffset < StripScrollable - 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------------------------------------------------------
        // clock, start, search, explorer
        // ---------------------------------------------------------------
        void UpdateClock()
        {
            var now = DateTime.Now;
            ClockTime.Text = now.ToString("HH:mm");
            ClockDate.Text = now.ToString("ddd d MMM");
        }

        static string GlassExplorerExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GlassExplorer", "GlassExplorer.exe");


        void Explorer_Click(object s, RoutedEventArgs e)
        {
            Launch(File.Exists(GlassExplorerExe) ? GlassExplorerExe : "explorer.exe");
        }

        async void SearchBox_KeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { SearchBox.Text = ""; return; }
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var text = SearchBox.Text.Trim();
            SearchBox.Text = "";
            if (text.Length == 0) return;
            var expanded = Environment.ExpandEnvironmentVariables(text.Trim('"'));
            bool looksDirect = Uri.TryCreate(expanded, UriKind.Absolute, out var uri) && (uri.IsFile || uri.Scheme is "http" or "https" or "mailto" or "ms-settings")
                               || Directory.Exists(expanded) || File.Exists(expanded)
                               || expanded.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
            if (looksDirect)
            {
                Launch(expanded.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + expanded : expanded);
                return;
            }
            // anything else: hand it to Windows search in the Start menu
            Native.PressKeys(0x5B);
            await Task.Delay(450);
            Native.TypeText(text);
        }

        public void Launch(string file, string args = null, string dir = null)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { UseShellExecute = true };
                if (args != null) psi.Arguments = args;
                if (dir != null) psi.WorkingDirectory = dir;
                Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
            catch (Exception ex) { ShowToast("Couldn't open: " + ex.Message); }
        }

        // ---------------------------------------------------------------
        // settings menu
        // ---------------------------------------------------------------
        static MenuItem MI(string header, Action a, bool check = false, bool enabled = true)
        {
            var m = new MenuItem { Header = header, IsChecked = check, IsEnabled = enabled };
            m.Click += (s, e) => a();
            return m;
        }

        void Menu_Click(object s, RoutedEventArgs e)
        {
            var m = new ContextMenu();
            m.Items.Add(MI("Replace Windows taskbar (auto-hide it)", () => ApplyTaskbarReplace(!Settings.ReplaceTaskbar), Settings.ReplaceTaskbar));
            m.Items.Add(MI("Start with Windows", ToggleAutostart, AutostartOn));
            m.Items.Add(MI("Windows key opens the glass Start menu", ToggleGlassStart, Settings.GlassStart));
            m.Items.Add(PositionMenu());
            m.Items.Add(SizeMenu());
            m.Items.Add(new Separator());
            m.Items.Add(MI("Sign out of chats and mail", ClearWebData));
            m.Items.Add(MI("Open recordings folder", () => Launch(RecordingsDir)));
            m.Items.Add(new Separator());
            m.Items.Add(MI("Exit bar", Shutdown));
            m.PlacementTarget = s as UIElement ?? this;
            m.Placement = s == null ? System.Windows.Controls.Primitives.PlacementMode.MousePoint
                        : Edge switch { 0 => System.Windows.Controls.Primitives.PlacementMode.Right, 1 => System.Windows.Controls.Primitives.PlacementMode.Bottom,
                                        2 => System.Windows.Controls.Primitives.PlacementMode.Left, _ => System.Windows.Controls.Primitives.PlacementMode.Top };
            m.IsOpen = true;
        }

        void ApplyTaskbarReplace(bool on, bool save = true)
        {
            try
            {
                if (on)
                {
                    if (save || Settings.OriginalAutoHide == null) Settings.OriginalAutoHide ??= Native.TaskbarAutoHide;
                    Native.TaskbarAutoHide = true;
                }
                else if (Settings.OriginalAutoHide is bool orig)
                {
                    Native.TaskbarAutoHide = orig;
                }
                if (save)
                {
                    Settings.ReplaceTaskbar = on;
                    if (!on) Settings.OriginalAutoHide = null;
                    Settings.Save();
                    ShowToast(on ? "Windows taskbar hidden – touch the very bottom edge to bring it back (tray icons)." : "Windows taskbar restored");
                }
                Dispatcher.BeginInvoke(Dock, DispatcherPriority.Background);
            }
            catch (Exception ex) { ShowToast(ex.Message); }
        }

        void AskHide_Click(object s, RoutedEventArgs e)
        {
            TaskbarAsk.Visibility = Visibility.Collapsed;
            Settings.AskedTaskbar = true;
            ApplyTaskbarReplace(true);
        }

        void AskKeep_Click(object s, RoutedEventArgs e)
        {
            TaskbarAsk.Visibility = Visibility.Collapsed;
            Settings.AskedTaskbar = true;
            Settings.Save();
            ShowToast("Kept. You can hide it later from the ⋯ menu.");
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static bool AutostartOn
        {
            get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("ModernGlassBar") != null; }
        }

        void ToggleAutostart()
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (AutostartOn) k.DeleteValue("ModernGlassBar", false);
            else k.SetValue("ModernGlassBar", $"\"{Environment.ProcessPath}\"");
            ShowToast(AutostartOn ? "The bar starts with Windows" : "The bar won't start with Windows");
        }

        // ---------------------------------------------------------------
        // toast
        // ---------------------------------------------------------------
        DispatcherTimer toastTimer;
        public void ShowToast(string text, double seconds = 4)
        {
            ToastText.Text = text;
            Toast.Visibility = Visibility.Visible;
            toastTimer ??= new DispatcherTimer();
            toastTimer.Stop();
            toastTimer.Interval = TimeSpan.FromSeconds(seconds);
            toastTimer.Tick -= HideToast;
            toastTimer.Tick += HideToast;
            toastTimer.Start();
        }
        void HideToast(object s, EventArgs e) { toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; }

        // ---------------------------------------------------------------
        // helpers
        // ---------------------------------------------------------------
        public static T FindAncestor<T>(object o) where T : DependencyObject
        {
            var d = o as DependencyObject;
            while (d != null && d is not T) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as T;
        }

        /// Dev only: render a window to PNG (--snapshot).
        public static void SaveSnapshot(Window w, FrameworkElement el, string file)
        {
            double width = el.ActualWidth, height = el.ActualHeight;
            var dpi = VisualTreeHelper.GetDpi(w);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(40, 70, 110), Color.FromRgb(20, 34, 60), 90), null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, width, height));
            }
            var rtb = new RenderTargetBitmap((int)(width * dpi.DpiScaleX), (int)(height * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(file);
            enc.Save(fs);
        }
    }
}
