using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace GlassBar
{
    /// Glass popup panel that opens above a card in the bar.
    public class Flyout : Window
    {
        public readonly DockPanel Body = new();
        readonly TextBlock titleText;
        readonly StackPanel headerButtons;
        public readonly StackPanel HeaderExtra;
        public FrameworkElement Anchor { get; private set; }
        public bool KeepOpen;   // e.g. while a dialog is up
        IntPtr hwnd;
        DateTime hiddenAt;

        public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

        public Flyout(string title, double width, double height)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Transparent;
            Width = width;
            Height = height;
            FontFamily = new FontFamily("Segoe UI");
            Foreground = Res("TextPrimary");
            UseLayoutRounding = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -20000; Top = -20000;
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });

            titleText = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            headerButtons = new StackPanel { Orientation = Orientation.Horizontal };
            HeaderExtra = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            var header = new DockPanel { Margin = new Thickness(18, 12, 10, 8), LastChildFill = true };
            DockPanel.SetDock(headerButtons, Dock.Right);
            header.Children.Add(headerButtons);
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(titleText);
            left.Children.Add(HeaderExtra);
            header.Children.Add(left);
            AddHeaderButton("", "Close", Hide);

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(Body);

            Content = new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(Color.FromArgb(0xEC, 0x0A, 0x13, 0x28)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = root
            };

            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                Native.MakeToolWindow(hwnd);
                Native.EnableBlur(hwnd);
            };
            SizeChanged += (s, e) => UpdateRegion();
            IsVisibleChanged += (s, e) => { if (!IsVisible) hiddenAt = DateTime.Now; };
            Deactivated += (s, e) => Dispatcher.BeginInvoke(() =>
            {
                if (KeepOpen || IsActive || !IsVisible) return;
                // stay open while the user types into the bar (terminal), hide when another app takes focus
                var fg = Native.GetForegroundWindow();
                if (BarWindow.Instance != null && fg == BarWindow.Instance.Hwnd && StaysOpenOverBar) return;
                Hide();
            }, DispatcherPriority.Background);
            PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Hide(); e.Handled = true; } };
        }

        /// When true the flyout doesn't close while the bar itself has focus.
        public bool StaysOpenOverBar { get; set; }

        public string Title2 { set => titleText.Text = value; }

        public Button AddHeaderButton(string glyph, string tip, Action click)
        {
            var b = new Button
            {
                Style = (Style)Application.Current.Resources["IconButton"],
                Content = glyph, ToolTip = tip, Width = 30, Height = 30, FontSize = 11
            };
            b.Click += (s, e) => click();
            headerButtons.Children.Insert(0, b);
            return b;
        }

        void UpdateRegion()
        {
            if (hwnd == IntPtr.Zero) return;
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Native.SetRoundRegion(hwnd, (int)Math.Round(16 * scale));
        }

        public void Toggle(FrameworkElement anchor)
        {
            if (IsVisible && Anchor == anchor) { Hide(); return; }
            // the click on the card already closed us via Deactivated: treat it as a toggle
            if (Anchor == anchor && (DateTime.Now - hiddenAt).TotalMilliseconds < 400) return;
            ShowAbove(anchor);
        }

        public void ShowAbove(FrameworkElement anchor)
        {
            Anchor = anchor;
            BarWindow.Instance?.CloseFlyoutsExcept(this);
            if (hwnd == IntPtr.Zero) { Show(); }
            var dpi = VisualTreeHelper.GetDpi(anchor);
            int w = (int)Math.Round(Width * dpi.DpiScaleX), h = (int)Math.Round(Height * dpi.DpiScaleY);
            var (x, y) = BarWindow.Instance.PopupPos(anchor, w, h, center: false);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_SHOWWINDOW);
            Show();
            Activate();
            UpdateRegion();
            OnShown();
        }

        protected virtual void OnShown() { }

        // ---------- small UI helpers shared by flyouts ----------
        public static TextBlock Glyph(string g, double size, Brush fg = null) => new()
        {
            Text = g, FontFamily = (FontFamily)Application.Current.Resources["Icons"], FontSize = size,
            Foreground = fg ?? Res("TextSecondary"), VerticalAlignment = VerticalAlignment.Center
        };

        public static Button Pill(string text, Action click, string glyph = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (glyph != null) { var g = Glyph(glyph, 11, Brushes.White); g.Margin = new Thickness(0, 0, 7, 0); sp.Children.Add(g); }
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Style = (Style)Application.Current.Resources["PillButton"], Content = sp, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (s, e) => click();
            return b;
        }

        public static Button SmallIcon(string glyph, string tip, Action click)
        {
            var b = new Button { Style = (Style)Application.Current.Resources["IconButton"], Content = glyph, ToolTip = tip, Width = 28, Height = 28, FontSize = 11 };
            b.Click += (s, e) => click();
            return b;
        }
    }
}
