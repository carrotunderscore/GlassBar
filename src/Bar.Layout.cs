using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace GlassBar
{
    /// Bar size (scale) and which screen edge it sits on (bottom, top, left, right).
    public partial class BarWindow
    {
        [StructLayout(LayoutKind.Sequential)] struct CPOINT { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out CPOINT p);

        /// 0 = left, 1 = top, 2 = right, 3 = bottom (same numbers as the Windows ABE_* edges)
        public int Edge => Settings.Edge is >= 0 and <= 3 ? Settings.Edge : 3;
        public bool Vertical => Edge is 0 or 2;
        const double HorizontalBase = 96, VerticalBase = 320;
        const double MinScale = 0.6, MaxScale = 1.8;
        double BarThickness => (Vertical ? VerticalBase : HorizontalBase) * Settings.Scale;

        readonly Dictionary<FrameworkElement, double> designWidth = new();
        readonly ScaleTransform zoom = new();

        static readonly Thickness H8 = new(0, 0, 8, 0), V8 = new(0, 0, 0, 8);

        void InitLayout()
        {
            Settings.Scale = Math.Clamp(Settings.Scale <= 0 ? 1 : Settings.Scale, MinScale, MaxScale);
            Root.LayoutTransform = zoom;
            ApplyLayout();

            Grip.MouseLeftButtonDown += Grip_Down;
            Grip.MouseMove += Grip_Move;
            Grip.MouseLeftButtonUp += Grip_Up;
            Grip.LostMouseCapture += (s, e) => { if (resizing) { resizing = false; Settings.Save(); } };

            Strip.SizeChanged += (s, e) => QueuePlace();
            StartCard.SizeChanged += (s, e) => QueuePlace();
            ClockCard.SizeChanged += (s, e) => QueuePlace();

            InitEndGrips();
            LayoutEndGrips();

            Root.MouseLeftButtonDown += Move_Down;
            Root.MouseMove += Move_Move;
            Root.MouseLeftButtonUp += Move_Up;
            Root.LostMouseCapture += (s, e) => EndMove();
        }

        /// Empty spot of the bar (not a card, button or text).
        bool IsEmptySpot(object src) => src == Root || src is Grid || src is StackPanel || src is ScrollContentPresenter || src is ScrollViewer;

        void ApplyLayout()
        {
            bool v = Vertical;
            zoom.ScaleX = zoom.ScaleY = Settings.Scale;

            // three parts: start | strip | clock   — side by side or stacked
            Main.ColumnDefinitions.Clear();
            Main.RowDefinitions.Clear();
            for (int i = 0; i < 3; i++)
            {
                var size = i == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
                if (v) Main.RowDefinitions.Add(new RowDefinition { Height = size });
                else Main.ColumnDefinitions.Add(new ColumnDefinition { Width = size });
            }
            var parts = new FrameworkElement[] { StartCard, StripHost, ClockCard };
            for (int i = 0; i < 3; i++) { Grid.SetColumn(parts[i], v ? 0 : i); Grid.SetRow(parts[i], v ? i : 0); }
            foreach (var el in new FrameworkElement[] { TaskbarAsk, Toast }) { Grid.SetColumn(el, 0); Grid.SetRow(el, 0); Grid.SetColumnSpan(el, 3); Grid.SetRowSpan(el, 3); }
            Main.Margin = v ? new Thickness(10, 10, 10, 10) : new Thickness(10, 0, 10, 0);
            if (Floating)
            {
                Root.BorderThickness = new Thickness(1);
                Root.CornerRadius = new CornerRadius(FloatRadius);
                Root.Background = new SolidColorBrush(TintA(BarAlpha()));
                Root.BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
            }
            else
            {
                Root.BorderThickness = Edge switch { 0 => new Thickness(0, 0, 1, 0), 1 => new Thickness(0, 0, 0, 1), 2 => new Thickness(1, 0, 0, 0), _ => new Thickness(0, 1, 0, 0) };
                Root.CornerRadius = new CornerRadius(0);
                Root.Background = new SolidColorBrush(TintA(BarAlpha(0.98)));
                Root.BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            }
            AiPanel.Orientation = v ? Orientation.Vertical : Orientation.Horizontal;

            // cards: fixed widths in a row, full width when stacked
            foreach (var c in Strip.Children.OfType<FrameworkElement>().Append(StartCard).Append(ClockCard))
            {
                if (!designWidth.ContainsKey(c)) designWidth[c] = c.Width;
                c.Width = v ? double.NaN : designWidth[c];
                c.Margin = v ? V8 : H8;
            }
            ClockCard.Margin = v ? new Thickness(0, 8, 0, 0) : new Thickness(8, 0, 0, 0);
            TaskbarAsk.MaxWidth = v ? 300 : double.PositiveInfinity;

            Strip.Orientation = v ? Orientation.Vertical : Orientation.Horizontal;
            Strip.VerticalAlignment = v ? (Floating ? VerticalAlignment.Center : VerticalAlignment.Top) : VerticalAlignment.Center;
            Strip.HorizontalAlignment = !v && Floating ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            LayoutEndGrips();
            StripScroll.HorizontalScrollBarVisibility = v ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
            StripScroll.VerticalScrollBarVisibility = v ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;

            // app icons and tray icons wrap into rows when the bar is a side panel
            WindowsCard.Height = TrayCard.Height = AiCard.Height = v ? double.NaN : 74;
            AppsPanel.Orientation = v ? Orientation.Vertical : Orientation.Horizontal;
            PinSep.Width = v ? double.NaN : 1;
            PinSep.Height = v ? 1 : double.NaN;
            PinSep.Margin = v ? new Thickness(10, 5, 10, 5) : new Thickness(5, 10, 5, 10);
            PinList.ItemsPanel = Panel(v ? typeof(WrapPanel) : typeof(StackPanel), Orientation.Horizontal);
            WindowsList.ItemsPanel = Panel(v ? typeof(WrapPanel) : typeof(StackPanel), Orientation.Horizontal);
            TrayList.ItemsPanel = Panel(v ? typeof(WrapPanel) : typeof(StackPanel), Orientation.Horizontal);
            QuickPanel.Orientation = Orientation.Horizontal;   // two pills + one button fit a side panel's width too
            QuickPanel.HorizontalAlignment = v ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            MonthDots.HorizontalAlignment = v ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            if (TrayMoreBtn is Button more)
            {
                more.Width = 40;
                more.Height = 40;
                more.Content = "";   // up: the arrow sits beside the icons in both layouts
            }

            // scroll fades/arrows at the ends of the strip
            SetFade(FadeLeft, FadeLeftBtn, start: true, v);
            SetFade(FadeRight, FadeRightBtn, start: false, v);

            // resize grip on the edge facing the screen
            Grip.Width = v ? 6 : double.NaN;
            Grip.Height = v ? double.NaN : 6;
            Grip.HorizontalAlignment = Edge == 0 ? HorizontalAlignment.Right : Edge == 2 ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            Grip.VerticalAlignment = Edge == 3 ? VerticalAlignment.Top : Edge == 1 ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
            Grip.Cursor = v ? Cursors.SizeWE : Cursors.SizeNS;

            Dispatcher.BeginInvoke(UpdateFades, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        static ItemsPanelTemplate Panel(Type t, Orientation o, double maxHeight = double.PositiveInfinity)
        {
            var f = new FrameworkElementFactory(t);
            f.SetValue(t == typeof(WrapPanel) ? WrapPanel.OrientationProperty : StackPanel.OrientationProperty, o);
            if (!double.IsPositiveInfinity(maxHeight)) f.SetValue(MaxHeightProperty, maxHeight);
            return new ItemsPanelTemplate(f);
        }

        static void SetFade(Border fade, Button btn, bool start, bool v)
        {
            var c0 = TintA(BarAlpha(1.05));
            var c1 = TintA(BarAlpha(0.85));
            var c2 = TintA(0x00);
            Point a = v ? new Point(0, start ? 0 : 1) : new Point(start ? 0 : 1, 0);
            Point b = v ? new Point(0, start ? 1 : 0) : new Point(start ? 1 : 0, 0);
            fade.Background = new LinearGradientBrush(new GradientStopCollection { new(c0, 0), new(c1, 0.5), new(c2, 1) }, a, b);
            fade.Width = v ? double.NaN : 48;
            fade.Height = v ? 40 : double.NaN;
            fade.HorizontalAlignment = v ? HorizontalAlignment.Stretch : start ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            fade.VerticalAlignment = !v ? VerticalAlignment.Stretch : start ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            btn.HorizontalAlignment = v ? HorizontalAlignment.Center : start ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            btn.VerticalAlignment = !v ? VerticalAlignment.Center : start ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            btn.Content = v ? (start ? "" : "") : (start ? "" : "");
        }

        // ---------------------------------------------------------------
        // scrolling along the strip (horizontal or vertical)
        // ---------------------------------------------------------------
        double StripOffset => Vertical ? StripScroll.VerticalOffset : StripScroll.HorizontalOffset;
        double StripScrollable => Vertical ? StripScroll.ScrollableHeight : StripScroll.ScrollableWidth;
        double StripViewport => Vertical ? StripScroll.ViewportHeight : StripScroll.ViewportWidth;
        void StripScrollTo(double o) { if (Vertical) StripScroll.ScrollToVerticalOffset(o); else StripScroll.ScrollToHorizontalOffset(o); }

        // ---------------------------------------------------------------
        // placing popups next to the bar, whichever edge it's on
        // ---------------------------------------------------------------
        /// Screen position (physical px) for a popup of w x h next to the anchor, on the screen side of the bar.
        public (int x, int y) PopupPos(FrameworkElement anchor, int w, int h, bool center)
        {
            var tl = anchor.PointToScreen(new Point(0, 0));
            var br = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
            Native.GetWindowRect(Hwnd, out var bar);
            var mon = Native.MonitorOf(Hwnd);
            int gap = (int)(8 * VisualTreeHelper.GetDpi(this).DpiScaleX);
            int x, y;
            if (!Vertical)
            {
                x = center ? (int)((tl.X + br.X) / 2) - w / 2 : (int)tl.X;
                x = Math.Max(mon.Left + gap, Math.Min(x, mon.Right - w - gap));
                y = Edge == 1 ? bar.Bottom + gap : bar.Top - h - gap;
            }
            else
            {
                y = center ? (int)((tl.Y + br.Y) / 2) - h / 2 : (int)tl.Y;
                y = Math.Max(mon.Top + gap, Math.Min(y, mon.Bottom - h - gap));
                x = Edge == 0 ? bar.Right + gap : bar.Left - w - gap;
            }
            return (x, y);
        }

        // ---------------------------------------------------------------
        // resize: drag the inner edge
        // ---------------------------------------------------------------
        bool resizing;

        // ---------------------------------------------------------------
        // floating dock: as long as its content, centered, with a gap to the edge
        // ---------------------------------------------------------------
        public bool Floating => Settings.Floating;
        const double FloatGap = 10, FloatSide = 36, FloatRadius = 22;
        Native.RECT dockRect;
        bool placeQueued;

        int FloatGapPx => Floating ? (int)Math.Round(FloatGap * VisualTreeHelper.GetDpi(this).DpiScaleX) : 0;

        static double Outer(FrameworkElement e, bool v) =>
            e.Visibility == Visibility.Collapsed ? 0 : v ? e.ActualHeight + e.Margin.Top + e.Margin.Bottom : e.ActualWidth + e.Margin.Left + e.Margin.Right;

        /// Length the bar needs to show every card without scrolling (DIPs, including the size scale).
        double ContentLength()
        {
            bool v = Vertical;
            double m = v ? Main.Margin.Top + Main.Margin.Bottom : Main.Margin.Left + Main.Margin.Right;
            double strip = v ? Strip.ActualHeight : Strip.ActualWidth;
            return (m + Outer(StartCard, v) + strip + Outer(ClockCard, v) + 2) * Settings.Scale;
        }

        void QueuePlace()
        {
            if (placeQueued) return;
            placeQueued = true;
            Dispatcher.BeginInvoke(() => { placeQueued = false; PlaceWindow(); }, System.Windows.Threading.DispatcherPriority.Background);
        }

        void PlaceWindow()
        {
            if (!appBarOn) return;
            var r = dockRect;
            if (!Floating)
            {
                shownRect = r;
                ApplySlide(sized: true);
                Native.ClearRegion(Hwnd);
                return;
            }
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int gap = FloatGapPx, thick = (int)Math.Round(BarThickness * dpi);
            int along = Vertical ? r.Bottom - r.Top : r.Right - r.Left;
            int want = (int)Math.Ceiling((Settings.DockLength > 0 ? Settings.DockLength : ContentLength()) * dpi);
            int side = (int)Math.Round(FloatSide * dpi);
            int len = want < 200 ? along - 2 * side : Math.Min(along - 2 * side, want);
            int x, y, w, h;
            if (!Vertical)
            {
                w = len; h = thick;
                x = r.Left + (along - len) / 2;
                y = Edge == 1 ? r.Top + gap : r.Bottom - gap - thick;
            }
            else
            {
                w = thick; h = len;
                y = r.Top + (along - len) / 2;
                x = Edge == 0 ? r.Left + gap : r.Right - gap - thick;
            }
            shownRect = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
            ApplySlide(sized: true);
            Native.SetRoundRegion(Hwnd, (int)Math.Round(FloatRadius * Settings.Scale * dpi));
        }

        // ---------------------------------------------------------------
        // dock length: drag either end of the floating dock (it stays centered)
        // ---------------------------------------------------------------
        readonly Border[] endGrips = new Border[2];
        bool lengthDrag;
        const double MinDockLength = 320;

        void InitEndGrips()
        {
            for (int i = 0; i < 2; i++)
            {
                var handle = new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), Opacity = 0 };
                var g = new Border { Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)), Child = handle,
                    ToolTip = "Drag to make the dock longer or shorter. Double-click: fit the content." };
                g.MouseEnter += (s, e) => handle.Opacity = 1;
                g.MouseLeave += (s, e) => { if (!lengthDrag) handle.Opacity = 0; };
                g.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ClickCount == 2) { SetDockLength(0); e.Handled = true; return; }
                    if (!appBarOn) return;
                    lengthDrag = true;
                    ((UIElement)s).CaptureMouse();
                    HideHover();
                    e.Handled = true;
                };
                g.MouseMove += (s, e) => { if (lengthDrag) LengthDrag_Move(); };
                g.MouseLeftButtonUp += (s, e) => ((UIElement)s).ReleaseMouseCapture();
                g.LostMouseCapture += (s, e) => { if (lengthDrag) { lengthDrag = false; handle.Opacity = 0; Settings.Save(); } };
                endGrips[i] = g;
                Shell.Children.Add(g);
            }
        }

        void LayoutEndGrips()
        {
            if (endGrips[0] == null) return;
            bool v = Vertical;
            for (int i = 0; i < 2; i++)
            {
                var g = endGrips[i];
                var handle = (Border)g.Child;
                g.Visibility = Floating ? Visibility.Visible : Visibility.Collapsed;
                g.Width = v ? double.NaN : 12;
                g.Height = v ? 12 : double.NaN;
                g.HorizontalAlignment = v ? HorizontalAlignment.Stretch : i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                g.VerticalAlignment = !v ? VerticalAlignment.Stretch : i == 0 ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                g.Cursor = v ? Cursors.SizeNS : Cursors.SizeWE;
                handle.Width = v ? 34 : 4;
                handle.Height = v ? 4 : 34;
                handle.HorizontalAlignment = HorizontalAlignment.Center;
                handle.VerticalAlignment = VerticalAlignment.Center;
            }
        }

        void LengthDrag_Move()
        {
            GetCursorPos(out var p);
            var r = dockRect;
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double center = Vertical ? (r.Top + r.Bottom) / 2.0 : (r.Left + r.Right) / 2.0;
            double half = Math.Abs((Vertical ? p.Y : p.X) - center);
            double along = (Vertical ? r.Bottom - r.Top : r.Right - r.Left) / dpi - 2 * FloatSide;
            double len = Math.Clamp(2 * half / dpi, MinDockLength, along);
            // snap to "fit content" when close to it
            if (Math.Abs(len - ContentLength()) < 24) len = Math.Min(ContentLength(), along);
            if (Math.Abs(len - Settings.DockLength) < 1) return;
            Settings.DockLength = Math.Round(len);
            PlaceWindow();
        }

        void SetDockLength(double len)
        {
            Settings.DockLength = len;
            Settings.Save();
            PlaceWindow();
        }

        MenuItem LengthMenu()
        {
            var m = new MenuItem { Header = "Dock length", IsEnabled = Floating };
            m.Items.Add(MI("Fit the content", () => SetDockLength(0), Settings.DockLength <= 0));
            var mon = Native.PrimaryMonitor();
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double along = (Vertical ? mon.Bottom - mon.Top : mon.Right - mon.Left) / dpi - 2 * FloatSide;
            foreach (var (name, k) in new[] { ("Short (50%)", 0.5), ("Medium (70%)", 0.7), ("Long (85%)", 0.85), ("Full width", 1.0) })
            {
                double len = Math.Round(along * k);
                m.Items.Add(MI(name, () => SetDockLength(len), Settings.DockLength > 0 && Math.Abs(Settings.DockLength - len) < 4));
            }
            m.Items.Add(new Separator());
            m.Items.Add(MI("Tip: drag either end of the dock", () => { }, enabled: false));
            return m;
        }

        void SetFloating(bool on)
        {
            Settings.Floating = on;
            Settings.Save();
            ApplyLayout();
            LayoutEndGrips();
            Dock();
        }

        void Grip_Down(object s, MouseButtonEventArgs e)
        {
            if (!appBarOn) return;
            resizing = true;
            Grip.CaptureMouse();
            e.Handled = true;
        }

        void Grip_Move(object s, MouseEventArgs e)
        {
            if (!resizing) return;
            GetCursorPos(out var p);
            var mon = Native.PrimaryMonitor();
            double px = Edge switch { 0 => p.X - mon.Left, 1 => p.Y - mon.Top, 2 => mon.Right - p.X, _ => mon.Bottom - p.Y } - FloatGapPx;
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double scale = Math.Clamp(px / ((Vertical ? VerticalBase : HorizontalBase) * dpi), MinScale, MaxScale);
            if (Math.Abs(scale - Settings.Scale) < 0.01) return;
            SetScale(scale, save: false);
        }

        void Grip_Up(object s, MouseButtonEventArgs e)
        {
            if (!resizing) return;
            Grip.ReleaseMouseCapture();
        }

        void SetScale(double scale, bool save = true)
        {
            Settings.Scale = Math.Round(Math.Clamp(scale, MinScale, MaxScale), 2);
            zoom.ScaleX = zoom.ScaleY = Settings.Scale;
            Dock();
            if (save) Settings.Save();
        }

        // ---------------------------------------------------------------
        // move: drag an empty spot of the bar towards another screen edge
        // ---------------------------------------------------------------
        bool moveArmed, moving;
        CPOINT moveStart;

        void Move_Down(object s, MouseButtonEventArgs e)
        {
            if (!appBarOn || !IsEmptySpot(e.OriginalSource) || CardOf(e.OriginalSource) != null) return;
            GetCursorPos(out moveStart);
            moveArmed = true;
            Root.CaptureMouse();
        }

        void Move_Move(object s, MouseEventArgs e)
        {
            if (!moveArmed) return;
            GetCursorPos(out var p);
            if (!moving)
            {
                if (Math.Abs(p.X - moveStart.X) + Math.Abs(p.Y - moveStart.Y) < 30) return;
                moving = true;
                Cursor = Cursors.SizeAll;
                CloseFlyoutsExcept(null);
            }
            var mon = Native.PrimaryMonitor();
            double w = mon.Right - mon.Left, h = mon.Bottom - mon.Top;
            // nearest edge, measured relative to the screen size so corners feel fair
            var d = new[] { (p.X - mon.Left) / w, (p.Y - mon.Top) / h, (mon.Right - p.X) / w, (mon.Bottom - p.Y) / h };
            int edge = Array.IndexOf(d, d.Min());
            if (edge != Edge) SetEdge(edge, save: false);
        }

        void Move_Up(object s, MouseButtonEventArgs e)
        {
            if (moveArmed) Root.ReleaseMouseCapture();
        }

        void EndMove()
        {
            if (!moveArmed) return;
            moveArmed = false;
            if (moving) { moving = false; Cursor = null; Settings.Save(); }
        }

        void SetEdge(int edge, bool save = true)
        {
            Settings.Edge = edge;
            ApplyLayout();
            Dock();
            if (save) Settings.Save();
        }

        MenuItem PositionMenu()
        {
            var m = new MenuItem { Header = "Bar position" };
            string[] names = { "Left", "Top", "Right", "Bottom" };
            foreach (int i in new[] { 3, 1, 0, 2 })
            {
                int edge = i;
                m.Items.Add(MI(names[i], () => SetEdge(edge), Edge == i));
            }
            return m;
        }

        MenuItem SizeMenu()
        {
            var m = new MenuItem { Header = "Bar size" };
            foreach (var (name, k) in new[] { ("Small", 0.8), ("Normal", 1.0), ("Large", 1.2), ("Extra large", 1.4) })
            {
                double scale = k;
                m.Items.Add(MI(name, () => SetScale(scale), Math.Abs(Settings.Scale - k) < 0.04));
            }
            m.Items.Add(new Separator());
            m.Items.Add(MI("Tip: drag the bar's inner edge to resize freely", () => { }, enabled: false));
            return m;
        }
    }
}
