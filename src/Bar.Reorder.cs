using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GlassBar
{
    /// Drag and drop on the bar: press on a card or a pinned app and drag it to a new place.
    /// The others slide out of the way while you drag; the order is remembered.
    /// Files dropped on a pinned app open in that app.
    public partial class BarWindow
    {
        class DragState
        {
            public Panel Panel;
            public ItemsControl Items;          // set for pinned apps / windows (item containers)
            public FrameworkElement Item;
            public Point Start;
            public bool Active, Free;
            public List<FrameworkElement> Kids;
            public List<Rect> Slots;
            public int From, To;
        }

        DragState drag;
        const double DragThreshold = 8;

        void InitReorder()
        {
            ApplyCardOrder();
            Strip.PreviewMouseLeftButtonDown += Reorder_Down;
            Strip.PreviewMouseMove += Reorder_Move;
            Strip.PreviewMouseLeftButtonUp += Reorder_Up;
            Strip.LostMouseCapture += (s, e) => { if (drag?.Active == true) EndReorder(); };
        }

        /// The card on the strip that contains this element, if any.
        FrameworkElement CardOf(object src)
        {
            var d = src as DependencyObject;
            while (d != null)
            {
                var parent = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
                if (parent == Strip) return d as FrameworkElement;
                d = parent;
            }
            return null;
        }

        static bool IsInput(object src)
        {
            for (var d = src as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : null)
                if (d is System.Windows.Controls.Primitives.TextBoxBase || d is Slider || d is ScrollBar || d is PasswordBox) return true;
            return false;
        }

        /// A pinned app or open window under the mouse: (its list, its container).
        (ItemsControl list, FrameworkElement container) ItemOfList(object src)
        {
            foreach (var list in new[] { PinList, WindowsList })
            {
                var cont = list.ContainerFromElement(src as DependencyObject) as FrameworkElement;
                if (cont != null) return (list, cont);
            }
            return (null, null);
        }

        void Reorder_Down(object s, MouseButtonEventArgs e)
        {
            drag = null;
            if (IsInput(e.OriginalSource)) return;
            var (list, cont) = ItemOfList(e.OriginalSource);
            if (cont != null)
            {
                drag = new DragState { Items = list, Item = cont, Panel = VisualTreeHelper.GetParent(cont) as Panel, Start = e.GetPosition(Strip) };
            }
            else if (CardOf(e.OriginalSource) is FrameworkElement card)
            {
                drag = new DragState { Item = card, Panel = Strip, Start = e.GetPosition(Strip) };
            }
            if (drag?.Panel == null) drag = null;
        }

        void Reorder_Move(object s, MouseEventArgs e)
        {
            if (drag == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { if (drag.Active) EndReorder(); drag = null; return; }
            var p = e.GetPosition(Strip);
            var d = p - drag.Start;
            if (!drag.Active)
            {
                if (Math.Abs(d.X) + Math.Abs(d.Y) < DragThreshold) return;
                BeginReorder();
            }
            e.Handled = true;

            bool v = drag.Panel == Strip ? Vertical : !(drag.Panel is StackPanel sp) || sp.Orientation == Orientation.Vertical;
            if (!drag.Free) d = v ? new Vector(0, d.Y) : new Vector(d.X, 0);
            var tt = (TranslateTransform)drag.Item.RenderTransform;
            tt.X = d.X; tt.Y = d.Y;

            // where would it land?
            var me = drag.Slots[drag.From];
            var center = new Point(me.X + me.Width / 2 + d.X, me.Y + me.Height / 2 + d.Y);
            int to;
            if (drag.Free)
            {
                to = 0; double best = double.MaxValue;
                for (int i = 0; i < drag.Slots.Count; i++)
                {
                    var r = drag.Slots[i];
                    double dist = (new Point(r.X + r.Width / 2, r.Y + r.Height / 2) - center).LengthSquared;
                    if (dist < best) { best = dist; to = i; }
                }
            }
            else
            {
                to = 0;
                for (int i = 0; i < drag.Kids.Count; i++)
                {
                    if (i == drag.From || drag.Kids[i].Visibility == Visibility.Collapsed) continue;
                    var r = drag.Slots[i];
                    double c = v ? r.Y + r.Height / 2 : r.X + r.Width / 2;
                    if (c < (v ? center.Y : center.X)) to++;
                }
                // collapsed children before the target keep their relative place
                to = Math.Min(to, drag.Kids.Count - 1);
            }
            if (to != drag.To) { drag.To = to; ShiftOthers(v); }

            // drag near the end of the strip: scroll it along
            if (drag.Panel == Strip || drag.Items != null)
            {
                var vp = e.GetPosition(StripScroll);
                double pos = Vertical ? vp.Y : vp.X, len = Vertical ? StripScroll.ActualHeight : StripScroll.ActualWidth;
                if (pos < 50) ScrollBy(-18);
                else if (pos > len - 50) ScrollBy(18);
            }
        }

        void BeginReorder()
        {
            drag.Active = true;
            drag.Free = drag.Panel is WrapPanel;
            drag.Kids = drag.Panel.Children.OfType<FrameworkElement>().ToList();
            drag.From = drag.To = drag.Kids.IndexOf(drag.Item);
            drag.Slots = drag.Kids.Select(k => k.TransformToAncestor(Strip).TransformBounds(new Rect(k.RenderSize))).ToList();
            foreach (var k in drag.Kids) k.RenderTransform = new TranslateTransform();
            System.Windows.Controls.Panel.SetZIndex(drag.Item, 100);
            drag.Item.Opacity = 0.92;
            drag.Item.RenderTransformOrigin = new Point(0.5, 0.5);
            HideHover();
            HideThumb();
            CloseFlyoutsExcept(null);
            Strip.CaptureMouse();
            Cursor = Cursors.SizeAll;
        }

        /// Slide the other items to where they'll be if the dragged one is dropped now.
        void ShiftOthers(bool v)
        {
            var order = drag.Kids.Where(k => k != drag.Item).ToList();
            order.Insert(drag.To, drag.Item);
            var targets = new Dictionary<FrameworkElement, Point>();
            if (drag.Free)
            {
                for (int i = 0; i < order.Count; i++) targets[order[i]] = drag.Slots[i].TopLeft;
            }
            else
            {
                // pack them one after another, each keeping its own size + spacing
                double pos = v ? drag.Slots[0].Y : drag.Slots[0].X;
                foreach (var k in order)
                {
                    int i = drag.Kids.IndexOf(k);
                    var r = drag.Slots[i];
                    double size;
                    if (i + 1 < drag.Slots.Count) size = v ? drag.Slots[i + 1].Y - r.Y : drag.Slots[i + 1].X - r.X;
                    else size = (v ? r.Height : r.Width) + (v ? k.Margin.Bottom + k.Margin.Top : k.Margin.Right + k.Margin.Left);
                    targets[k] = v ? new Point(r.X, pos) : new Point(pos, r.Y);
                    pos += size;
                }
            }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            foreach (var k in drag.Kids)
            {
                if (k == drag.Item) continue;
                var r = drag.Slots[drag.Kids.IndexOf(k)];
                var t = targets[k];
                var tt = (TranslateTransform)k.RenderTransform;
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(t.X - r.X, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(t.Y - r.Y, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
            }
        }

        void Reorder_Up(object s, MouseButtonEventArgs e)
        {
            if (drag?.Active == true)
            {
                e.Handled = true;           // no click on the thing we just dropped
                Strip.ReleaseMouseCapture(); // -> EndReorder
            }
            drag = null;
        }

        void EndReorder()
        {
            var st = drag;
            if (st == null || !st.Active) return;
            st.Active = false;
            Cursor = null;
            foreach (var k in st.Kids)
            {
                k.RenderTransform = Transform.Identity;
                System.Windows.Controls.Panel.SetZIndex(k, 0);
            }
            st.Item.Opacity = 1;
            if (st.To == st.From) return;

            if (st.Items == PinList)
            {
                pins.Move(st.From, st.To);
                Settings.PinOrder = pins.Select(p => System.IO.Path.GetFileName(p.Path)).ToList();
            }
            else if (st.Items == WindowsList)
            {
                windows.Move(st.From, st.To);
            }
            else
            {
                Strip.Children.Remove(st.Item);
                Strip.Children.Insert(st.To, st.Item);
                Settings.CardOrder = Strip.Children.OfType<FrameworkElement>().Select(c => c.Name).ToList();
            }
            Settings.Save();
            UpdateFades();
        }

        /// Put the strip's cards in the order the user dragged them into.
        void ApplyCardOrder()
        {
            var order = Settings.CardOrder;
            if (order == null || order.Count == 0) return;
            var cards = Strip.Children.OfType<FrameworkElement>().ToList();
            int Rank(FrameworkElement c)
            {
                int i = order.IndexOf(c.Name);
                // a card the saved order doesn't know (new in an update): keep it after its old neighbour
                if (i < 0) { int at = cards.IndexOf(c); for (int j = at - 1; j >= 0; j--) { int k = order.IndexOf(cards[j].Name); if (k >= 0) return k * 2 + 1; } return -1; }
                return i * 2;
            }
            var sorted = cards.OrderBy(Rank).ToList();
            Strip.Children.Clear();
            foreach (var c in sorted) Strip.Children.Add(c);
        }

        void ResetOrder()
        {
            Settings.CardOrder = null;
            Settings.PinOrder = null;
            Settings.Save();
            ShowToast("Order reset. Restart the bar to see the original order.");
            LoadPins();
        }

        // ---------------------------------------------------------------
        // drop files on a pinned app to open them with it
        // ---------------------------------------------------------------
        void Pin_DragOver(object s, DragEventArgs e)
        {
            bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) && PinAt(e.OriginalSource) != null;
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void Pin_Drop(object s, DragEventArgs e)
        {
            e.Handled = true;
            if (PinAt(e.OriginalSource) is not PinItem p || e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            Launch(p.Path, string.Join(" ", files.Select(f => "\"" + f + "\"")));
            ShowToast($"Opening {files.Length} file{(files.Length == 1 ? "" : "s")} in {p.Name}");
        }

        PinItem PinAt(object src) => (PinList.ContainerFromElement(src as DependencyObject) as FrameworkElement)?.DataContext as PinItem;
    }
}
