using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlassBar
{
    /// Small dark name label that floats next to the icon under the mouse.
    /// Its own window, placed in screen pixels, so the bar's size scale and DPI can't shift it.
    public class HoverLabel : Window
    {
        readonly TextBlock text = new() { FontSize = 12.5, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        IntPtr hwnd;

        public HoverLabel()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            IsHitTestVisible = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = new FontFamily("Segoe UI");
            UseLayoutRounding = true;
            Left = -20000; Top = -20000;
            Content = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x0E, 0x16, 0x2A)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 5, 10, 6),
                Child = text
            };
            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.MakeToolWindow(hwnd, noActivate: true);
                // clicks go through it
                long ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (IntPtr)(ex | 0x20 /* WS_EX_TRANSPARENT */));
            };
        }

        /// Show the text centred on the anchor's side facing the screen (edge: 0 left, 1 top, 2 right, 3 bottom of the screen).
        internal void ShowFor(FrameworkElement anchor, string label, int edge, Native.RECT bar)
        {
            text.Text = label;
            if (hwnd == IntPtr.Zero) Show();
            // measure in DIPs, convert with the label's own DPI
            text.UpdateLayout();
            UpdateLayout();
            var content = (FrameworkElement)Content;
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var dpi = VisualTreeHelper.GetDpi(anchor);
            int w = (int)Math.Ceiling(content.DesiredSize.Width * dpi.DpiScaleX);
            int h = (int)Math.Ceiling(content.DesiredSize.Height * dpi.DpiScaleY);

            var tl = anchor.PointToScreen(new Point(0, 0));
            var br = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
            double cx = (tl.X + br.X) / 2, cy = (tl.Y + br.Y) / 2;
            int gap = (int)Math.Round(8 * dpi.DpiScaleX);
            int x, y;
            switch (edge)
            {
                case 0: x = bar.Right + gap; y = (int)Math.Round(cy - h / 2.0); break;
                case 2: x = bar.Left - gap - w; y = (int)Math.Round(cy - h / 2.0); break;
                case 1: x = (int)Math.Round(cx - w / 2.0); y = bar.Bottom + gap; break;
                default: x = (int)Math.Round(cx - w / 2.0); y = bar.Top - gap - h; break;
            }
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            if (!IsVisible) Show();
        }

        public void HideLabel()
        {
            if (hwnd != IntPtr.Zero && IsVisible) Hide();
        }
    }

    /// Instant name labels: hovering an icon on the bar shows its name right away.
    public partial class BarWindow
    {
        HoverLabel hoverLabel;
        FrameworkElement hoverEl;
        string hoverShown;

        void InitHover()
        {
            if (snapshotPath != null) return;
            Root.PreviewMouseMove += (s, e) => UpdateHover(e.OriginalSource as DependencyObject);
            Root.MouseLeave += (s, e) => HideHover();
            Root.PreviewMouseDown += (s, e) => HideHover();
            Deactivated += (s, e) => HideHover();
        }

        /// The nearest element under the mouse that has a text tooltip.
        FrameworkElement LabelOwner(DependencyObject d)
        {
            while (d != null && d != Root)
            {
                if (d is FrameworkElement fe && fe.ToolTip is string t && t.Length > 0) return fe;
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        void UpdateHover(DependencyObject src)
        {
            if (snapshotPath != null) return;
            if (drag?.Active == true || moving || resizing || lengthDrag || sliding || (thumb != null && thumb.IsVisible)) { HideHover(); return; }
            var el = LabelOwner(src);
            if (el == null) { HideHover(); return; }
            ToolTipService.SetIsEnabled(el, false);   // ours replaces the slow Windows tooltip
            var text = (string)el.ToolTip;
            if (el == hoverEl && hoverShown == text && hoverLabel?.IsVisible == true) return;
            hoverEl = el;
            hoverShown = text;
            hoverLabel ??= new HoverLabel();
            Native.GetWindowRect(Hwnd, out var bar);
            hoverLabel.ShowFor(el, text, Edge, bar);
        }

        void HideHover()
        {
            hoverEl = null;
            hoverShown = null;
            hoverLabel?.HideLabel();
        }

        /// Update the label if it is showing for this element (e.g. AI usage numbers changed).
        void RefreshHover(FrameworkElement el)
        {
            if (hoverEl == el && Mouse.DirectlyOver is DependencyObject d) { hoverEl = null; UpdateHover(d); }
        }
    }
}
