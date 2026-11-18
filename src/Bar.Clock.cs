using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassBar
{
    /// Clock pop-up (time with seconds, timer, stopwatch). Timer and stopwatch can also sit on the bar as their own cards.
    public partial class BarWindow
    {
        // ---------------- timer ----------------
        public TimeSpan TimerLength => TimeSpan.FromSeconds(Math.Max(1, Settings.TimerSeconds));
        TimeSpan timerLeftWhenPaused;
        DateTime timerEnds;
        public bool TimerRunning { get; private set; }
        public bool TimerStarted => TimerRunning || timerLeftWhenPaused != TimerLength;
        public TimeSpan TimerLeft => TimerRunning ? Max0(timerEnds - DateTime.Now) : timerLeftWhenPaused;
        static TimeSpan Max0(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

        // ---------------- stopwatch ----------------
        public readonly Stopwatch Sw = new();

        DispatcherTimer tick;
        ClockFlyout clockFly;
        System.Media.SoundPlayer alarm;

        void InitClock()
        {
            timerLeftWhenPaused = TimerLength;
            tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            tick.Tick += (s, e) => UpdateTimers();
            UpdateTimers();
        }

        /// Refresh everything that shows the timer or stopwatch; runs the 100 ms tick only while needed.
        public void UpdateTimers()
        {
            if (TimerRunning && TimerLeft <= TimeSpan.Zero) TimerFinished();

            var left = TimerLeft;
            TimerCardText.Text = Fmt(left);
            TimerOf.Text = " of " + Fmt(TimerLength);
            TimerPlayBtn.Content = TimerRunning ? "" : "";
            double frac = TimerLength.TotalSeconds > 0 ? left.TotalSeconds / TimerLength.TotalSeconds : 0;
            if (TimerBar.Parent is Grid g && g.ActualWidth > 0) TimerBar.Width = Math.Max(0, g.ActualWidth * (TimerStarted ? frac : 1));

            SwCardText.Text = FmtSw(Sw.Elapsed);
            SwPlayBtn.Content = Sw.IsRunning ? "" : "";

            if (clockFly?.IsVisible == true) clockFly.Update();
            bool need = TimerRunning || Sw.IsRunning || clockFly?.IsVisible == true;
            if (need && !tick.IsEnabled) tick.Start();
            else if (!need && tick.IsEnabled) tick.Stop();
        }

        public static string Fmt(TimeSpan t)
        {
            int secs = (int)Math.Ceiling(t.TotalSeconds - 0.001);
            var x = TimeSpan.FromSeconds(Math.Max(0, secs));
            return x.TotalHours >= 1 ? $"{(int)x.TotalHours}:{x.Minutes:00}:{x.Seconds:00}" : $"{x.Minutes}:{x.Seconds:00}";
        }

        public static string FmtSw(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds / 100}";

        public void TimerToggle()
        {
            StopAlarm();
            if (TimerRunning) { timerLeftWhenPaused = TimerLeft; TimerRunning = false; }
            else
            {
                if (timerLeftWhenPaused <= TimeSpan.Zero) timerLeftWhenPaused = TimerLength;
                timerEnds = DateTime.Now + timerLeftWhenPaused;
                TimerRunning = true;
            }
            UpdateTimers();
        }

        public void TimerReset()
        {
            StopAlarm();
            TimerRunning = false;
            timerLeftWhenPaused = TimerLength;
            UpdateTimers();
        }

        public void SetTimerLength(TimeSpan t)
        {
            if (t <= TimeSpan.Zero) return;
            Settings.TimerSeconds = (int)Math.Min(t.TotalSeconds, 24 * 3600 - 1);
            Settings.Save();
            TimerRunning = false;
            timerLeftWhenPaused = TimerLength;
            UpdateTimers();
        }

        void TimerFinished()
        {
            TimerRunning = false;
            timerLeftWhenPaused = TimeSpan.Zero;
            RevealNow();
            ShowToast($"⏰  Timer done ({Fmt(TimerLength)})", 8);
            try
            {
                var wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Alarm01.wav");
                if (File.Exists(wav)) { alarm ??= new System.Media.SoundPlayer(wav); alarm.Play(); }
                else System.Media.SystemSounds.Exclamation.Play();
            }
            catch { }
        }

        void StopAlarm() { try { alarm?.Stop(); } catch { } }

        public void SwToggle() { if (Sw.IsRunning) Sw.Stop(); else Sw.Start(); UpdateTimers(); }
        public void SwReset() { Sw.Reset(); UpdateTimers(); }

        void TimerPlay_Click(object s, RoutedEventArgs e) => TimerToggle();
        void TimerReset_Click(object s, RoutedEventArgs e) => TimerReset();
        void SwPlay_Click(object s, RoutedEventArgs e) => SwToggle();
        void SwReset_Click(object s, RoutedEventArgs e) => SwReset();

        void Clock_Click(object s, RoutedEventArgs e)
        {
            clockFly ??= Register(new ClockFlyout(this));
            clockFly.Toggle(ClockCard);
            UpdateTimers();
        }

        public bool CardOnBar(string name) => name == "TimerCard" ? Settings.TimerOnBar : Settings.StopwatchOnBar;
        public void PinCard(string name, bool on) => SetWidgetShown(name, on);
    }

    public class ClockFlyout : Flyout
    {
        readonly BarWindow bar;
        readonly TextBlock time = new() { FontSize = 46, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        readonly TextBlock secs = new() { FontSize = 24, FontWeight = FontWeights.Normal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 8) };
        readonly TextBlock date = new() { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
        readonly TextBlock timerText = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock timerOf = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        readonly Border timerFill = new() { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
        readonly Grid timerTrack = new() { Height = 4, Margin = new Thickness(0, 8, 0, 10) };
        readonly Button timerPlay, swPlay, timerPin, swPin;
        readonly TextBox custom;
        readonly TextBlock swText = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        readonly StackPanel laps = new();
        TimeSpan lastLap;
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");

        public ClockFlyout(BarWindow bar) : base("Clock", 380, 640)
        {
            this.bar = bar;
            date.Foreground = Res("TextSecondary");
            secs.Foreground = Res("TextSecondary");
            timerOf.Foreground = Res("TextSecondary");
            timerFill.Background = Res("Accent");
            timerTrack.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Res("CardBorder") });
            timerTrack.Children.Add(timerFill);

            var stack = new StackPanel { Margin = new Thickness(14, 0, 14, 14) };
            var clockRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            clockRow.Children.Add(time);
            clockRow.Children.Add(secs);
            stack.Children.Add(clockRow);
            stack.Children.Add(date);

            // ---- timer ----
            timerPlay = Round("", GlassTheme.Current.Accent, "Start / pause", bar.TimerToggle);
            var reset = SmallIcon("", "Reset", bar.TimerReset);
            timerPin = SmallIcon("", "", () => { bar.PinCard("TimerCard", !bar.CardOnBar("TimerCard")); Update(); });
            var tHead = Header("Timer", "#8FB8FF", timerPin);
            var tRow = new DockPanel();
            DockPanel.SetDock(timerPlay, Dock.Right); DockPanel.SetDock(reset, Dock.Right);
            tRow.Children.Add(timerPlay); tRow.Children.Add(reset);
            var tt = new StackPanel { Orientation = Orientation.Horizontal };
            tt.Children.Add(timerText);
            tt.Children.Add(new Border { Width = 8 });
            tt.Children.Add(timerOf);
            tRow.Children.Add(tt);

            var presets = new WrapPanel();
            foreach (var m in new[] { 1, 3, 5, 10, 15, 25, 45, 60 })
            {
                var b = Chip(m < 60 ? $"{m} min" : "1 h", () => bar.SetTimerLength(TimeSpan.FromMinutes(m)));
                presets.Children.Add(b);
            }
            custom = new TextBox { Style = (Style)Application.Current.Resources["PlainBox"], FontSize = 12.5, Width = 62, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Your own time: minutes, or mm:ss, then Enter" };
            var customHint = new TextBlock { Text = "mm:ss", FontSize = 12.5, Foreground = Res("TextTertiary"), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
            custom.TextChanged += (s, e) => customHint.Visibility = custom.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            custom.KeyDown += (s, e) =>
            {
                if (e.Key != Key.Enter) return;
                if (ParseTime(custom.Text) is TimeSpan t) { bar.SetTimerLength(t); custom.Text = ""; }
                e.Handled = true;
            };
            var cg = new Grid();
            cg.Children.Add(custom); cg.Children.Add(customHint);
            presets.Children.Add(new Border { Child = cg, Height = 28, Padding = new Thickness(10, 0, 8, 0), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(14), Background = Res("ControlBg"), BorderBrush = Res("ControlBorder"), BorderThickness = new Thickness(1) });

            var tBox = Section();
            var ts = (StackPanel)tBox.Child;
            ts.Children.Add(tHead); ts.Children.Add(tRow); ts.Children.Add(timerTrack); ts.Children.Add(presets);
            stack.Children.Add(tBox);

            // ---- stopwatch ----
            swPlay = Round("", "#3DBE6E", "Start / pause", bar.SwToggle);
            var swReset = SmallIcon("", "Reset", () => { bar.SwReset(); laps.Children.Clear(); lastLap = TimeSpan.Zero; });
            var lap = SmallIcon("", "Lap", AddLap);
            swPin = SmallIcon("", "", () => { bar.PinCard("StopwatchCard", !bar.CardOnBar("StopwatchCard")); Update(); });
            var sHead = Header("Stopwatch", "#5BD68A", swPin);
            var sRow = new DockPanel();
            DockPanel.SetDock(swPlay, Dock.Right); DockPanel.SetDock(swReset, Dock.Right); DockPanel.SetDock(lap, Dock.Right);
            sRow.Children.Add(swPlay); sRow.Children.Add(swReset); sRow.Children.Add(lap);
            sRow.Children.Add(swText);
            var sBox = Section();
            var ss = (StackPanel)sBox.Child;
            ss.Children.Add(sHead); ss.Children.Add(sRow);
            ss.Children.Add(new ScrollViewer { Content = laps, MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 0) });
            stack.Children.Add(sBox);

            Body.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            IsVisibleChanged += (s, e) => bar.UpdateTimers();
        }

        void AddLap()
        {
            if (!bar.Sw.IsRunning && bar.Sw.Elapsed == TimeSpan.Zero) return;
            var now = bar.Sw.Elapsed;
            var row = new DockPanel { Margin = new Thickness(0, 2, 4, 2) };
            var right = new TextBlock { Text = BarWindow.FmtSw(now), FontSize = 12, Foreground = Res("TextSecondary") };
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
            row.Children.Add(new TextBlock { Text = $"Lap {laps.Children.Count + 1}   +{BarWindow.FmtSw(now - lastLap)}", FontSize = 12 });
            laps.Children.Insert(0, row);
            lastLap = now;
        }

        static TimeSpan? ParseTime(string s)
        {
            s = s.Trim().Replace(',', '.');
            var parts = s.Split(':');
            try
            {
                if (parts.Length == 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var min)) return TimeSpan.FromMinutes(min);
                if (parts.Length == 2) return new TimeSpan(0, int.Parse(parts[0]), int.Parse(parts[1]));
                if (parts.Length == 3) return new TimeSpan(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
            }
            catch { }
            return null;
        }

        public void Update()
        {
            var now = DateTime.Now;
            time.Text = now.ToString("HH:mm");
            secs.Text = now.ToString("ss");
            date.Text = now.ToString("dddd d MMMM yyyy", En) + $"  ·  week {ISOWeek.GetWeekOfYear(now)}";
            var left = bar.TimerLeft;
            timerText.Text = BarWindow.Fmt(left);
            timerOf.Text = "of " + BarWindow.Fmt(bar.TimerLength);
            double frac = bar.TimerLength.TotalSeconds > 0 ? left.TotalSeconds / bar.TimerLength.TotalSeconds : 0;
            if (timerTrack.ActualWidth > 0) timerFill.Width = timerTrack.ActualWidth * (bar.TimerStarted ? frac : 1);
            timerPlay.Content = bar.TimerRunning ? "" : "";
            swText.Text = BarWindow.FmtSw(bar.Sw.Elapsed);
            swPlay.Content = bar.Sw.IsRunning ? "" : "";
            SetPin(timerPin, bar.CardOnBar("TimerCard"));
            SetPin(swPin, bar.CardOnBar("StopwatchCard"));
        }

        static void SetPin(Button b, bool on)
        {
            b.Content = on ? "" : "";
            b.ToolTip = on ? "Remove from the bar" : "Show on the bar";
            b.Foreground = on ? Res("Accent") : Res("TextSecondary");
        }

        static Border Section() => new()
        {
            CornerRadius = new CornerRadius(14), Background = Res("CardBg"), BorderBrush = Res("CardBorder"), BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 10, 12), Margin = new Thickness(0, 12, 0, 0), Child = new StackPanel()
        };

        static DockPanel Header(string text, string color, Button pin)
        {
            var d = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(pin, Dock.Right);
            d.Children.Add(pin);
            d.Children.Add(new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)new BrushConverter().ConvertFromString(color) });
            return d;
        }

        static Button Round(string glyph, string color, string tip, Action click)
        {
            var b = new Button
            {
                Style = (Style)BarWindow.Instance.FindResource("RoundBtn"), Content = glyph, ToolTip = tip, Width = 40, Height = 40,
                Background = (Brush)new BrushConverter().ConvertFromString(color), Foreground = Brushes.White, Margin = new Thickness(6, 0, 0, 0)
            };
            b.Click += (s, e) => click();
            return b;
        }

        static Button Chip(string text, Action click)
        {
            var b = new Button { Style = (Style)Application.Current.Resources["PillButton"], Content = text, Padding = new Thickness(11, 4, 11, 4), Margin = new Thickness(0, 0, 6, 6), FontSize = 12 };
            b.Click += (s, e) => click();
            return b;
        }
    }
}
