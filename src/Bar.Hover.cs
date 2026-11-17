using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace GlassBar
{
    /// Instant name labels: hovering an icon on the bar shows its name right away
    /// in a small dark pill next to it (instead of the slow Windows tooltip).
    public partial class BarWindow
    {
        Popup hoverPop;
        Border hoverBox;
        TextBlock hoverText;
        FrameworkElement hoverEl;

        void InitHover()
        {
            hoverText = new TextBlock { FontSize = 12.5, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
            hoverBox = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x0E, 0x16, 0x2A)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 5, 10, 6),
                Child = hoverText,
                IsHitTestVisible = false
            };
            hoverPop = new Popup
            {
                Child = hoverBox, AllowsTransparency = true, Placement = PlacementMode.Relative, PlacementTarget = Shell,
                IsHitTestVisible = false, StaysOpen = true, PopupAnimation = PopupAnimation.None, Focusable = false
            };
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
            if (drag?.Active == true || moving || resizing || (thumb != null && thumb.IsVisible)) { HideHover(); return; }
            var el = LabelOwner(src);
            if (el == null) { HideHover(); return; }
            ToolTipService.SetIsEnabled(el, false);   // ours replaces the slow Windows tooltip
            var text = (string)el.ToolTip;
            if (el == hoverEl && hoverPop.IsOpen && hoverText.Text == text) return;
            hoverEl = el;
            hoverText.Text = text;
            hoverBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = hoverBox.DesiredSize;

            Rect b;
            try { b = el.TransformToAncestor(Shell).TransformBounds(new Rect(el.RenderSize)); }
            catch { HideHover(); return; }
            const double gap = 10;
            double x, y;
            switch (Edge)
            {
                case 0: x = b.Right + gap + 6; y = b.Top + (b.Height - size.Height) / 2; break;
                case 2: x = b.Left - size.Width - gap - 6; y = b.Top + (b.Height - size.Height) / 2; break;
                case 1: x = b.Left + (b.Width - size.Width) / 2; y = b.Bottom + gap + 6; break;
                default: x = b.Left + (b.Width - size.Width) / 2; y = b.Top - size.Height - gap - 6; break;
            }
            // left/right edge: keep it outside the bar
            if (Edge == 0) x = System.Math.Max(x, Shell.ActualWidth + gap);
            if (Edge == 2) x = System.Math.Min(x, -size.Width - gap);
            if (Edge == 1) y = System.Math.Max(y, Shell.ActualHeight + gap);
            if (Edge == 3) y = System.Math.Min(y, -size.Height - gap);
            hoverPop.HorizontalOffset = x;
            hoverPop.VerticalOffset = y;
            if (!hoverPop.IsOpen) hoverPop.IsOpen = true;
        }

        void HideHover()
        {
            hoverEl = null;
            if (hoverPop != null) hoverPop.IsOpen = false;
        }

        /// Update the label if it is showing for this element (e.g. AI usage numbers changed).
        void RefreshHover(FrameworkElement el)
        {
            if (hoverEl == el && hoverPop.IsOpen && Mouse.DirectlyOver is DependencyObject d) { hoverEl = null; UpdateHover(d); }
        }
    }
}
