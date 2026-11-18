using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GlassBar
{
    /// Calendar dots, brightness, which widgets are shown, bar transparency, and the folder for snips, notes and videos.
    public partial class BarWindow
    {
        // =============================================================
        // widgets on/off
        // =============================================================
        static readonly (string card, string label)[] WidgetList =
        {
            ("SearchCard", "Search"), ("WindowsCard", "Apps and windows"), ("MediaCard", "Media"), ("ChatsCard", "Chats and mail"),
            ("AiCard", "AI usage"), ("LsCard", "LocalSend"), ("RecentCard", "Recently opened"), ("NotesCard", "Notes"),
            ("TasksCard", "CPU and memory"), ("OmenCard", "OMEN Monitor"), ("ToolsCard", "Snip and record"), ("TermCard", "Terminal"),
            ("CalendarCard", "Calendar"), ("BrightnessCard", "Brightness"), ("VolumeCard", "Volume"),
            ("TimerCard", "Timer"), ("StopwatchCard", "Stopwatch"), ("QuickCard", "Wi-Fi and Bluetooth"), ("TrayCard", "Tray icons"),
        };

        static string WidgetGlyph(string card) => card switch
        {
            "SearchCard" => "", "WindowsCard" => "", "MediaCard" => "", "ChatsCard" => "", "AiCard" => "",
            "LsCard" => "", "RecentCard" => "", "NotesCard" => "", "TasksCard" => "", "OmenCard" => "",
            "ToolsCard" => "", "TermCard" => "", "CalendarCard" => "", "BrightnessCard" => "", "VolumeCard" => "",
            "TimerCard" => "", "StopwatchCard" => "", "QuickCard" => "", "TrayCard" => "", _ => ""
        };

        static string WidgetLabel(string card) => WidgetList.FirstOrDefault(w => w.card == card).label ?? card;

        bool WidgetShown(string name) => name == "AiCard" ? Settings.AiUsage : !IsHidden((FrameworkElement)FindName(name));

        /// Right-click a widget: hide it. Right-click an empty spot: the bar menu.
        void Root_RightUp(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton != System.Windows.Input.MouseButton.Right || e.Handled) return;
            // icons that have their own right-click menu (pinned apps, windows, tray icons)
            if (FindAncestor<FrameworkElement>(e.OriginalSource) is FrameworkElement fe && fe.DataContext is PinItem or WinItem or TrayItem) return;
            var card = CardOf(e.OriginalSource);
            if (card != null && WidgetList.Any(w => w.card == card.Name))
            {
                e.Handled = true;
                var m = new ContextMenu();
                var hide = new MenuItem { Header = $"Hide {WidgetLabel(card.Name)}", Icon = Flyout.Glyph("", 13) };
                string name = card.Name;
                hide.Click += (_, _) => { SetWidgetShown(name, false); ShowToast($"{WidgetLabel(name)} hidden. Get it back from ⋯ → Hidden widgets.", 5); };
                m.Items.Add(hide);
                m.Items.Add(new Separator());
                m.Items.Add(MI("Bar settings…", () => Menu_Click(null, null)));
                m.IsOpen = true;
                return;
            }
            if (IsEmptySpot(e.OriginalSource)) { e.Handled = true; Menu_Click(null, null); }
        }

        MenuItem HiddenWidgetsMenu()
        {
            var hidden = WidgetList.Where(w => !WidgetShown(w.card) && !(w.card == "BrightnessCard" && brightnessMissing)).ToList();
            var root = new MenuItem { Header = hidden.Count == 0 ? "Hidden widgets" : $"Hidden widgets ({hidden.Count})", Icon = Flyout.Glyph("", 13) };
            if (hidden.Count == 0) root.Items.Add(new MenuItem { Header = "Nothing hidden. Right-click a widget to hide it.", IsEnabled = false });
            foreach (var (card, label) in hidden)
            {
                var mi = new MenuItem { Header = "Show " + label, Icon = Flyout.Glyph(WidgetGlyph(card), 14, Flyout.Res("TextPrimary")) };
                mi.Click += (_, _) => SetWidgetShown(card, true);
                root.Items.Add(mi);
            }
            if (hidden.Count > 1)
            {
                root.Items.Add(new Separator());
                root.Items.Add(MI("Show all", () => { foreach (var (c, _) in hidden) SetWidgetShown(c, true); }));
            }
            return root;
        }

        /// GB_DEMO=1: for screen recordings, leaves out personal things (name, recent files, chats, media, notes).
        public static readonly bool Demo = Environment.GetEnvironmentVariable("GB_DEMO") != null;
        static readonly string[] DemoHidden = { "MediaCard", "ChatsCard", "AiCard", "RecentCard", "NotesCard" };

        bool IsHidden(FrameworkElement card) => Demo && DemoHidden.Contains(card.Name) ? true : card.Name switch
        {
            "TimerCard" => !Settings.TimerOnBar,
            "StopwatchCard" => !Settings.StopwatchOnBar,
            _ => Settings.HiddenCards?.Contains(card.Name) == true
        };

        void ApplyHiddenWidgets()
        {
            foreach (var (name, _) in WidgetList)
            {
                if (FindName(name) is not FrameworkElement c) continue;
                if (name == "AiCard") { UpdateAiVisibility(); continue; }
                if (name == "WindowsCard") { c.Visibility = IsHidden(c) ? Visibility.Collapsed : (windows.Count > 0 || pins.Count > 0 ? Visibility.Visible : Visibility.Collapsed); continue; }
                if (name == "BrightnessCard" && brightnessMissing) { c.Visibility = Visibility.Collapsed; continue; }
                c.Visibility = IsHidden(c) ? Visibility.Collapsed : Visibility.Visible;
            }
            QueuePlace();
        }

        void SetWidgetShown(string name, bool show)
        {
            switch (name)
            {
                case "TimerCard": Settings.TimerOnBar = show; break;
                case "StopwatchCard": Settings.StopwatchOnBar = show; break;
                case "AiCard": if (Settings.AiUsage != show) { ToggleAi(); return; } break;
                default:
                    Settings.HiddenCards ??= new List<string>();
                    Settings.HiddenCards.Remove(name);
                    if (!show) Settings.HiddenCards.Add(name);
                    if (Settings.HiddenCards.Count == 0) Settings.HiddenCards = null;
                    break;
            }
            Settings.Save();
            ApplyHiddenWidgets();
            if (name is "TimerCard" or "StopwatchCard") UpdateTimers();
        }

        MenuItem WidgetsMenu()
        {
            var root = new MenuItem { Header = "Widgets on the bar" };
            foreach (var (name, label) in WidgetList)
            {
                var mi = new MenuItem { Header = label, IsCheckable = true, IsChecked = WidgetShown(name), StaysOpenOnClick = true };
                if (name == "BrightnessCard" && brightnessMissing) { mi.IsEnabled = false; mi.Header = label + " (not supported by this screen)"; }
                string n = name;
                mi.Click += (s, e) => SetWidgetShown(n, mi.IsChecked);
                root.Items.Add(mi);
            }
            return root;
        }

        // =============================================================
        // transparency
        // =============================================================
        static double barOpacity = 0.89;
        static Color TintA(byte a) { var t = GlassTheme.Tint; return Color.FromArgb(a, t.R, t.G, t.B); }
        static byte BarAlpha(double baseAlpha = 1) => (byte)Math.Round(Math.Clamp(255 * barOpacity * baseAlpha, 0, 255));

        MenuItem OpacityMenu()
        {
            var pct = new TextBlock { Width = 38, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Flyout.Res("TextSecondary") };
            var slider = new Slider { Minimum = 0.15, Maximum = 1, Value = Settings.BarOpacity, Width = 170, Margin = new Thickness(12, 0, 8, 0), Style = (Style)FindResource("BarSlider"), VerticalAlignment = VerticalAlignment.Center };
            pct.Text = $"{Settings.BarOpacity * 100:0}%";
            slider.ValueChanged += (s, e) =>
            {
                Settings.BarOpacity = Math.Round(slider.Value, 2);
                pct.Text = $"{Settings.BarOpacity * 100:0}%";
                ApplyOpacity();
            };
            slider.PreviewMouseUp += (s, e) => Settings.Save();
            slider.LostMouseCapture += (s, e) => Settings.Save();
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = "Opacity", VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(slider);
            row.Children.Add(pct);
            return new MenuItem { Header = row, StaysOpenOnClick = true, ToolTip = "How see-through the bar is" };
        }

        void ApplyOpacity()
        {
            ApplyTheme();
            ApplyLayout();
            ApplyPopupOpacity();
            UpdateRadios();
            if (awakeBtn != null) SetAwakeLook();
            MonthDots.InvalidateVisual();
        }

        /// Colour + opacity into the shared brushes, and into theme.json for ModernGlass Explorer.
        void ApplyTheme()
        {
            barOpacity = Math.Clamp(Settings.BarOpacity <= 0 ? 0.89 : Settings.BarOpacity, 0.15, 1);
            GlassTheme.Set(Settings.Theme, barOpacity);
            GlassTheme.ApplyBrushes(Application.Current.Resources, Math.Min(1, barOpacity * 1.09));
            if (snapshotPath == null) GlassTheme.Save();
        }

        string winAccentSet;

        /// Windows' accent colour follows the bar's colour (once per change, it repaints every window).
        void SyncWindowsAccent(bool force = false)
        {
            if (snapshotPath != null || !Settings.MatchWindowsAccent) return;
            if (!force && winAccentSet == GlassTheme.Current.Name) return;
            if (Settings.OriginalWindowsAccent == null && WinAccent.Get() is WinAccent.Pref orig)
            {
                Settings.OriginalWindowsAccent = new[] { orig.Start, orig.Accent };
                Settings.Save();
            }
            if (WinAccent.Set(GlassTheme.Accent)) winAccentSet = GlassTheme.Current.Name;
        }

        void ToggleWindowsAccent()
        {
            Settings.MatchWindowsAccent = !Settings.MatchWindowsAccent;
            Settings.Save();
            if (Settings.MatchWindowsAccent) { SyncWindowsAccent(force: true); ShowToast("Windows accent colour now follows the bar"); }
            else
            {
                if (Settings.OriginalWindowsAccent is { Length: 2 } o)
                    WinAccent.Set(new WinAccent.Pref { Start = o[0], Accent = o[1] });
                winAccentSet = null;
                ShowToast("Windows accent colour set back to what it was");
            }
        }

        MenuItem ThemeMenu()
        {
            var root = new MenuItem { Header = "Colour", ToolTip = "Also changes ModernGlass Explorer" };
            foreach (var p in GlassTheme.Presets)
            {
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 14, Height = 14,
                    Fill = new LinearGradientBrush(GlassTheme.Hex(p.Accent), GlassTheme.Hex(p.Accent2), 45),
                    Stroke = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), StrokeThickness = 1
                };
                var name = p.Name;
                var mi = new MenuItem { Header = name, Icon = dot, IsChecked = GlassTheme.Current.Name == name, StaysOpenOnClick = true };
                mi.Click += (s, e) =>
                {
                    Settings.Theme = name;
                    Settings.Save();
                    ApplyOpacity();
                    SyncWindowsAccent();
                    foreach (var o in root.Items.OfType<MenuItem>()) o.IsChecked = (string)o.Header == name;
                };
                root.Items.Add(mi);
            }
            root.Items.Add(new Separator());
            var win = new MenuItem { Header = "Windows uses this colour too", IsChecked = Settings.MatchWindowsAccent, StaysOpenOnClick = true,
                                     ToolTip = "Start, taskbar, title bars and the accent in Settings" };
            win.Click += (s, e) => { ToggleWindowsAccent(); win.IsChecked = Settings.MatchWindowsAccent; };
            root.Items.Add(win);
            return root;
        }

        /// Pop-ups are a touch more solid than the bar (text sits on them), but follow the same slider.
        static void ApplyPopupOpacity()
        {
            Flyout.Tint.Color = TintA(BarAlpha(1.04));
            Flyout.Tint.Opacity = 1;
        }

        // =============================================================
        // calendar dots
        // =============================================================
        CalendarFlyout calFly;

        void UpdateCalendar()
        {
            var now = DateTime.Now;
            MonthDots.SetDay(now);
            var en = CultureInfo.GetCultureInfo("en-GB");
            CalDay.Text = now.ToString("MMM d", en);
            int days = DateTime.IsLeapYear(now.Year) ? 366 : 365;
            double yearPct = 100.0 * (now.DayOfYear - 1 + now.TimeOfDay.TotalDays) / days;
            CalPct.Text = $"{yearPct:0}%";
            CalendarCard.ToolTip = $"{now.ToString("dddd d MMMM", en)} · week {ISOWeek.GetWeekOfYear(now)} · day {now.Day} of {DateTime.DaysInMonth(now.Year, now.Month)} · {yearPct:0}% of {now.Year} has passed";
        }

        void Calendar_Click(object s, RoutedEventArgs e)
        {
            calFly ??= Register(new CalendarFlyout());
            calFly.ShowMonth(DateTime.Today);
            calFly.Toggle(CalendarCard);
        }

        // =============================================================
        // brightness
        // =============================================================
        bool brightnessMissing, brightSetting;
        DateTime brightTouched;

        async void InitBrightness()
        {
            var v = await Task.Run(Brightness.Get);
            if (v == null)
            {
                brightnessMissing = true;
                BrightnessCard.Visibility = Visibility.Collapsed;
                QueuePlace();
                return;
            }
            ShowBrightness(v.Value);
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += async (s, e) =>
            {
                // follow changes from the keyboard's brightness keys, but not while dragging
                if (!BrightnessCard.IsVisible || (DateTime.Now - brightTouched).TotalSeconds < 3 || BrightSlider.IsMouseCaptureWithin) return;
                var now = await Task.Run(Brightness.Get);
                if (now != null && (DateTime.Now - brightTouched).TotalSeconds >= 3) ShowBrightness(now.Value);
            };
            t.Start();
        }

        void ShowBrightness(int v)
        {
            brightSetting = true;
            BrightSlider.Value = v;
            brightSetting = false;
            BrightPct.Text = $"{v}%";
        }

        void Bright_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BrightPct == null) return;
            int v = (int)Math.Round(BrightSlider.Value);
            BrightPct.Text = $"{v}%";
            if (brightSetting) return;
            brightTouched = DateTime.Now;
            Brightness.Set(v);
        }

        // =============================================================
        // save folder: <folder>\Snips, Notes, Videos  (Documents\GlassBar by default)
        // =============================================================
        public static string SaveRoot
        {
            get
            {
                var custom = Instance?.Settings.SaveFolder;
                var d = !string.IsNullOrWhiteSpace(custom) ? custom
                      : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GlassBar");
                return d;
            }
        }

        public static string SaveDir(string sub)
        {
            var d = Path.Combine(SaveRoot, sub);
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }

        public static string SnipsDir => SaveDir("Snips");
        public static string NotesDir => SaveDir("Notes");

        void ChooseSaveFolder()
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Where should snips, notes and videos be saved? (Snips, Notes and Videos folders are made inside it)",
                InitialDirectory = Directory.Exists(SaveRoot) ? SaveRoot : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog() != true) return;
            Settings.SaveFolder = dlg.FolderName;
            Settings.Save();
            foreach (var sub in new[] { "Snips", "Notes", "Videos" }) SaveDir(sub);
            LoadNotes();
            ShowToast("Snips, notes and videos are saved in " + dlg.FolderName);
        }

        MenuItem SaveFolderMenu()
        {
            var root = new MenuItem { Header = "Saved snips, notes and videos" };
            root.Items.Add(MI("Open folder", () => { SaveDir("Snips"); SaveDir("Notes"); SaveDir("Videos"); Launch(SaveRoot); }));
            root.Items.Add(MI("Change folder…", ChooseSaveFolder));
            if (!string.IsNullOrWhiteSpace(Settings.SaveFolder))
                root.Items.Add(MI("Back to Documents\\GlassBar", () => { Settings.SaveFolder = null; Settings.Save(); LoadNotes(); ShowToast("Saving in Documents\\GlassBar"); }));
            root.Items.Add(new MenuItem { Header = SaveRoot, IsEnabled = false });
            return root;
        }

        // =============================================================
        // snips: every screenshot from the snipping tool (button or Win+Shift+S) is saved to the Snips folder
        // =============================================================
        [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
        uint clipSeq;
        DateTime snipAsked;

        void InitSnipSaver()
        {
            clipSeq = GetClipboardSequenceNumber();
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            t.Tick += (s, e) => CheckSnip();
            t.Start();
        }

        void CheckSnip()
        {
            uint seq = GetClipboardSequenceNumber();
            if (seq == clipSeq) return;
            clipSeq = seq;
            try
            {
                var owner = GetClipboardOwner();
                string proc = "";
                if (owner != IntPtr.Zero)
                {
                    Native.GetWindowThreadProcessId(owner, out uint pid);
                    proc = Path.GetFileNameWithoutExtension(Native.ProcessPath(pid) ?? "");
                }
                bool fromSnipper = proc.Contains("ScreenClipping", StringComparison.OrdinalIgnoreCase) || proc.Contains("SnippingTool", StringComparison.OrdinalIgnoreCase)
                                   || (DateTime.Now - snipAsked).TotalSeconds < 60 && proc.Length == 0;
                if (!fromSnipper || !Clipboard.ContainsImage()) return;
                var img = Clipboard.GetImage();
                if (img == null) return;
                var file = Path.Combine(SnipsDir, $"Snip {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                using (var fs = File.Create(file)) enc.Save(fs);
                snipAsked = default;
                ShowToast("Snip saved to " + Path.GetFileName(SnipsDir) + " (and copied)");
            }
            catch { }
        }
    }

    /// Month view opened from the calendar dots.
    public class CalendarFlyout : Flyout
    {
        readonly UniformGrid grid = new() { Columns = 7, Margin = new Thickness(14, 4, 14, 14) };
        readonly TextBlock month = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        DateTime shown;
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");

        public CalendarFlyout() : base("Calendar", 360, 390)
        {
            var nav = new DockPanel { Margin = new Thickness(18, 0, 12, 4) };
            var next = SmallIcon("", "Next month", () => ShowMonth(shown.AddMonths(1)));
            var prev = SmallIcon("", "Previous month", () => ShowMonth(shown.AddMonths(-1)));
            var today = Pill("Today", () => ShowMonth(DateTime.Today));
            today.Padding = new Thickness(10, 3, 10, 3);
            DockPanel.SetDock(next, Dock.Right); DockPanel.SetDock(prev, Dock.Right); DockPanel.SetDock(today, Dock.Right);
            nav.Children.Add(next); nav.Children.Add(prev); nav.Children.Add(today);
            nav.Children.Add(month);
            DockPanel.SetDock(nav, Dock.Top);
            Body.Children.Add(nav);
            Body.Children.Add(grid);
        }

        public void ShowMonth(DateTime d)
        {
            shown = new DateTime(d.Year, d.Month, 1);
            month.Text = shown.ToString("MMMM yyyy", En);
            Title2 = DateTime.Today.ToString("dddd d MMMM", En);
            grid.Children.Clear();
            foreach (var n in new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" })
                grid.Children.Add(new TextBlock { Text = n, FontSize = 11, Foreground = Res("TextTertiary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            int lead = ((int)shown.DayOfWeek + 6) % 7;
            var start = shown.AddDays(-lead);
            for (int i = 0; i < 42; i++)
            {
                var day = start.AddDays(i);
                bool inMonth = day.Month == shown.Month, isToday = day == DateTime.Today;
                var t = new TextBlock
                {
                    Text = day.Day.ToString(), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = isToday ? Brushes.White : inMonth ? Res("TextPrimary") : Res("TextTertiary"),
                    FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal
                };
                var cell = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Child = t, Background = isToday ? Res("Accent") : Brushes.Transparent };
                if (inMonth && day < DateTime.Today) t.Opacity = 0.75;
                grid.Children.Add(cell);
            }
        }
    }
}
