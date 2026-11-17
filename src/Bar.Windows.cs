using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassBar
{
    public class WinItem : INotifyPropertyChanged
    {
        public IntPtr Hwnd;
        public string Title { get; set; }
        public ImageSource Icon { get; set; }
        public string Letter => Icon == null && Title?.Length > 0 ? Title.Substring(0, 1).ToUpperInvariant() : "";
        public Visibility LetterVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
        bool active;
        public bool IsActive { get => active; set { if (active == value) return; active = value; On(nameof(IsActive)); } }
        public void On(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// Open windows (what the regular taskbar shows) + live preview on hover.
    public partial class BarWindow
    {
        readonly ObservableCollection<WinItem> windows = new();
        IntPtr lastForeground;
        ThumbPopup thumb;
        DispatcherTimer hoverTimer;
        WinItem hoverItem;
        FrameworkElement hoverAnchor;

        void InitWindows()
        {
            WindowsList.ItemsSource = windows;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            t.Tick += (s, e) => RefreshWindows();
            t.Start();
            var fg = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            fg.Tick += (s, e) => TrackForeground();
            fg.Start();
            hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            hoverTimer.Tick += (s, e) => { hoverTimer.Stop(); ShowThumb(); };
            Dispatcher.BeginInvoke(RefreshWindows, DispatcherPriority.Loaded);
        }

        bool IsOwnWindow(IntPtr h)
        {
            if (h == Hwnd) return true;
            foreach (Window w in Application.Current.Windows)
                if (new WindowInteropHelper(w).Handle == h) return true;
            return false;
        }

        void TrackForeground()
        {
            var h = Native.GetForegroundWindow();
            if (h != IntPtr.Zero && !IsOwnWindow(h)) lastForeground = h;
            foreach (var w in windows) w.IsActive = w.Hwnd == lastForeground && !Native.IsIconic(w.Hwnd);
        }

        void RefreshWindows()
        {
            var current = Native.TaskbarWindows(Hwnd).Where(h => !IsOwnWindow(h)).ToList();
            var set = new HashSet<IntPtr>(current);
            for (int i = windows.Count - 1; i >= 0; i--)
                if (!set.Contains(windows[i].Hwnd)) windows.RemoveAt(i);
            var known = new HashSet<IntPtr>(windows.Select(w => w.Hwnd));
            foreach (var h in current)
            {
                if (known.Contains(h))
                {
                    var w = windows.First(x => x.Hwnd == h);
                    var title = Native.WindowTitle(h);
                    if (title != w.Title) { w.Title = title; w.On(nameof(WinItem.Title)); }
                    continue;
                }
                var item = new WinItem { Hwnd = h, Title = Native.WindowTitle(h) };
                item.Icon = IconFor(h);
                windows.Add(item);
            }
            TrackForeground();
            WindowsCard.Visibility = windows.Count > 0 || pins.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        static readonly Dictionary<string, ImageSource> exeIcons = new(StringComparer.OrdinalIgnoreCase);

        static ImageSource IconFor(IntPtr h)
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            var exe = Native.ProcessPath(pid);
            if (exe != null && !exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (!exeIcons.TryGetValue(exe, out var img)) exeIcons[exe] = img = Native.ShellImage(exe, 48);
                if (img != null) return img;
            }
            return Native.WindowIcon(h);
        }

        static WinItem ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as WinItem;

        void Window_Click(object s, RoutedEventArgs e)
        {
            HideThumb();
            if (ItemOf(s) is not WinItem w) return;
            if (w.Hwnd == lastForeground && !Native.IsIconic(w.Hwnd))
            {
                Native.ShowWindow(w.Hwnd, Native.SW_MINIMIZE);
                lastForeground = IntPtr.Zero;
            }
            else Native.Activate(w.Hwnd);
        }

        void Window_MiddleClick(object s, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle || ItemOf(s) is not WinItem w) return;
            e.Handled = true;
            HideThumb();
            Native.PostMessage(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        void Window_RightClick(object s, MouseButtonEventArgs e)
        {
            if (ItemOf(s) is not WinItem w) return;
            e.Handled = true;
            HideThumb();
            var m = new ContextMenu();
            m.Items.Add(MI("Bring to front", () => Native.Activate(w.Hwnd)));
            m.Items.Add(MI("Minimize", () => Native.ShowWindow(w.Hwnd, Native.SW_MINIMIZE)));
            m.Items.Add(new Separator());
            m.Items.Add(MI("Close window", () => Native.PostMessage(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero)));
            m.PlacementTarget = (UIElement)s;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            m.IsOpen = true;
        }

        void Window_Enter(object s, MouseEventArgs e)
        {
            hoverItem = ItemOf(s);
            hoverAnchor = s as FrameworkElement;
            if (thumb != null && thumb.IsVisible) ShowThumb();
            else { hoverTimer.Stop(); hoverTimer.Start(); }
        }

        void Window_Leave(object s, MouseEventArgs e)
        {
            hoverTimer.Stop();
            hoverItem = null;
            Dispatcher.BeginInvoke(() => { if (hoverItem == null) HideThumb(); }, DispatcherPriority.Background);
        }

        void ShowThumb()
        {
            if (hoverItem == null || hoverAnchor == null || !Native.IsWindow(hoverItem.Hwnd)) return;
            thumb ??= new ThumbPopup();
            thumb.ShowFor(hoverItem, hoverAnchor, Hwnd);
        }

        void HideThumb() => thumb?.HideThumb();
    }

    /// Small glass popup that shows a live DWM thumbnail of a window.
    class ThumbPopup : Window
    {
        readonly TextBlock title = new() { FontSize = 12, Margin = new Thickness(12, 8, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        readonly Border host = new() { Margin = new Thickness(10, 6, 10, 10) };
        IntPtr hwnd, thumbHandle;

        public ThumbPopup()
        {
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
            ShowActivated = false; Background = Brushes.Transparent; Width = 280; Height = 200;
            Foreground = Flyout.Res("TextPrimary"); FontFamily = new FontFamily("Segoe UI");
            Left = -20000; Top = -20000;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(0) });
            var dock = new DockPanel();
            DockPanel.SetDock(title, Dock.Top);
            dock.Children.Add(title);
            dock.Children.Add(host);
            Content = new Border
            {
                CornerRadius = new CornerRadius(14), Child = dock, BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x0C, 0x16, 0x2E)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255))
            };
            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                Native.MakeToolWindow(hwnd, noActivate: true);
                Native.EnableBlur(hwnd);
            };
        }

        public void ShowFor(WinItem item, FrameworkElement anchor, IntPtr bar)
        {
            title.Text = item.Title;
            if (!IsVisible) Show();
            if (thumbHandle != IntPtr.Zero) { Native.DwmUnregisterThumbnail(thumbHandle); thumbHandle = IntPtr.Zero; }
            thumbHandle = Native.RegisterThumb(hwnd, item.Hwnd);

            var dpi = VisualTreeHelper.GetDpi(anchor);
            double sx = dpi.DpiScaleX;
            // fit the source aspect ratio into a max 260x150 area
            var src = Native.ThumbSourceSize(thumbHandle);
            double maxW = 260, maxH = 150, tw = maxW, th = maxH;
            if (src.Width > 0 && src.Height > 0)
            {
                double k = Math.Min(maxW / src.Width, maxH / src.Height);
                tw = Math.Max(120, src.Width * k); th = src.Height * k;
            }
            double w = tw + 20, h = th + 16 + 30;
            Width = w; Height = h;

            int pw = (int)(w * sx), ph = (int)(h * sx);
            var (x, y) = BarWindow.Instance.PopupPos(anchor, pw, ph, center: true);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, pw, ph, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            Native.SetRoundRegion(hwnd, (int)(14 * sx));
            if (thumbHandle != IntPtr.Zero)
            {
                int left = (int)(10 * sx), top = (int)(36 * sx);
                Native.PlaceThumb(thumbHandle, new Native.RECT { Left = left, Top = top, Right = left + (int)(tw * sx), Bottom = top + (int)(th * sx) });
            }
        }

        public void HideThumb()
        {
            if (thumbHandle != IntPtr.Zero) { Native.DwmUnregisterThumbnail(thumbHandle); thumbHandle = IntPtr.Zero; }
            if (IsVisible) Hide();
        }
    }
}
